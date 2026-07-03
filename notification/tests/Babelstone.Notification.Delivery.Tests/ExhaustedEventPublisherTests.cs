using System.Text;
using Avro;
using Avro.Generic;
using Avro.IO;
using Xunit;

namespace Babelstone.Notification.Delivery.Tests;

/// <summary>
/// The exhaustion event's WIRE SHAPE (ADR-IC-011): the encoded value round-trips through the governed
/// <c>operations.NotificationDeliveryExhausted</c> Avro schema (the embedded copy of
/// <c>contracts/avro/operations/NotificationDeliveryExhausted.avsc</c> — the single source), the
/// Confluent framing is byte-exact (magic 0x00 ‖ big-endian schema_id ‖ value), the CloudEvents
/// binary-mode headers carry the catalogue-documented attribute set, and the field-inline
/// <c>trigger_kind</c> enum copy stays symbol-identical to <c>NotificationDue</c>'s. All pure — no
/// broker, no registry, no Docker.
/// </summary>
public sealed class ExhaustedEventPublisherTests
{
    private static ExhaustedDelivery Exhausted(
        Guid? customerRef = null, Guid? causationId = null, string? lastError = "receiver answered 503") => new(
        NotificationId: Guid.NewGuid(),
        EventId: Guid.NewGuid(),
        InstanceId: Guid.NewGuid(),
        CustomerRef: customerRef,
        TemplateRef: "pt.test.notice",
        TemplatePackVersion: "pt.2026.1",
        TriggerKind: NotificationTriggerKind.EventDriven,
        CausationId: causationId,
        Attempts: 10,
        LastError: lastError,
        ExhaustedAt: new DateTimeOffset(2026, 7, 3, 12, 30, 45, 123, TimeSpan.Zero));

    [Fact]
    public void Avro_value_round_trips_through_the_governed_schema()
    {
        var customerRef = Guid.NewGuid();
        var causationId = Guid.NewGuid();
        var exhausted = Exhausted(customerRef, causationId);

        var decoded = Decode(KafkaExhaustedEventPublisher.EncodeAvro(exhausted));

        Assert.Equal(exhausted.NotificationId, decoded["notification_id"]);
        Assert.Equal(exhausted.InstanceId, decoded["instance_id"]);
        Assert.Equal(customerRef, decoded["customer_id"]);
        Assert.Equal("pt.test.notice", decoded["template_ref"]);
        Assert.Equal("pt.2026.1", decoded["template_pack_version"]);
        Assert.Equal("EVENT_DRIVEN", Assert.IsType<GenericEnum>(decoded["trigger_kind"]).Value);
        // The causing-fact reference carries through unchanged (ADR-PC-023 traceability).
        Assert.Equal(causationId, decoded["causation_id"]);
        Assert.Equal(10, decoded["attempts"]);
        Assert.Equal("receiver answered 503", decoded["last_error"]);
        // timestamp-millis round-trips as a UTC DateTime at millisecond precision.
        Assert.Equal(exhausted.ExhaustedAt.UtcDateTime, decoded["exhausted_at"]);
    }

    [Fact]
    public void Null_customer_ref_causation_and_last_error_ride_the_null_first_unions()
    {
        // The v1 SCHEDULED leg's signal carries no recipient reference and no causing domain event
        // (ADR-PC-025 / ADR-PC-023) — its exhaustion must encode cleanly through the [null, T]
        // unions (ADR-IC-002).
        var decoded = Decode(KafkaExhaustedEventPublisher.EncodeAvro(
            Exhausted(customerRef: null, causationId: null, lastError: null)));

        Assert.Null(decoded["customer_id"]);
        Assert.Null(decoded["causation_id"]);
        Assert.Null(decoded["last_error"]);
    }

    [Fact]
    public void Confluent_wire_format_is_magic_byte_then_big_endian_schema_id_then_value()
    {
        byte[] value = [0xAA, 0xBB];

        var framed = KafkaExhaustedEventPublisher.ToConfluentWireFormat(0x01020304, value);

        Assert.Equal([0x00, 0x01, 0x02, 0x03, 0x04, 0xAA, 0xBB], framed);
    }

    [Fact]
    public void Cloud_events_headers_carry_the_catalogued_attribute_set()
    {
        var exhausted = Exhausted();

        var headers = KafkaExhaustedEventPublisher.BuildHeaders(exhausted);

        Assert.Equal("1.0", Header(headers, "ce_specversion"));
        Assert.Equal(exhausted.EventId.ToString(), Header(headers, "ce_id"));
        Assert.Equal("urn:babelstone:notification", Header(headers, "ce_source"));
        Assert.Equal("com.bank.operations.NotificationDeliveryExhausted", Header(headers, "ce_type"));
        Assert.Equal("application/avro", Header(headers, "ce_datacontenttype"));
        Assert.Equal(exhausted.InstanceId.ToString(), Header(headers, "ce_subject"));
        Assert.Equal("operations", Header(headers, "ce_aggregatetype"));
        Assert.StartsWith("2026-07-03T12:30:45", Header(headers, "ce_time"), StringComparison.Ordinal);
    }

    [Fact]
    public void Payload_carries_no_pii_field()
    {
        // The no-PII-on-bus posture (ADR-PC-004), asserted on the embedded schema the publisher
        // actually encodes with: only opaque references and structural/transport fields.
        var fieldNames = KafkaExhaustedEventPublisher.PayloadSchema.Fields.Select(f => f.Name).ToArray();

        Assert.Equal(
            ["notification_id", "instance_id", "customer_id", "template_ref", "template_pack_version",
             "trigger_kind", "causation_id", "attempts", "last_error", "exhausted_at"],
            fieldNames);
    }

    [Fact]
    public void Trigger_kind_enum_copy_stays_symbol_identical_to_notification_due()
    {
        // The exhausted schema deliberately embeds its OWN copy of the NotificationTriggerKind enum
        // (independent SR subjects — ADR-IC-002); this pins the two copies symbol-for-symbol so a
        // taxonomy change in NotificationDue.avsc that misses this schema fails HERE, not as an
        // encode throw at dead-letter time.
        var duePath = Path.Combine(
            RepoRoot(), "contracts", "avro", "operations", "NotificationDue.avsc");
        var dueSchema = (RecordSchema)Schema.Parse(File.ReadAllText(duePath));

        var dueSymbols = ((EnumSchema)UnwrapEnum(dueSchema["trigger_kind"].Schema)).Symbols;
        var exhaustedSymbols = ((EnumSchema)UnwrapEnum(
            KafkaExhaustedEventPublisher.PayloadSchema["trigger_kind"].Schema)).Symbols;

        Assert.Equal(dueSymbols, exhaustedSymbols);

        static Schema UnwrapEnum(Schema schema) =>
            schema is UnionSchema union ? union.Schemas.Single(s => s is EnumSchema) : schema;
    }

    private static GenericRecord Decode(byte[] avroValue)
    {
        var schema = KafkaExhaustedEventPublisher.PayloadSchema;
        using var stream = new MemoryStream(avroValue);
        var reader = new GenericDatumReader<GenericRecord>(schema, schema);
        return reader.Read(default!, new BinaryDecoder(stream));
    }

    private static string Header(Confluent.Kafka.Headers headers, string key) =>
        Encoding.UTF8.GetString(headers.Single(h => h.Key == key).GetValueBytes());

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "contracts", "avro", "operations", "NotificationDue.avsc")))
            {
                return dir.FullName;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException(
            $"repo root (containing contracts/avro) not found from {AppContext.BaseDirectory}");
    }
}
