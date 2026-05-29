using Babelstone.EventStore;

namespace Babelstone.EventStore.Tests;

/// <summary>Builders for valid envelope / outbox rows so tests state only what they vary.</summary>
internal static class TestData
{
    private static readonly DateTimeOffset Fixed = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static (EventEnvelope Event, OutboxRow Outbox) Pair(Guid streamId, long sequence)
    {
        var eventId = Guid.NewGuid();
        var envelope = new EventEnvelope(
            EventId: eventId,
            StreamId: streamId,
            SequenceNumber: sequence,
            EventType: "term_deposit.DepositConstituted",
            EventSchemaVersion: 1,
            Family: "term_deposit",
            PartitionKey: streamId,
            PackVersion: "pt.2026.1",
            SchemaVersion: "term_deposit@2026.1",
            ValidTime: Fixed,
            TransactionTime: Fixed,
            CausationId: null,
            CorrelationId: null,
            Actor: "test",
            Payload: new byte[] { 0x01, 0x02 },
            PayloadSchemaId: 42);

        var outbox = new OutboxRow(
            EventId: eventId,
            AggregateType: "term_deposit",
            AggregateId: streamId,
            SequenceNumber: sequence,
            EventType: "term_deposit.DepositConstituted",
            Payload: new byte[] { 0x01, 0x02 },
            SchemaId: 42,
            Status: OutboxStatus.Pending,
            CreatedAt: Fixed,
            PublishedAt: null);

        return (envelope, outbox);
    }
}
