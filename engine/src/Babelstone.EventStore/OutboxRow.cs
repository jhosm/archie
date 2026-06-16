namespace Babelstone.EventStore;

/// <summary>Outbox row lifecycle (ADR-IC-004 §P1): written PENDING, flipped PUBLISHED by the relay.</summary>
public enum OutboxStatus
{
    Pending,
    Published,
}

/// <summary>
/// The outbox row mirroring ADR-IC-004 §P1 column-for-column. Written in the SAME
/// local transaction as the event it records (§P2 / ADR-PC-001 §P2); drained by the
/// polling publisher (Epic E), which is the only reader.
/// </summary>
public sealed record OutboxRow(
    Guid                 EventId,
    string               AggregateType,
    Guid                 AggregateId,
    long                 SequenceNumber,       // the event's per-stream sequence; the §P2 drain tiebreaker
    string               EventType,
    ReadOnlyMemory<byte> Payload,
    int                  SchemaId,             // schema-registry id, embedded at write (§P3)
    OutboxStatus         Status,
    DateTimeOffset       CreatedAt,
    DateTimeOffset?      PublishedAt,
    // CloudEvents extension attributes the event declared (DomainEvent.IntegrationHeaders), persisted
    // as the integration_headers JSONB column (migration 0016). The relay promotes each entry to a
    // ce_<key> header (OutboxDrainer.BuildHeaders, ADR-IC-018 §P5) — keeping every emitted header
    // derivable from the outbox row alone (ADR-IC-004). Null for events that declare none; optional +
    // null-defaulted so every existing construction site stays source-compatible.
    IReadOnlyDictionary<string, string>? IntegrationHeaders = null);
