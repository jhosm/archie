using Npgsql;

namespace Babelstone.EventStore.Migrations;

/// <summary>
/// Applies the forward-only <see cref="MigrationSet"/> against a PostgreSQL database.
/// Thin and hand-rolled (ADR-PC-010): no migration framework owns the schema. It
/// connects as the migration role (ADR-PC-001 §P3) — the role that holds the
/// DDL and UPDATE/DELETE privileges the engine's runtime role is denied.
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

    /// <summary>
    /// Applies every migration not yet recorded in <c>schema_migrations</c>, in
    /// ascending version order. Each migration runs in its own transaction together
    /// with its ledger insert, so a failure leaves the database at the last fully
    /// applied version. Idempotent: a second call with nothing pending is a no-op.
    /// </summary>
    /// <returns>The migrations applied by this call, in the order applied.</returns>
    public async Task<IReadOnlyList<Migration>> ApplyAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

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
