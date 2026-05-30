using System.Buffers.Binary;
using Xunit;

namespace Babelstone.OutboxPublisher.Tests;

/// <summary>
/// Pure (no-container) tests for the relay's two pure transforms: the Confluent wire-format
/// framing (ADR-IC-002 §P3) and the reverse-DNS ce_type mapping (ADR-IC-008). Default CI lane.
/// </summary>
public sealed class WireFormatTests
{
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
    [InlineData("term_deposit.DepositConstituted", "com.bank.deposits.DepositConstituted")]
    [InlineData("term_deposit.InterestAccrued", "com.bank.deposits.InterestAccrued")]
    [InlineData("term_deposit.WithholdingApplied", "com.bank.deposits.WithholdingApplied")]
    [InlineData("term_deposit.DepositMatured", "com.bank.deposits.DepositMatured")]
    public void Reverse_dns_ce_type_strips_aggregate_prefix(string eventType, string expected)
        => Assert.Equal(expected, OutboxDrainer.ReverseDnsType(eventType));
}
