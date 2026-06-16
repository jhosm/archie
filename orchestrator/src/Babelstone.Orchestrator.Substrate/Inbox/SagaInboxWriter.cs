using Npgsql;

namespace Babelstone.Orchestrator.Inbox;

/// <summary>
/// The substrate's <c>inbox</c> dedup-row writer (ADR-IC-003 §P1 — inbox deduplication for saga event
/// consumption). The <c>inbox</c> table is generic substrate infrastructure (the consumer dedup row),
/// so writing to it is a substrate concern — exposed here so a family's <see cref="Saga.IPostAdvanceHook"/>
/// self-emit can record its deterministic-id dedup row WITHOUT the family naming the substrate's table SQL.
/// </summary>
/// <remarks>
/// A self-emitted event's message id is deterministic (derived from the process id + event type), so it
/// dedups through the SAME <c>inbox</c> as an external advance: a re-drive of the trigger derives the same
/// id and the row collides, so the self-emit is applied exactly once (effectively-once). result_summary
/// stays operational-tier (the saga step taken) — NEVER PII (ADR-PC-004 §P2).
/// </remarks>
public sealed class SagaInboxWriter
{
    /// <summary>
    /// INSERT one inbox dedup row on the caller's transaction. A duplicate <c>message_id</c> throws a
    /// unique-violation (the race-safe backstop), rolling the caller's transaction back.
    /// </summary>
    public async Task WriteRowAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        SagaInboxEvent message, string resultSummary, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO inbox (message_id, source_topic, result_summary)
            VALUES (@message_id, @source_topic, @result_summary);
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("message_id", message.MessageId);
        command.Parameters.AddWithValue("source_topic", message.SourceTopic);
        command.Parameters.AddWithValue("result_summary", resultSummary);
        await command.ExecuteNonQueryAsync(ct);
    }
}
