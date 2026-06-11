namespace Babelstone.Orchestrator.Inbox;

/// <summary>
/// One inbox event handed to the saga-advance seam — the orchestrator's view of a decoded
/// integration/domain message (the mirror of the engine's <c>InboxMessage</c>, ADR-IC-003
/// §S2 "the orchestrator is a Redpanda consumer like every other service"). The real
/// consume loop (the engine's <c>InboxPump</c>, plugged in via its <c>IInboxMessageHandler</c>
/// seam) un-frames the Confluent wire format and decodes the Avro; this is the PII-free
/// projection of that record the saga reasons over.
/// </summary>
/// <remarks>
/// Every field is structural / a reference, NEVER PII (ADR-PC-004 §P2; no-PII-on-the-durable
/// -bus): <see cref="MessageId"/> is the dedup identity (ce_id), <see cref="ProcessId"/> is
/// the saga instance reference (ce_subject), <see cref="EventType"/> is a type name, and
/// <see cref="CorrelationId"/> is the trace reference (ADR-IC-003 §P7). A subject's
/// NIF/IBAN/name/amount NEVER arrives here — a saga that needs PII resolves it internally
/// behind the engine's OpenBao boundary.
/// </remarks>
/// <param name="MessageId">The CloudEvents <c>ce_id</c> — the dedup key (Document 04).</param>
/// <param name="ProcessId">The saga instance this event drives (the <c>ce_subject</c>
/// PROC-… reference).</param>
/// <param name="EventType">The event's record-name type (e.g. <c>BalanceReserved</c>) — the
/// key the state machine's transition table keys on (ADR-IC-003 §P2).</param>
/// <param name="SourceTopic">The topic the record arrived on (structural, not PII).</param>
/// <param name="CorrelationId">The trace correlation reference carried unchanged through the
/// saga (ADR-IC-003 §P7). Null on a message that carried none.</param>
/// <param name="TraceParent">The inbound W3C Trace Context <c>traceparent</c> header (H.5,
/// ADR-IC-007 Layer 1), extracted from the Kafka record by the consume loop. It parents the
/// saga-advance span onto the upstream trace so the saga's work is one connected distributed
/// trace. Operational, NOT PII (an opaque <c>00-trace-span-flags</c> string). Null on a message
/// that carried no trace context — the advance then roots a fresh trace.</param>
public sealed record SagaInboxEvent(
    Guid MessageId,
    Guid ProcessId,
    string EventType,
    string SourceTopic,
    Guid? CorrelationId,
    string? TraceParent = null);
