using Babelstone.Engine;
using Babelstone.Engine.Avro;
using Babelstone.Families.TermDeposit;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.OutboxPublisher.Tests;

/// <summary>
/// Pure (no-container) round-trip tests for the Avro codec: Encode→Decode is lossless for
/// every term-deposit event, and the Money/Guid/DateOnly mappings preserve value exactly.
/// Runs in the DEFAULT CI lane (no Integration trait) — the codec mapping is the part that
/// must never silently drift, independent of Redpanda.
/// </summary>
public sealed class AvroCodecRoundTripTests
{
    // A stub resolver: the round-trip does not need a real Schema Registry, only a stable id.
    private sealed class StubSchemaIdResolver : ISchemaIdResolver
    {
        public int ResolveSchemaId(string eventType) => 1;
    }

    private static AvroEventSerializer NewSerializer()
        => new(new AvroSchemaCatalog(), new StubSchemaIdResolver());

    [Fact]
    public void DepositConstituted_round_trips_with_money_guid_and_dateonly_preserved()
    {
        var serializer = NewSerializer();
        var original = new DepositConstituted(
            DepositId: Guid.NewGuid(),
            Principal: new Money(1_000_000),
            TanBasisPoints: 300,
            RateSheetVersionId: "rs-2026-01",
            TermDays: 364,
            StartDate: new DateOnly(2026, 1, 1),
            MaturityDate: new DateOnly(2026, 12, 31),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE");

        var encoded = serializer.Encode(original);
        Assert.Equal(1, encoded.SchemaId);
        var decoded = (DepositConstituted)serializer.Decode(encoded.Bytes, typeof(DepositConstituted));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void InterestAccrued_round_trips()
    {
        var serializer = NewSerializer();
        var original = new InterestAccrued(new Money(30_417), new DateOnly(2026, 12, 31));

        var decoded = (InterestAccrued)serializer.Decode(serializer.Encode(original).Bytes, typeof(InterestAccrued));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void WithholdingApplied_round_trips()
    {
        var serializer = NewSerializer();
        var original = new WithholdingApplied(new Money(8_517), new Money(21_900));

        var decoded = (WithholdingApplied)serializer.Decode(serializer.Encode(original).Bytes, typeof(WithholdingApplied));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void DepositMatured_round_trips()
    {
        var serializer = NewSerializer();
        var original = new DepositMatured(
            PrincipalReturned: new Money(1_000_000),
            NetInterestPaid: new Money(21_900),
            TotalPayout: new Money(1_021_900),
            MaturedOn: new DateOnly(2026, 12, 31));

        var decoded = (DepositMatured)serializer.Decode(serializer.Encode(original).Bytes, typeof(DepositMatured));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Subjects_follow_ADR_IC_002_P1_naming()
    {
        var catalog = new AvroSchemaCatalog();

        Assert.Equal(
            "deposits.term_deposit.DepositConstituted-value",
            catalog.ForEventType("term_deposit.DepositConstituted").Subject);
        Assert.Equal(
            "deposits.term_deposit.DepositMatured-value",
            catalog.ForEventType("term_deposit.DepositMatured").Subject);
    }

    [Fact]
    public void Unknown_event_type_fails_loud_on_encode()
    {
        var serializer = NewSerializer();
        var ex = Assert.Throws<InvalidOperationException>(() => serializer.Encode(new UnknownEvent()));
        Assert.Contains("No Avro schema catalogued", ex.Message);
        Assert.Contains(nameof(UnknownEvent), ex.Message);
    }

    private sealed record UnknownEvent : DomainEvent;
}
