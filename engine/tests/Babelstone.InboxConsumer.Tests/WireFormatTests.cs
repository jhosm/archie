using Babelstone.Families.TermDeposit;
using Xunit;

namespace Babelstone.InboxConsumer.Tests;

/// <summary>
/// Pure (no-container) tests for the consumer's wire-format un-framing and ce_type → record-name
/// mapping — the exact inverses of the relay's framing (ADR-IC-002 §P3) and reverse-DNS ce_type
/// (ADR-IC-008). Default CI lane: the un-framing must never silently drift from the framing, the
/// same way the producer-side WireFormatTests guard the forward direction.
/// </summary>
public sealed class WireFormatTests
{
    [Fact]
    public void Unframe_is_the_inverse_of_the_relay_framing()
    {
        byte[] avro = [0xAA, 0xBB, 0xCC];
        // Frame it exactly as the outbox relay does (the documented wire contract), then un-frame and
        // assert we get the value back — the consumer's TryUnframe is the inverse of the relay framing.
        var framed = WireFormat.Frame(schemaId: 7, avroValue: avro);

        Assert.True(InboxPump.TryUnframe(framed, out var value));
        Assert.Equal(avro, value.ToArray());
        // The embedded schema_id survives for diagnostics (not used by decode).
        Assert.Equal(7, InboxPump.ReadSchemaId(framed));
    }

    [Fact]
    public void Unframe_rejects_a_bad_magic_byte()
    {
        byte[] notWireFormat = [0x01, 0x00, 0x00, 0x00, 0x07, 0xAA];
        Assert.False(InboxPump.TryUnframe(notWireFormat, out _));
    }

    [Fact]
    public void Unframe_rejects_a_too_short_value()
    {
        byte[] tooShort = [0x00, 0x00, 0x00]; // magic byte but truncated header
        Assert.False(InboxPump.TryUnframe(tooShort, out _));
    }

    [Theory]
    [InlineData("com.bank.deposits.DepositConstituted", "DepositConstituted")]
    [InlineData("com.bank.deposits.DepositMatured", "DepositMatured")]
    [InlineData("WithholdingApplied", "WithholdingApplied")] // no dots: the whole string is the record name
    [InlineData("", "")]
    public void RecordName_is_the_last_segment_of_a_reverse_dns_ce_type(string ceType, string expected)
        => Assert.Equal(expected, InboxPump.RecordName(ceType));

    [Fact]
    public void Record_name_round_trips_the_relays_reverse_dns_ce_type()
    {
        // The relay maps "term_deposit.DepositMatured" → "com.bank.deposits.DepositMatured"; the
        // consumer must recover the record name the codec + resolver key on from that ce_type.
        var ceType = WireFormat.ReverseDnsType("term_deposit.DepositMatured");
        Assert.Equal(nameof(DepositMatured), InboxPump.RecordName(ceType));
    }
}
