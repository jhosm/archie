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
/// <param name="ExtensionHeaders">The NON-standard CloudEvents extension attributes carried on the
/// record (ADR-IC-018 §P5/§D5), keyed by attribute name WITHOUT the <c>ce_</c> prefix and lowercased
/// (e.g. <c>ce_autorenewalpolicy</c> → <c>{ "autorenewalpolicy": "SAME_TERM_CURRENT_RATE" }</c>). The
/// substrate's event-auto-start machinery reads ONLY these declared headers — never the Avro payload —
/// to decide whether an unknown-saga event starts a new instance, so the extraction-ready,
/// payload-blind boundary is preserved. Every value is a structural routing discriminator, NEVER PII
/// (ADR-PC-004 §P2). Null when the record carried no extension attributes beyond the standard set.</param>
/// <param name="SubjectId">The account/instrument linkage (the record's REAL <c>ce_subject</c>) when
/// <see cref="ProcessId"/> is a DERIVED per-occurrence saga instance id (ADR-PC-032 §A9/§A10 Revised
/// 2026-07-04): the settlement fan-out rewrites <see cref="ProcessId"/> to a deterministic derivation of
/// (ce_subject, event id, movement index) and preserves the subject here, so the start path persists it
/// into <c>saga_state.subject_id</c> (the LCD-2 probe's key). <c>null</c> — the default, and what the
/// consume loop always projects — means <see cref="ProcessId"/> IS the subject (every non-fanned-out
/// event). A non-null value also marks the event as an already-projected occurrence leg, which is what
/// makes a projected leg INERT on fan-out re-entry. Structural GUID, never PII (ADR-PC-004 §P2).</param>
public sealed record SagaInboxEvent(
    Guid MessageId,
    Guid ProcessId,
    string EventType,
    string SourceTopic,
    Guid? CorrelationId,
    string? TraceParent = null,
    IReadOnlyDictionary<string, string>? ExtensionHeaders = null,
    Guid? SubjectId = null);
