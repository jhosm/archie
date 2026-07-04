using Npgsql;

namespace Babelstone.Orchestrator.Saga;

/// <summary>
/// Appends to the immutable <c>saga_transition</c> history (ADR-IC-003 §F2: the transition
/// trail is the DORA/PSD2 audit evidence). One row per accepted advance, written in the
/// SAME transaction as the <c>saga_state</c> move and the inbox dedup row — so the history
/// and the state never diverge. Append-only: this writer only ever INSERTs (the runtime
/// role is denied UPDATE/DELETE on the table).
/// </summary>
public sealed class SagaTransitionLog
{
    /// <summary>
    /// Record one accepted transition. Carries only structural, PII-free fields (ADR-PC-004
    /// §P2): the from/to business states, the triggering event type, the causation
    /// message id (ADR-IC-003 §P7), and an optional operational-tier note. NEVER a
    /// NIF/IBAN/amount.
    /// </summary>
    public async Task AppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        string fromState,
        string toState,
        string eventType,
        Guid? messageId,
        string? note,
        CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO saga_transition (process_id, from_state, to_state, event_type, message_id, note)
            VALUES (@process_id, @from_state, @to_state, @event_type, @message_id, @note);
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("process_id", processId);
        // The from/to states ARE the wire strings the saga owns (ADR-IC-018 §D3) — written verbatim.
        command.Parameters.AddWithValue("from_state", fromState);
        command.Parameters.AddWithValue("to_state", toState);
        command.Parameters.AddWithValue("event_type", eventType);
        command.Parameters.AddWithValue("message_id", (object?)messageId ?? DBNull.Value);
        command.Parameters.AddWithValue("note", (object?)note ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Count the immutable transition rows that LANDED this saga in <paramref name="state"/> — i.e. the
    /// number of times the saga has ENTERED that state. The transition trail (ADR-IC-003 §F2) is the saga's
    /// only per-instance memory beyond its current <c>state</c> column, so a count over it is how a
    /// (state, event)-keyed table reads history without carrying a counter on the saga row. Used by the
    /// indeterminate-clearance reissue BUDGET (bd babelstone-rq3e): the count of AWAIT_CORE_CLEARANCE
    /// entries is the saga's clearance-cycle count, which bounds the RETRY_PERMITTED reissue loop. Reads
    /// only the structural <c>to_state</c> column (PII-free, ADR-PC-004 §P2).
    /// </summary>
    /// <remarks>
    /// Runs on the SAME connection + transaction as the advance, so it reads under the <c>FOR UPDATE</c>
    /// row lock the advance already holds on the <c>saga_state</c> row (<see cref="SagaStateStore.LoadAsync"/>):
    /// concurrent advancers on the same instance are serialised, so the count is consistent with the move
    /// about to be made. The budget it feeds is defense-in-depth, and the optimistic-concurrency version
    /// guard owns correctness on the rare cross-instance race, so a count is never relied on for safety —
    /// only for liveness.
    /// </remarks>
    public async Task<int> CountEntriesIntoStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        string state,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT COUNT(*) FROM saga_transition
            WHERE process_id = @process_id AND to_state = @to_state;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("process_id", processId);
        command.Parameters.AddWithValue("to_state", state);
        // Hard unbox: COUNT(*) is non-null bigint in PostgreSQL, so the scalar is always a boxed
        // Int64 — any other shape is schema/query drift and must throw, never be coerced. The
        // narrowing to int is safe: a per-(process, state) transition count near int.MaxValue is
        // structurally impossible (the reissue budget escalates after a handful of entries).
        return (int)(long)(await command.ExecuteScalarAsync(ct))!;
    }
}
