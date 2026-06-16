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
        string initialState,
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
        // The state IS the persisted wire string (ADR-IC-018 §D3): the saga owns its vocabulary,
        // the substrate persists it verbatim — no central enum round-trip.
        command.Parameters.AddWithValue("state", initialState);
        command.Parameters.AddWithValue("correlation_id", (object?)correlationId ?? DBNull.Value);

        var inserted = await command.ExecuteNonQueryAsync(ct) == 1;
        return inserted;
    }

    /// <summary>
    /// Start a saga the EDGE owns (I.1, ADR-IC-006 §P4 / Document 05 §Step 0): the same INSERT as
    /// <see cref="TryStartAsync"/> but ALSO persisting the client-facing <paramref name="publicProcessId"/>
    /// (the <c>PROC-…</c> reference the edge returns + the SSE <c>stream_url</c> is keyed on) and the
    /// <paramref name="owningClientId"/> the SSE read enforces ownership against. Idempotent on the
    /// process id exactly like <see cref="TryStartAsync"/> — a duplicate start collides on
    /// <c>saga_state_pkey</c> and returns false. Runs inside the caller's transaction so the start row,
    /// the first transition, and the emitted commands commit atomically (the edge is the producer; it
    /// puts NOTHING on the durable bus). Both extra columns are structural references, never PII
    /// (ADR-PC-004 §P2).
    /// </summary>
    /// <returns>True if this call created the saga; false if it already existed.</returns>
    public async Task<bool> TryStartWithEdgeIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        string sagaType,
        string initialState,
        Guid? correlationId,
        string publicProcessId,
        string owningClientId,
        CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO saga_state (process_id, saga_type, state, version, correlation_id, public_process_id, owning_client_id)
            VALUES (@process_id, @saga_type, @state, 0, @correlation_id, @public_process_id, @owning_client_id)
            ON CONFLICT (process_id) DO NOTHING;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("process_id", processId);
        command.Parameters.AddWithValue("saga_type", sagaType);
        command.Parameters.AddWithValue("state", initialState);
        command.Parameters.AddWithValue("correlation_id", (object?)correlationId ?? DBNull.Value);
        command.Parameters.AddWithValue("public_process_id", publicProcessId);
        command.Parameters.AddWithValue("owning_client_id", owningClientId);

        return await command.ExecuteNonQueryAsync(ct) == 1;
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
            SELECT process_id, saga_type, state, version, correlation_id, public_process_id, owning_client_id
            FROM saga_state
            WHERE process_id = @process_id
            FOR UPDATE;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("process_id", processId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await ReadInstanceAsync(reader, ct);
    }

    /// <summary>
    /// Resolve the saga for a client-facing <paramref name="publicProcessId"/> (the <c>PROC-…</c>
    /// reference the SSE <c>stream_url</c> carries), or null if no saga was minted for it. The SSE
    /// read uses this to find the saga and then enforces <see cref="SagaInstance.OwningClientId"/>
    /// against the requester (ADR-IC-006 §P4). A plain read (NO <c>FOR UPDATE</c>): the SSE path
    /// observes the saga's state, it never advances it, so it must not take the advancer's row lock.
    /// </summary>
    public async Task<SagaInstance?> LoadByPublicIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string publicProcessId,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT process_id, saga_type, state, version, correlation_id, public_process_id, owning_client_id
            FROM saga_state
            WHERE public_process_id = @public_process_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("public_process_id", publicProcessId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await ReadInstanceAsync(reader, ct);
    }

    private static async Task<SagaInstance?> ReadInstanceAsync(NpgsqlDataReader reader, CancellationToken ct)
    {
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new SagaInstance(
            ProcessId: reader.GetGuid(0),
            SagaType: reader.GetString(1),
            // The persisted state IS the wire string the saga owns (ADR-IC-018 §D3) — read verbatim.
            State: reader.GetString(2),
            Version: reader.GetInt64(3),
            CorrelationId: reader.IsDBNull(4) ? null : reader.GetGuid(4),
            PublicProcessId: reader.IsDBNull(5) ? null : reader.GetString(5),
            OwningClientId: reader.IsDBNull(6) ? null : reader.GetString(6));
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
        string next,
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
        command.Parameters.AddWithValue("next", next);

        return await command.ExecuteNonQueryAsync(ct) == 1;
    }
}
