using Npgsql;

namespace Babelstone.Orchestrator.Saga;

/// <summary>
/// The persistence boundary for the saga aggregate (ADR-IC-003 §S2: "saga state is a table
/// in the application database … queryable, surfaced by the operations console API without
/// a vendor adapter"). Hand-rolled Npgsql (ADR-PC-010): no ORM, no framework owns the
/// table. Every write takes the caller's open connection + transaction so the state move,
/// the transition-history row, and the inbox dedup row commit ATOMICALLY in one transaction
/// — the effectively-once guarantee.
/// </summary>
/// <remarks>
/// <b>Optimistic concurrency (ADR-IC-003 §Residual "Concurrent writer race", §P1):</b>
/// <see cref="TryAdvanceAsync"/> updates with a <c>WHERE process_id = ? AND version = ?</c>
/// predicate and bumps the version. Two orchestrator instances that observe the same event
/// both attempt the move; the database serialises them — one matches the row and wins, the
/// loser matches ZERO rows (<see cref="TryAdvanceAsync"/> returns false) and re-reads. The
/// losing writer never clobbers.
/// </remarks>
public sealed class SagaStateStore
{
    /// <summary>
    /// Start a saga: INSERT the <c>saga_state</c> row in <paramref name="initialState"/> and
    /// append its first transition (a self-transition from the initial state, recording the
    /// creation). Idempotent on the process id: a duplicate start collides on
    /// <c>saga_state_pkey</c> and returns false (the saga already exists), so a redelivered
    /// "start" event never resets a running saga. Runs inside the caller's transaction.
    /// </summary>
    /// <returns>True if this call created the saga; false if it already existed.</returns>
    public async Task<bool> TryStartAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        string sagaType,
        SagaState initialState,
        Guid? correlationId,
        CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO saga_state (process_id, saga_type, state, version, correlation_id)
            VALUES (@process_id, @saga_type, @state, 0, @correlation_id)
            ON CONFLICT (process_id) DO NOTHING;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("process_id", processId);
        command.Parameters.AddWithValue("saga_type", sagaType);
        command.Parameters.AddWithValue("state", SagaStateNames.ToName(initialState));
        command.Parameters.AddWithValue("correlation_id", (object?)correlationId ?? DBNull.Value);

        var inserted = await command.ExecuteNonQueryAsync(ct) == 1;
        return inserted;
    }

    /// <summary>
    /// Load the current saga row, or null if no saga with that process id exists. A
    /// <c>FOR UPDATE</c> row lock serialises concurrent advancers on the same instance — the
    /// belt to the optimistic-concurrency braces, so the common single-process path takes
    /// the lock and the rare cross-instance race still falls back to the version predicate.
    /// </summary>
    public async Task<SagaInstance?> LoadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT process_id, saga_type, state, version, correlation_id
            FROM saga_state
            WHERE process_id = @process_id
            FOR UPDATE;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("process_id", processId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new SagaInstance(
            ProcessId: reader.GetGuid(0),
            SagaType: reader.GetString(1),
            State: SagaStateNames.FromName(reader.GetString(2)),
            Version: reader.GetInt64(3),
            CorrelationId: reader.IsDBNull(4) ? null : reader.GetGuid(4));
    }

    /// <summary>
    /// Advance the saga from <paramref name="expectedVersion"/> to
    /// <paramref name="next"/>, bumping the version (ADR-IC-003 §P1). The
    /// <c>WHERE process_id = ? AND version = ?</c> predicate is the concurrent-writer guard
    /// (§Residual "Concurrent writer race"): if another writer already advanced the row, this
    /// matches zero rows and returns false — the caller re-reads and retries, never clobbers.
    /// </summary>
    /// <returns>True if this writer won the move; false if the version was stale.</returns>
    public async Task<bool> TryAdvanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        long expectedVersion,
        SagaState next,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE saga_state
            SET state = @next, version = version + 1, updated_at = clock_timestamp()
            WHERE process_id = @process_id AND version = @expected_version;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("process_id", processId);
        command.Parameters.AddWithValue("expected_version", expectedVersion);
        command.Parameters.AddWithValue("next", SagaStateNames.ToName(next));

        return await command.ExecuteNonQueryAsync(ct) == 1;
    }
}
