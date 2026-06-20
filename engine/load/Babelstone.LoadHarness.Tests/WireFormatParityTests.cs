using System.Text;
using Babelstone.EventStore;
using Babelstone.OutboxPublisher;
using Confluent.Kafka;
using Xunit;

namespace Babelstone.LoadHarness.Tests;

/// <summary>
/// Parity guard for the harness's duplicated Confluent wire framing + CloudEvents header contract
/// (ADR-PC-011 §G1, bd a7f6). The load harness re-implements the relay's framing in its own
/// <see cref="WireFormat"/> because the relay's helpers
/// (<c>OutboxDrainer.ToConfluentWireFormat</c> / <c>BuildHeadersCore</c> / <c>ReverseDnsType</c>) are
/// <c>internal</c> to <c>Babelstone.OutboxPublisher</c>. The drift-prone VALUE bytes still come from
/// the engine's own serializer (so §G1 holds), but the FRAMING is duplicated and could silently drift
/// from the relay. These tests pin the two implementations byte-for-byte / header-for-header so any
/// future change to one without the other fails the build.
/// </summary>
/// <remarks>
/// In plain English: the load test and the real outbox relay each build the little Kafka prefix and the
/// CloudEvents headers in their own code. If someone changed one and forgot the other, the load test
/// would stop looking like production traffic — and nobody would notice. This test holds the two side
/// by side and asserts they produce exactly the same bytes and the same headers, so a drift breaks CI
/// rather than the load run silently lying.
///
/// Both implementations are reached via <c>InternalsVisibleTo</c>: <see cref="WireFormat"/> from the
/// harness (already granted) and <c>OutboxDrainer</c> from the relay (the additive grant this lane adds
/// to <c>Babelstone.OutboxPublisher.csproj</c> — no relay source changes).
/// </remarks>
public sealed class WireFormatParityTests
{
    [Theory]
    [InlineData(0, new byte[0])]
    [InlineData(1, new byte[] { 0xAA })]
    [InlineData(7, new byte[] { 0xAA, 0xBB, 0xCC })]
    [InlineData(42, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 })]
    [InlineData(int.MaxValue, new byte[] { 0xFF, 0x00, 0xFF })]
    public void Confluent_framing_is_byte_for_byte_identical(int schemaId, byte[] avroValue)
    {
        // The relay's framing (internal to Babelstone.OutboxPublisher) and the harness's mirror must
        // produce identical bytes: magic byte 0x00 ‖ big-endian int32 schema_id ‖ avro value.
        var relay = OutboxDrainer.ToConfluentWireFormat(schemaId, avroValue);
        var harness = WireFormat.ToConfluentWireFormat(schemaId, avroValue);

        Assert.Equal(relay, harness);
    }

    [Theory]
    // The catalogued (relay-publishable) set plus a generic non-deposit prefix and a no-dot case, so the
    // parity covers the transform's shape, not just the three promoted events.
    [InlineData("term_deposit.DepositConstituted")]
    [InlineData("term_deposit.InterestPaid")]
    [InlineData("term_deposit.DepositMatured")]
    [InlineData("savings.SomethingHappened")]
    [InlineData("NoPrefix")]
    public void Reverse_dns_ce_type_mapping_is_identical(string eventType)
    {
        // ce_type derivation must match: a divergence here would make the harness publish a different
        // CloudEvents type than the relay for the same event.
        Assert.Equal(
            OutboxDrainer.ReverseDnsType(eventType),
            WireFormat.ReverseDnsType(eventType));
    }

    [Fact]
    public void Standard_cloud_event_header_set_matches_the_relay()
    {
        // Build the SAME logical event two ways — through the relay's row-driven BuildHeadersCore and
        // through the harness's argument-driven BuildCloudEventHeaders — and assert the resulting
        // standard ce_* header set is identical key-for-key and value-for-value.
        var eventId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var partitionKey = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var time = new DateTimeOffset(2026, 6, 20, 9, 5, 0, TimeSpan.Zero);
        const string source = "urn:babelstone:loadharness";
        const string eventType = "term_deposit.DepositConstituted";
        const string aggregateType = "term_deposit";

        var relay = HeaderMap(OutboxDrainer.BuildHeadersCore(
            Row(eventId, partitionKey, aggregateType, eventType, time, integrationHeaders: null),
            source));
        var harness = HeaderMap(WireFormat.BuildCloudEventHeaders(
            eventId, source, eventType, aggregateType, partitionKey, time, extensionHeaders: null));

        Assert.Equal(relay, harness);

        // Spot-check the contract values are the published ones, not just internally consistent.
        Assert.Equal("1.0", relay["ce_specversion"]);
        Assert.Equal("com.bank.deposits.DepositConstituted", relay["ce_type"]);
        Assert.Equal("application/avro", relay["ce_datacontenttype"]);
        Assert.Equal(partitionKey.ToString(), relay["ce_subject"]);
        Assert.Equal(aggregateType, relay["ce_aggregatetype"]);
    }

    [Fact]
    public void Extension_attribute_promotion_matches_the_relay()
    {
        // A family-declared extension attribute (ADR-IC-018 §P5) must be promoted to the SAME ce_<key>
        // header by both implementations — the seam names no key, it copies whatever the event declared.
        var eventId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var partitionKey = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var time = new DateTimeOffset(2026, 6, 20, 9, 5, 0, TimeSpan.Zero);
        const string source = "urn:babelstone:loadharness";
        const string eventType = "term_deposit.DepositMatured";
        const string aggregateType = "term_deposit";
        var extensions = new Dictionary<string, string>
        {
            ["autorenewalpolicy"] = "SAME_TERM_CURRENT_RATE",
            ["foo"] = "1",
        };

        var relay = HeaderMap(OutboxDrainer.BuildHeadersCore(
            Row(eventId, partitionKey, aggregateType, eventType, time, extensions),
            source));
        var harness = HeaderMap(WireFormat.BuildCloudEventHeaders(
            eventId, source, eventType, aggregateType, partitionKey, time, extensions));

        Assert.Equal(relay, harness);
        Assert.Equal("SAME_TERM_CURRENT_RATE", harness["ce_autorenewalpolicy"]);
        Assert.Equal("1", harness["ce_foo"]);
    }

    // ---- helpers ----

    // An outbox row carrying exactly the fields the relay's header build reads, so BuildHeadersCore and
    // the harness's BuildCloudEventHeaders are fed the SAME logical event. ce_time is row.CreatedAt for
    // the relay and the harness's `time` argument — passed as the same instant on both sides.
    private static OutboxRow Row(
        Guid eventId,
        Guid partitionKey,
        string aggregateType,
        string eventType,
        DateTimeOffset time,
        IReadOnlyDictionary<string, string>? integrationHeaders) => new(
        EventId: eventId,
        AggregateType: aggregateType,
        AggregateId: partitionKey,
        SequenceNumber: 1,
        EventType: eventType,
        Payload: ReadOnlyMemory<byte>.Empty,
        SchemaId: 1,
        Status: OutboxStatus.Pending,
        CreatedAt: time,
        PublishedAt: null,
        IntegrationHeaders: integrationHeaders);

    private static SortedDictionary<string, string> HeaderMap(Headers headers)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in headers)
        {
            map[header.Key] = Encoding.UTF8.GetString(header.GetValueBytes());
        }

        return map;
    }
}
