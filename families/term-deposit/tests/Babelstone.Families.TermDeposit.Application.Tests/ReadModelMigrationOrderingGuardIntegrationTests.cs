using Babelstone.Families.TermDeposit.Application.Migrations;
using Babelstone.TestFixtures;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using FamilyMigrationRunner = Babelstone.Families.TermDeposit.Application.Migrations.MigrationRunner;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// End-to-end guard for the hard engine-before-family migration ordering. The family read-model
/// migration <c>0001_read_model.sql</c> GRANTs on the <c>babelstone_engine</c> runtime role that the
/// ENGINE migration set (<c>0002_append_only_role.sql</c>) creates, so the engine set MUST run first
/// (ADR-IC-005 §S1 — same Postgres tier; ADR-PC-021 family-owned ownership). To fail loud rather than
/// cryptically at a later GRANT, <c>0001_read_model.sql</c> opens with a <c>DO $$ … RAISE EXCEPTION …
/// $$</c> guard that checks <c>pg_roles</c> for the role up front.
///
/// The sibling <c>ReadModelMigrationSchemaIntegrationTests</c> only exercises the HAPPY path
/// (engine-then-family) and the SQL-text fitness check only string-matches "RAISE EXCEPTION"; neither
/// proves the guard actually fires. This test closes that gap: it stands up a FRESH PostgreSQL where
/// the engine migrations were NEVER run (so the <c>babelstone_engine</c> role is absent) and asserts the
/// family runner throws with a message that names the ordering contract.
///
/// Its own container — distinct from the engine-then-family fixture — because the whole point is a
/// database WITHOUT the engine schema/role. Tagged Integration so the default (Docker-free) lane skips
/// it; the integration lane runs it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReadModelMigrationOrderingGuardIntegrationTests : IAsyncLifetime
{
    // Match the dev stack's pinned major version (infra/compose.yaml) so the guard is exercised
    // against the PostgreSQL the engine actually deploys on. Deliberately NO engine migration run in
    // InitializeAsync: the role must be absent for the guard to fire.
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync() => await _pg.GatedStartAsync();

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    [Fact]
    public async Task Family_migration_applied_before_engine_fails_loud_with_ordering_guard()
    {
        // The babelstone_engine role is absent (engine migrations never ran on this fresh container),
        // so the family runner's first migration must raise the fail-loud ordering guard rather than
        // crash cryptically deep in a later GRANT.
        var runner = new FamilyMigrationRunner(ConnectionString);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => runner.ApplyAsync());

        // Assert on a stable substring of the guard's RAISE EXCEPTION text (0001_read_model.sql): it
        // names the absent role and the engine-before-family ordering contract.
        Assert.Contains("babelstone_engine role is absent", ex.Message);
        Assert.Contains("run the ENGINE event-store migrations", ex.Message);

        // And the guard fired BEFORE any family DDL landed: read_model.deposits must not exist, since
        // the guard aborts the migration's transaction up front.
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        var tableCount = (long)(await new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.tables "
            + "WHERE table_schema = 'read_model' AND table_name = 'deposits';", connection).ExecuteScalarAsync())!;
        Assert.Equal(0L, tableCount);
    }
}
