using Npgsql;

namespace Babelstone.Families.PersonalLoan.Application.Migrations;

/// <summary>
/// Applies the personal_loan family's forward-only <see cref="MigrationSet"/> against a
/// PostgreSQL database (lifted from the engine's
/// <c>Babelstone.EventStore.Migrations.MigrationRunner</c>, mirroring the term-deposit family runner).
/// Thin and hand-rolled (ADR-PC-010): no migration framework owns the schema. It connects as the
/// migration role (ADR-PC-001 §P3) — the role that holds the DDL privileges the engine's runtime role
/// is denied.
///
/// CRITICAL deviation from the engine and orchestrator runners: the family read model lives
/// on the SAME PostgreSQL tier as the engine event store (ADR-IC-005 §S1 — zero incremental
/// infrastructure), so this runner shares a database with the engine's <see cref="MigrationRunner"/>
/// but must NOT share its ledger. It records into a DISTINCT ledger table
/// (<c>schema_migrations_personal_loan</c>) and serialises on a DISTINCT advisory-lock key, so
/// an engine deploy and a family deploy against the same cluster neither ledger each other's
/// migrations nor block on each other's unrelated set. A hard ORDERING dependency still holds:
/// the engine migrations must run first — this set's <c>0001_installment_calendar.sql</c> grants on
/// the <c>babelstone_engine</c> role that engine migration <c>0002_append_only_role.sql</c> creates,
/// and fails loud if that role is absent.
/// </summary>
public sealed class MigrationRunner(string connectionString)
{
    // DISTINCT ledger from the engine's `schema_migrations` AND the term-deposit family's
    // `schema_migrations_term_deposit`: every independently-versioned set on the shared tier
    // (ADR-IC-005 §S1) keeps its own applied history, or a shared ledger would interleave sets that
    // each start at 0001 — duplicate version PKs and cross-set version collisions.
    private const string LedgerDdl = """
        CREATE TABLE IF NOT EXISTS schema_migrations_personal_loan (
            version    BIGINT      NOT NULL PRIMARY KEY,
            name       TEXT        NOT NULL,
            applied_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp()
        );
        """;

    // A stable, arbitrary 64-bit key naming THIS runner's session advisory lock. Two runners that
    // start concurrently (overlapping deploys, an app boot racing CI) would otherwise both read the
    // ledger, both apply migration N, and collide. The lock serialises them: the second waits. A
    // DIFFERENT constant from the engine runner's (3937070637541916881), the orchestrator runner's
    // (6044827715031892057), and the term-deposit family runner's (5821934760118327449), so a
    // personal_loan deploy and any of those against the same cluster do not block on each other's
    // unrelated migration set.
    private const long MigrationLockKey = 7103928465120937461;

    /// <summary>
    /// Applies every migration not yet recorded in <c>schema_migrations_personal_loan</c>, in
    /// ascending version order. Each migration runs in its own transaction together with its
    /// ledger insert, so a failure leaves the database at the last fully applied version.
    /// Idempotent: a second call with nothing pending is a no-op.
    /// </summary>
    /// <returns>The migrations applied by this call, in the order applied.</returns>
    public async Task<IReadOnlyList<Migration>> ApplyAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // Session-level advisory lock: held by this connection until unlocked below (or
        // released when the connection closes). A concurrent runner blocks here instead of
        // racing into a half-applied collision.
        await ExecuteAsync(connection, $"SELECT pg_advisory_lock({MigrationLockKey});", ct);
        try
        {
            await ExecuteAsync(connection, LedgerDdl, ct);
            var applied = await LoadAppliedVersionsAsync(connection, ct);

            var justApplied = new List<Migration>();
            foreach (var migration in MigrationSet.All)
            {
                if (applied.Contains(migration.Version))
                {
                    continue;
                }

                await using var tx = await connection.BeginTransactionAsync(ct);
                await ExecuteAsync(connection, migration.Sql, ct, tx);

                await using (var record = new NpgsqlCommand(
                    "INSERT INTO schema_migrations_personal_loan (version, name) VALUES (@version, @name);", connection, tx))
                {
                    record.Parameters.AddWithValue("version", migration.Version);
                    record.Parameters.AddWithValue("name", migration.Name);
                    await record.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                justApplied.Add(migration);
            }

            return justApplied;
        }
        finally
        {
            await ExecuteAsync(connection, $"SELECT pg_advisory_unlock({MigrationLockKey});", ct);
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, string sql, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<HashSet<long>> LoadAppliedVersionsAsync(
        NpgsqlConnection connection, CancellationToken ct)
    {
        var versions = new HashSet<long>();
        await using var command = new NpgsqlCommand("SELECT version FROM schema_migrations_personal_loan;", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            versions.Add(reader.GetInt64(0));
        }

        return versions;
    }
}
