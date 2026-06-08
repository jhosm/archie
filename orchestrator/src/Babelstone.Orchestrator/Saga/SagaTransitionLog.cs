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
        SagaState fromState,
        SagaState toState,
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
        command.Parameters.AddWithValue("from_state", SagaStateNames.ToName(fromState));
        command.Parameters.AddWithValue("to_state", SagaStateNames.ToName(toState));
        command.Parameters.AddWithValue("event_type", eventType);
        command.Parameters.AddWithValue("message_id", (object?)messageId ?? DBNull.Value);
        command.Parameters.AddWithValue("note", (object?)note ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }
}
