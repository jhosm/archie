using Babelstone.Engine;

namespace Babelstone.InboxConsumer;

/// <summary>
/// One decoded integration message handed to the dispatch seam: the deduplication identity
/// (<see cref="MessageId"/>) plus the already-Avro-decoded <see cref="DomainEvent"/> and the
/// structural envelope a handler routes on. The consume loop builds this from the Kafka record —
/// it un-frames the Confluent wire-format value, decodes the Avro via the G.3 codec, and reads the
/// CloudEvents headers (ADR-IC-015) — so the handler never touches Kafka, wire bytes, or the
/// Schema Registry.
/// </summary>
/// <remarks>
/// Everything here is operational-tier / a reference, never PII (the durable bus carries references,
/// not personal data — ADR-PC-004 §P2): <see cref="MessageId"/> and <see cref="AggregateId"/> are
/// structural GUIDs, <see cref="SourceTopic"/> and <see cref="EventType"/> are type/topic names. A
/// handler that needs a subject's PII resolves it internally behind the engine's OpenBao boundary —
/// it does not arrive on this record.
/// </remarks>
public sealed record InboxMessage(
    Guid        MessageId,    // the CloudEvents ce_id (the producer's event_id) — the dedup key
    string      SourceTopic,  // the topic the record arrived on (== aggregate_type)
    Guid        AggregateId,  // the CloudEvents ce_subject — the stream the event belongs to
    string      EventType,    // the CloudEvents ce_type (reverse-DNS, e.g. com.bank.deposits.DepositMatured)
    DomainEvent Event);       // the Avro-decoded domain event payload
