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
    string               EventType,
    ReadOnlyMemory<byte> Payload,
    int                  SchemaId,             // schema-registry id, embedded at write (§P3)
    OutboxStatus         Status,
    DateTimeOffset       CreatedAt,
    DateTimeOffset?      PublishedAt);
