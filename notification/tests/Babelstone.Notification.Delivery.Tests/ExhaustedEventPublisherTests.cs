using System.Text;
using Avro.Generic;
using Avro.IO;
using Xunit;

namespace Babelstone.Notification.Delivery.Tests;

/// <summary>
/// The §D4 exhaustion event's WIRE SHAPE (bd babelstone-60n8.10; ADR-IC-011 §P3 step 7): the encoded
/// value round-trips through the governed <c>operations.NotificationDeliveryExhausted</c> Avro schema
/// (the embedded copy of <c>contracts/avro/operations/NotificationDeliveryExhausted.avsc</c> — the
/// single source), the Confluent framing is byte-exact (magic 0x00 ‖ big-endian schema_id ‖ value),
/// and the CloudEvents binary-mode headers carry the catalogue-documented attribute set. All pure —
/// no broker, no registry, no Docker.
/// </summary>
public sealed class ExhaustedEventPublisherTests
{
    private static ExhaustedDelivery Exhausted(
        Guid? customerRef = null, string? lastError = "receiver answered 503") => new(
        NotificationId: Guid.NewGuid(),
        EventId: Guid.NewGuid(),
        InstanceId: Guid.NewGuid(),
        CustomerRef: customerRef,
        TemplateRef: "pt.test.notice",
        TemplatePackVersion: "pt.2026.1",
        TriggerKind: NotificationTriggerKind.EventDriven,
        Attempts: 10,
        LastError: lastError,
        ExhaustedAt: new DateTimeOffset(2026, 7, 3, 12, 30, 45, 123, TimeSpan.Zero));

    [Fact]
    public void Avro_value_round_trips_through_the_governed_schema()
    {
        var customerRef = Guid.NewGuid();
        var exhausted = Exhausted(customerRef);

        var decoded = Decode(KafkaExhaustedEventPublisher.EncodeAvro(exhausted));

        Assert.Equal(exhausted.NotificationId, decoded["notification_id"]);
        Assert.Equal(exhausted.InstanceId, decoded["instance_id"]);
        Assert.Equal(customerRef, decoded["customer_id"]);
        Assert.Equal("pt.test.notice", decoded["template_ref"]);
        Assert.Equal("pt.2026.1", decoded["template_pack_version"]);
        Assert.Equal("EVENT_DRIVEN", Assert.IsType<GenericEnum>(decoded["trigger_kind"]).Value);
        Assert.Equal(10, decoded["attempts"]);
        Assert.Equal("receiver answered 503", decoded["last_error"]);
        // timestamp-millis round-trips as a UTC DateTime at millisecond precision.
        Assert.Equal(exhausted.ExhaustedAt.UtcDateTime, decoded["exhausted_at"]);
    }

    [Fact]
    public void Null_customer_ref_and_null_last_error_ride_the_null_first_unions()
    {
        // The v1 SCHEDULED leg's signal carries no recipient reference (ADR-PC-025 named residual) —
        // its exhaustion must encode cleanly through the [null, T] unions (ADR-IC-002 §P2).
        var decoded = Decode(KafkaExhaustedEventPublisher.EncodeAvro(Exhausted(customerRef: null, lastError: null)));

        Assert.Null(decoded["customer_id"]);
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
        // The no-PII-on-bus posture (ADR-PC-004 §P2), asserted on the embedded schema the publisher
        // actually encodes with: only opaque references and structural/transport fields.
        var fieldNames = KafkaExhaustedEventPublisher.PayloadSchema.Fields.Select(f => f.Name).ToArray();

        Assert.Equal(
            ["notification_id", "instance_id", "customer_id", "template_ref", "template_pack_version",
             "trigger_kind", "attempts", "last_error", "exhausted_at"],
            fieldNames);
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
}
