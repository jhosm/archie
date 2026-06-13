using Babelstone.Families.TermDeposit.Application.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using EngineMigrationRunner = Babelstone.EventStore.Migrations.MigrationRunner;
using FamilyMigrationRunner = Babelstone.Families.TermDeposit.Application.Migrations.MigrationRunner;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// Applies the ENGINE then the TERM-DEPOSIT FAMILY migration sets against a real PostgreSQL and
/// asserts the relocated read model is materialised, granted to the engine runtime role, and applied
/// idempotently (mirrors the engine's <c>MigrationSchemaIntegrationTests</c>). This is the relocated
/// home of the read-model schema/role assertions: <c>read_model.deposits</c> is now FAMILY-OWNED
/// (ADR-PC-021 family-owned ownership), shipped in the family's own migration set on the same Postgres
/// tier as the engine event store (ADR-IC-005 §S1), under its own
/// <c>schema_migrations_term_deposit</c> ledger and advisory lock — distinct from the engine's so the
/// two independently-versioned sets coexist on one cluster. The engine set runs FIRST: it creates the
/// <c>babelstone_engine</c> role the read model GRANTs on (engine migration 0002), the hard ordering
/// the family migration's fail-loud guard depends on. Tagged Integration so the default (Docker-free)
/// lane skips it; the integration lane runs it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReadModelMigrationSchemaIntegrationTests : IAsyncLifetime
{
    // Match the dev stack's pinned major version (infra/compose.yaml — the ADR-PC-001 event store,
    // latest stable PG 18) so the schema and §P3 role behaviour are tested against the PostgreSQL the
    // engine actually deploys on.
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        // Engine event-store schema first (creates the babelstone_engine role + the append-only
        // envelope), then the family read-model schema — engine-before-family ordering.
        await new EngineMigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    [Fact]
    public async Task Family_apply_is_idempotent_and_ledgers_each_migration_in_its_own_ledger()
    {
        var runner = new FamilyMigrationRunner(ConnectionString);

        var firstRun = await runner.ApplyAsync();
        Assert.Equal(MigrationSet.All.Length, firstRun.Count);

        // Idempotency: a second family ApplyAsync returns 0 applied (nothing pending).
        var secondRun = await runner.ApplyAsync();
        Assert.Empty(secondRun);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        // The family records into its OWN ledger, distinct from the engine's schema_migrations.
        var ledgered = (long)(await new NpgsqlCommand(
            "SELECT count(*) FROM schema_migrations_term_deposit;", connection).ExecuteScalarAsync())!;
        Assert.Equal(MigrationSet.All.Length, ledgered);
    }

    [Fact]
    public async Task Creates_the_read_model_deposits_table_and_maturity_index()
    {
        await new FamilyMigrationRunner(ConnectionString).ApplyAsync();

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        // read_model.deposits exists in the dedicated read_model schema (ADR-IC-005 §P1).
        var tableCount = (long)(await new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.tables "
            + "WHERE table_schema = 'read_model' AND table_name = 'deposits';", connection).ExecuteScalarAsync())!;
        Assert.Equal(1L, tableCount);

        // The maturity range-scan index (ADR-IC-005 upcoming_maturities access pattern).
        await using var indexCommand = new NpgsqlCommand(
            "SELECT count(*) FROM pg_indexes WHERE indexname = @name;", connection);
        indexCommand.Parameters.AddWithValue("name", "deposits_maturity_date_idx");
        Assert.Equal(1L, (long)(await indexCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Engine_role_can_select_insert_update_delete_and_truncate_the_read_model()
    {
        await new FamilyMigrationRunner(ConnectionString).ApplyAsync();

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        // Drop superuser bypass: act as the engine's runtime role. The read model is a REBUILDABLE
        // cache (ADR-IC-005 §P5), so UNLIKE the append-only events log the engine role gets the full
        // SELECT/INSERT/UPDATE/DELETE/TRUNCATE envelope (the §P2 UPSERT needs UPDATE; rebuild needs
        // DELETE/TRUNCATE). USAGE on the read_model schema is granted too.
        await new NpgsqlCommand("SET ROLE babelstone_engine;", connection).ExecuteNonQueryAsync();

        var streamId = Guid.NewGuid();
        await InsertDepositAsync(connection, streamId);

        // SELECT is granted; the row is there.
        var count = (long)(await new NpgsqlCommand(
            $"SELECT count(*) FROM read_model.deposits WHERE stream_id = '{streamId}';", connection).ExecuteScalarAsync())!;
        Assert.Equal(1L, count);

        // UPDATE is granted (the §P2 monotonicity UPSERT path).
        await new NpgsqlCommand(
            $"UPDATE read_model.deposits SET lifecycle = 'Matured' WHERE stream_id = '{streamId}';", connection)
            .ExecuteNonQueryAsync();

        // DELETE is granted.
        await new NpgsqlCommand(
            $"DELETE FROM read_model.deposits WHERE stream_id = '{streamId}';", connection).ExecuteNonQueryAsync();

        // TRUNCATE is granted (the §P5 clean-rebuild path).
        await new NpgsqlCommand("TRUNCATE read_model.deposits;", connection).ExecuteNonQueryAsync();
    }

    private static async Task InsertDepositAsync(NpgsqlConnection connection, Guid streamId)
    {
        const string sql = """
            INSERT INTO read_model.deposits (
                stream_id, sor, principal_cents, tan_basis_points, rate_sheet_version_id,
                product_code, term_days, start_date, maturity_date, interest_variant,
                auto_renewal_policy, payment_period_months, lifecycle,
                accrued_gross_interest_cents, withholding_to_date_cents, net_interest_cents,
                total_payout_cents, coupons_paid, detail, last_sequence, last_updated)
            VALUES (
                @stream_id, 'engine', 1000000, 300, 'pt-deposits-2026.1',
                'dpz_pt_12m_juros_venc', 365, DATE '2026-01-15', DATE '2027-01-15', 'AT_MATURITY',
                'NONE', 0, 'Active',
                0, 0, 0,
                1000000, 0, @detail, 0, now());
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("stream_id", streamId);
        command.Parameters.AddWithValue("detail", new byte[] { 0x01, 0x02 });
        await command.ExecuteNonQueryAsync();
    }
}
