using System.Text;
using Avro;
using Avro.Generic;
using Avro.IO;
using Confluent.Kafka;
using Xunit;

namespace Babelstone.Notification.Delivery.Tests;

/// <summary>
/// The REAL EVENT_DRIVEN ingress (bd babelstone-60n8.7 / babelstone-60n8.11): the Redpanda/Avro consumer
/// of the engine's <c>operations.NotificationDue</c> stream. These tests prove — with NO broker — that it
/// (1) decodes the governed Avro value round-trip into the delivery-side <see cref="NotificationDueSignal"/>
/// exactly as the consumer Pact pins it, (2) discriminates NotificationDue from the other records sharing
/// the <c>operations</c> topic by the <c>ce_type</c> header, (3) skips tombstones/poison without stalling,
/// and (4) commits offsets only on the NEXT poll — the deferred "commit only after enqueued" at-least-once
/// contract (<see cref="INotificationDueSource"/> / ADR-IC-011 §P3).
/// </summary>
public sealed class KafkaNotificationDueSourceTests
{
    private const string NotificationDueCeType = "com.bank.operations.NotificationDue";
    private const string ExhaustedCeType = "com.bank.operations.NotificationDeliveryExhausted";

    private static NotificationDueSignal Signal(
        NotificationTriggerKind triggerKind = NotificationTriggerKind.EventDriven,
        Guid? causationId = null) => new(
        NotificationId: Guid.NewGuid(),
        InstanceId: Guid.NewGuid(),
        CustomerRef: Guid.NewGuid(),
        TemplateRef: "pt.notice.maturity",
        TemplatePackVersion: "pt.2026.1",
        TriggerKind: triggerKind,
        CausationId: causationId,
        Data: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["principal_cents"] = "1000000",
            ["maturity_date"] = "2026-09-01",
        },
        DueAt: new DateOnly(2026, 9, 1));

    [Fact]
    public void Decodes_a_governed_notification_due_value_into_a_signal()
    {
        var expected = Signal(NotificationTriggerKind.EventDriven, causationId: Guid.NewGuid());

        var ok = KafkaNotificationDueSource.TryDecode(EncodeFramed(expected), out var actual);

        Assert.True(ok);
        Assert.Equal(expected.NotificationId, actual.NotificationId);
        Assert.Equal(expected.InstanceId, actual.InstanceId);
        Assert.Equal(expected.CustomerRef, actual.CustomerRef);
        Assert.Equal(expected.TemplateRef, actual.TemplateRef);
        Assert.Equal(expected.TemplatePackVersion, actual.TemplatePackVersion);
        Assert.Equal(NotificationTriggerKind.EventDriven, actual.TriggerKind);
        Assert.Equal(expected.CausationId, actual.CausationId);
        Assert.Equal("1000000", actual.Data["principal_cents"]);
        Assert.Equal("2026-09-01", actual.Data["maturity_date"]);
        Assert.Equal(expected.DueAt, actual.DueAt);
    }

    [Fact]
    public void A_null_causation_rides_the_null_first_union()
    {
        // A downstream-produced SCHEDULED signal carries no causing domain event (ADR-PC-023); the
        // [null, uuid] union must decode cleanly to a null CausationId.
        var ok = KafkaNotificationDueSource.TryDecode(
            EncodeFramed(Signal(NotificationTriggerKind.Scheduled, causationId: null)), out var actual);

        Assert.True(ok);
        Assert.Null(actual.CausationId);
        Assert.Equal(NotificationTriggerKind.Scheduled, actual.TriggerKind);
    }

    [Fact]
    public void A_malformed_value_is_poison_not_a_throw()
    {
        // Bad Avro body behind a valid Confluent frame: decode fails softly (poison → skipped by the pass).
        byte[] framed = KafkaExhaustedEventPublisher.ToConfluentWireFormat(42, [0xFF, 0xFF, 0xFF]);

        Assert.False(KafkaNotificationDueSource.TryDecode(framed, out _));
    }

    [Fact]
    public void An_unframed_or_short_buffer_is_poison_not_a_throw()
    {
        Assert.False(KafkaNotificationDueSource.TryDecode([0x01, 0x02], out _));   // no magic byte
        Assert.False(KafkaNotificationDueSource.TryDecode([], out _));             // empty
    }

    [Theory]
    [InlineData(NotificationDueCeType, true)]
    [InlineData("NotificationDue", true)]
    [InlineData(ExhaustedCeType, false)]
    [InlineData("com.bank.operations.SomethingElse", false)]
    public void IsNotificationDue_matches_only_the_notification_due_record(string ceType, bool expected)
        => Assert.Equal(expected, KafkaNotificationDueSource.IsNotificationDue(CeTypeHeader(ceType)));

    [Fact]
    public void IsNotificationDue_is_false_when_the_header_is_absent()
    {
        Assert.False(KafkaNotificationDueSource.IsNotificationDue(new Headers()));
        Assert.False(KafkaNotificationDueSource.IsNotificationDue(null));
    }

    [Fact]
    public async Task Poll_returns_notification_due_records_and_skips_foreign_tombstone_and_poison()
    {
        var wanted = Signal(NotificationTriggerKind.EventDriven, causationId: Guid.NewGuid());
        var consumer = new FakeByteMessageConsumer();
        consumer.Enqueue(
            Record(EncodeFramed(wanted), CeTypeHeader(NotificationDueCeType), offset: 0),
            Record(EncodeFramed(Signal()), CeTypeHeader(ExhaustedCeType), offset: 1),   // foreign record
            Tombstone(offset: 2),                                                        // compaction marker
            Record(KafkaExhaustedEventPublisher.ToConfluentWireFormat(1, [0xFF]),        // poison NotificationDue
                CeTypeHeader(NotificationDueCeType), offset: 3));

        using var source = new KafkaNotificationDueSource(consumer);
        var batch = await source.PollAsync();

        var signal = Assert.Single(batch);
        Assert.Equal(wanted.NotificationId, signal.NotificationId);
        Assert.Equal(wanted.CustomerRef, signal.CustomerRef);
    }

    [Fact]
    public async Task Empty_poll_returns_no_signals_and_commits_nothing()
    {
        var consumer = new FakeByteMessageConsumer();

        using var source = new KafkaNotificationDueSource(consumer);
        var batch = await source.PollAsync();

        Assert.Empty(batch);
        Assert.Empty(consumer.Committed);
    }

    [Fact]
    public async Task Offsets_commit_only_on_the_next_poll_not_the_one_that_consumed_them()
    {
        var consumer = new FakeByteMessageConsumer();
        var record = Record(EncodeFramed(Signal()), CeTypeHeader(NotificationDueCeType), offset: 7);
        consumer.Enqueue(record);

        using var source = new KafkaNotificationDueSource(consumer);

        // First poll hands out the batch — but MUST NOT commit yet (the pass has not enqueued it).
        var first = await source.PollAsync();
        Assert.Single(first);
        Assert.Empty(consumer.Committed);

        // The NEXT poll commits the prior batch's offset first — a full tick after the pass enqueued it
        // (deferred commit + idempotent outbox enqueue = at-least-once, nothing lost).
        var second = await source.PollAsync();
        Assert.Empty(second);
        var committed = Assert.Single(consumer.Committed);
        Assert.Equal(new Offset(7), committed.Offset);
    }

    [Fact]
    public async Task Dispose_commits_the_pending_batch_and_closes_the_group_membership()
    {
        var consumer = new FakeByteMessageConsumer();
        var record = Record(EncodeFramed(Signal()), CeTypeHeader(NotificationDueCeType), offset: 3);
        consumer.Enqueue(record);

        var source = new KafkaNotificationDueSource(consumer, ownsConsumer: true);
        _ = await source.PollAsync();
        source.Dispose();

        Assert.Equal(new Offset(3), Assert.Single(consumer.Committed).Offset);
        Assert.True(consumer.Closed);
        Assert.True(consumer.Disposed);
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static byte[] EncodeFramed(NotificationDueSignal signal, int schemaId = 42)
    {
        var schema = KafkaNotificationDueSource.PayloadSchema;
        var record = new GenericRecord(schema);
        record.Add("notification_id", signal.NotificationId);
        record.Add("instance_id", signal.InstanceId);
        record.Add("customer_id", signal.CustomerRef!.Value);   // required uuid on the wire
        record.Add("template_ref", signal.TemplateRef);
        record.Add("template_pack_version", signal.TemplatePackVersion);
        record.Add("trigger_kind", new GenericEnum(
            (EnumSchema)schema["trigger_kind"].Schema, TriggerKindWire.ToWire(signal.TriggerKind)));
        record.Add("causation_id", signal.CausationId.HasValue ? signal.CausationId.Value : null);
        record.Add("data", signal.Data.ToDictionary(kv => kv.Key, kv => (object)kv.Value));
        record.Add("due_at", signal.DueAt.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        using var stream = new MemoryStream();
        new GenericDatumWriter<GenericRecord>(schema).Write(record, new BinaryEncoder(stream));
        return KafkaExhaustedEventPublisher.ToConfluentWireFormat(schemaId, stream.ToArray());
    }

    private static Headers CeTypeHeader(string ceType)
    {
        var headers = new Headers();
        headers.Add("ce_type", Encoding.UTF8.GetBytes(ceType));
        return headers;
    }

    private static ConsumeResult<byte[], byte[]> Record(byte[] value, Headers headers, long offset) => new()
    {
        Message = new Message<byte[], byte[]>
        {
            Key = Guid.NewGuid().ToByteArray(),
            Value = value,
            Headers = headers,
        },
        TopicPartitionOffset = new TopicPartitionOffset(
            KafkaNotificationDueSource.Topic, new Partition(0), new Offset(offset)),
    };

    private static ConsumeResult<byte[], byte[]> Tombstone(long offset) => new()
    {
        Message = new Message<byte[], byte[]>
        {
            Key = Guid.NewGuid().ToByteArray(),
            Value = null!,
            Headers = new Headers(),
        },
        TopicPartitionOffset = new TopicPartitionOffset(
            KafkaNotificationDueSource.Topic, new Partition(0), new Offset(offset)),
    };

    private sealed class FakeByteMessageConsumer : IByteMessageConsumer
    {
        private readonly Queue<ConsumeResult<byte[], byte[]>> _results = new();

        public List<ConsumeResult<byte[], byte[]>> Committed { get; } = [];

        public bool Closed { get; private set; }

        public bool Disposed { get; private set; }

        public void Enqueue(params ConsumeResult<byte[], byte[]>[] results)
        {
            foreach (var result in results)
            {
                _results.Enqueue(result);
            }
        }

        public ConsumeResult<byte[], byte[]>? Consume(TimeSpan timeout)
            => _results.Count > 0 ? _results.Dequeue() : null;

        public void Commit(ConsumeResult<byte[], byte[]> result) => Committed.Add(result);

        public void Close() => Closed = true;

        public void Dispose() => Disposed = true;
    }
}
