namespace Babelstone.EventStore;

/// <summary>
/// The persisted event row, carrying the ADR-PC-001 §P1 column contract one-for-one.
/// No <c>mt_*</c> or other library-internal fields — the §P3 invariant of ADR-PC-010
/// forbids them. PII-bearing fields live inside <see cref="Payload"/> as ciphertext
/// under per-subject keys (ADR-PC-004); storage never sees plaintext.
/// </summary>
public sealed record EventEnvelope(
    Guid                 EventId,
    Guid                 StreamId,
    long                 SequenceNumber,
    string               EventType,            // "term_deposit.DepositConstituted"
    int                  EventSchemaVersion,
    string               Family,
    Guid                 PartitionKey,         // v1 = StreamId; v4 may differ
    string               PackVersion,          // "pt.2026.1"
    string               SchemaVersion,        // "term_deposit@2026.1"
    DateTimeOffset       ValidTime,
    DateTimeOffset       TransactionTime,
    Guid?                CausationId,
    Guid?                CorrelationId,
    string               Actor,
    ReadOnlyMemory<byte> Payload,              // Avro-serialized, PII fields ciphertext
    int                  PayloadSchemaId);     // Confluent SR id, embedded at write
