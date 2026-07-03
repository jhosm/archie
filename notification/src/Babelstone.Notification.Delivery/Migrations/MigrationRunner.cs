using Npgsql;

namespace Babelstone.Notification.Delivery.Migrations;

/// <summary>
/// Applies the forward-only <see cref="MigrationSet"/> against a PostgreSQL database — the
/// notification delivery estate's OWN migration series (ADR-IC-011; lifted from the engine's
/// <c>Babelstone.EventStore.Migrations.MigrationRunner</c> via the orchestrator's and the lifecycle
/// driver's copies). Thin and hand-rolled (ADR-PC-010): no migration framework owns the schema. It
/// connects as the migration role (ADR-PC-001) — the role that holds the DDL privileges the
/// delivery estate's runtime role (<c>babelstone_notification</c>) is denied.
/// </summary>
public sealed class MigrationRunner(string connectionString)
{
    private const string LedgerDdl = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version    BIGINT      NOT NULL PRIMARY KEY,
            name       TEXT        NOT NULL,
            applied_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp()
        );
        """;

    // A stable, arbitrary 64-bit key naming this runner's session advisory lock. Two runners that start
    // concurrently (overlapping deploys, an app boot racing CI) would otherwise both read the ledger,
    // both apply migration N, and collide. The lock serialises them: the second waits. The constant is
    // this component's own, distinct from the other Babelstone migration runners', so a notification
    // deploy against a shared cluster does not block on an unrelated migration set.
    private const long MigrationLockKey = 6193472850317264089;

    /// <summary>
    /// Applies every migration not yet recorded in <c>schema_migrations</c>, in ascending version order.
    /// Each migration runs in its own transaction together with its ledger insert, so a failure leaves
    /// the database at the last fully applied version. Idempotent: a second call with nothing pending is
    /// a no-op.
    /// </summary>
    /// <returns>The migrations applied by this call, in the order applied.</returns>
    public async Task<IReadOnlyList<Migration>> ApplyAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // Session-level advisory lock: held by this connection until unlocked below (or released when
        // the connection closes). A concurrent runner blocks here instead of racing into a half-applied
        // collision.
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
                    "INSERT INTO schema_migrations (version, name) VALUES (@version, @name);", connection, tx))
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
        await using var command = new NpgsqlCommand("SELECT version FROM schema_migrations;", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            versions.Add(reader.GetInt64(0));
        }

        return versions;
    }
}
