using Confluent.Kafka;

namespace Babelstone.InboxConsumer;

/// <summary>
/// The seam a host plugs a dead-letter / quarantine policy into. The pump calls it for every record
/// it skips as poison (un-decodable, unknown event type, or a missing <c>ce_id</c>) BEFORE committing
/// the offset past it — so a host can persist the raw bytes, alert, or republish to a DLQ topic
/// before the record is stepped over. Optional: with no sink, a poison record is silently skipped
/// (the poison counter still increments).
/// </summary>
/// <remarks>
/// A poison record is NOT a transient failure (those throw out of the handler and are redelivered) —
/// it is a record no amount of retry fixes, so the policy here is about preserving evidence, not
/// recovery. The raw <see cref="ConsumeResult{TKey,TValue}"/> is handed over so a sink can keep the
/// exact bytes/headers; a sink MUST treat the value as opaque and MUST NOT log payload contents that
/// could carry PII (the durable bus carries references, but a malformed record's provenance is
/// unknown — quarantine, do not echo).
/// </remarks>
public interface IInboxPoisonSink
{
    /// <param name="result">The raw Kafka record that could not be processed.</param>
    /// <param name="reason">A short operational reason (no payload contents) — e.g. "unknown event type".</param>
    /// <param name="ct">Cancellation for a graceful shutdown.</param>
    Task OnPoisonAsync(ConsumeResult<byte[], byte[]> result, string reason, CancellationToken ct = default);
}
