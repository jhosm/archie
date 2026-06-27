using Npgsql;

namespace Babelstone.EventStore;

/// <summary>
/// The original outcome of a command the engine has already applied — what a replay of the
/// same command id returns (ADR-PC-029 slot 4). Read back from the <c>command_dedup</c> ledger
/// the append transaction wrote (migration 0015).
/// </summary>
/// <param name="CommandId">The caller's deterministic command id (the dedup key).</param>
/// <param name="StreamId">The aggregate the command opened/mutated — an OUTPUT for a
/// constitution, so the replay reads it back from here rather than from the retried request.</param>
/// <param name="CommitSequence">The per-stream head version the append reached (the
/// ADR-IC-005 read-your-writes token the original response carried).</param>
public sealed record CommandReceipt(Guid CommandId, Guid StreamId, long CommitSequence);

/// <summary>
/// The READ side of the command-ingress idempotency ledger (ADR-PC-029 slot 4). A command
/// endpoint consults this BEFORE any side effect (decide / settle / append) so a replay of an
/// already-applied command short-circuits to the original outcome without re-running the
/// decider or re-settling. The authoritative, crash-atomic guarantee is the in-transaction
/// <c>command_dedup</c> INSERT inside <see cref="IEventStore.AppendAsync"/> (which raises
/// <see cref="DuplicateCommandException"/> on a concurrent racer that slipped past this read);
/// this read is the fast path that keeps the common sequential-retry off the write path.
/// </summary>
public interface ICommandLog
{
    /// <summary>
    /// Returns the receipt for an already-applied <paramref name="commandId"/>, or
    /// <c>null</c> if the command has not been applied (or its receipt has aged out of the
    /// retention window). A non-null result means "this is a replay — return the original".
    /// </summary>
    Task<CommandReceipt?> TryGetAsync(Guid commandId, CancellationToken ct = default);
}

/// <summary>
/// The PostgreSQL read side of the <c>command_dedup</c> ledger (migration 0015). The ledger is
/// WRITTEN by <see cref="PostgresEventStore.AppendAsync"/> inside the §P2 append transaction
/// (so a receipt and its events are atomic); this type only READs it for the pre-check. Both
/// live in this assembly, the single owner of the engine's storage tables.
/// </summary>
public sealed class PostgresCommandLog(string connectionString) : ICommandLog
{
    public async Task<CommandReceipt?> TryGetAsync(Guid commandId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT command_id, stream_id, commit_sequence
            FROM command_dedup
            WHERE command_id = @command_id;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("command_id", commandId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new CommandReceipt(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2));
    }
}
