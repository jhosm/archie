using System.Buffers.Binary;
using System.Text;
using Babelstone.EventStore;
using Xunit;

namespace Babelstone.OutboxPublisher.Tests;

/// <summary>
/// Pure (no-container) tests for the relay's pure transforms: the Confluent wire-format framing
/// (ADR-IC-002 §P3), the reverse-DNS ce_type mapping (ADR-IC-015), and the CloudEvents header build
/// (ADR-IC-015 / ADR-IC-018 §P5 extension promotion). Default CI lane.
/// </summary>
public sealed class WireFormatTests
{
    private static OutboxRow Row(IReadOnlyDictionary<string, string>? integrationHeaders) => new(
        EventId: Guid.NewGuid(),
        AggregateType: "term_deposit",
        AggregateId: Guid.NewGuid(),
        SequenceNumber: 1,
        EventType: "term_deposit.DepositMatured",
        Payload: ReadOnlyMemory<byte>.Empty,
        SchemaId: 1,
        Status: OutboxStatus.Pending,
        CreatedAt: DateTimeOffset.UnixEpoch,
        PublishedAt: null,
        IntegrationHeaders: integrationHeaders);

    private static string? HeaderValue(Confluent.Kafka.Headers headers, string key)
        => headers.TryGetLastBytes(key, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;

    [Fact]
    public void Build_headers_promotes_a_declared_extension_attribute_to_a_ce_header()
    {
        // ADR-IC-018 §P5: an event that declared autorenewalpolicy (carried on the outbox row's
        // integration_headers column) gets it promoted to ce_autorenewalpolicy. The relay names no
        // key — it copies whatever the row declared.
        var headers = OutboxDrainer.BuildHeadersCore(
            Row(new Dictionary<string, string> { ["autorenewalpolicy"] = "SAME_TERM_CURRENT_RATE" }),
            source: "urn:babelstone:engine");

        Assert.Equal("SAME_TERM_CURRENT_RATE", HeaderValue(headers, "ce_autorenewalpolicy"));
        // The standard CE set is still present and unaffected.
        Assert.Equal("1.0", HeaderValue(headers, "ce_specversion"));
        Assert.Equal("com.bank.deposits.DepositMatured", HeaderValue(headers, "ce_type"));
    }

    [Fact]
    public void Build_headers_emits_no_extension_header_when_the_row_declared_none()
    {
        // A row with null integration_headers (the common case, every pre-seam row) gets only the
        // standard CE header set — no ce_autorenewalpolicy.
        var headers = OutboxDrainer.BuildHeadersCore(Row(integrationHeaders: null), source: "urn:babelstone:engine");

        Assert.Null(HeaderValue(headers, "ce_autorenewalpolicy"));
        Assert.Equal("term_deposit", HeaderValue(headers, "ce_aggregatetype"));
    }

    [Fact]
    public void Build_headers_promotes_each_declared_extension_attribute_generically()
    {
        // The seam copies the whole declared map, naming no key — so a multi-entry map promotes every
        // entry as ce_<key>. This is what makes the seam family-agnostic.
        var headers = OutboxDrainer.BuildHeadersCore(
            Row(new Dictionary<string, string> { ["foo"] = "1", ["barbaz"] = "2" }),
            source: "urn:babelstone:engine");

        Assert.Equal("1", HeaderValue(headers, "ce_foo"));
        Assert.Equal("2", HeaderValue(headers, "ce_barbaz"));
    }

    [Fact]
    public void Confluent_wire_format_is_magic_byte_then_big_endian_schema_id_then_value()
    {
        byte[] avro = [0xAA, 0xBB, 0xCC];
        var framed = OutboxDrainer.ToConfluentWireFormat(schemaId: 7, avroValue: avro);

        Assert.Equal(0x00, framed[0]);                                       // magic byte
        Assert.Equal(7, BinaryPrimitives.ReadInt32BigEndian(framed.AsSpan(1, 4))); // schema_id, big-endian
        Assert.Equal(avro, framed[5..]);                                     // avro value follows
        Assert.Equal(5 + avro.Length, framed.Length);
    }

    [Theory]
    // The catalogued (relay-publishable) set after the ADR-IC-017 §P4 promotion pass:
    // DepositConstituted, InterestPaid, DepositMatured. The reverse-DNS transform itself is generic
    // (it strips the aggregate prefix for ANY event_type), so the examples track the promoted set.
    [InlineData("term_deposit.DepositConstituted", "com.bank.deposits.DepositConstituted")]
    [InlineData("term_deposit.InterestPaid", "com.bank.deposits.InterestPaid")]
    [InlineData("term_deposit.DepositMatured", "com.bank.deposits.DepositMatured")]
    public void Reverse_dns_ce_type_strips_aggregate_prefix(string eventType, string expected)
        => Assert.Equal(expected, OutboxDrainer.ReverseDnsType(eventType));
}
