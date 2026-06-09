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
    public void DepositConstituted_round_trips_the_periodic_payment_period_additive_field()
    {
        // F.1 added payment_period_months to DepositConstituted (additive, default 0). A PERIODIC
        // deposit carrying a non-zero cadence must survive the wire — proving the .avsc field, not
        // just the C# record, carries it (otherwise the field would silently drop to 0).
        var serializer = NewSerializer();
        var original = new DepositConstituted(
            DepositId: Guid.NewGuid(),
            Principal: new Money(49_900_000),
            TanBasisPoints: 325,
            RateSheetVersionId: "rs-2026-01",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 1),
            MaturityDate: new DateOnly(2027, 1, 1),
            InterestVariant: "PERIODIC",
            AutoRenewalPolicy: "NONE",
            PaymentPeriodMonths: 3);

        var decoded = (DepositConstituted)serializer.Decode(
            serializer.Encode(original).Bytes, typeof(DepositConstituted));

        Assert.Equal(original, decoded);
        Assert.Equal(3, decoded.PaymentPeriodMonths);
    }

    [Fact]
    public void DepositConstituted_decodes_a_pre_v794_record_as_the_empty_product_code_default()
    {
        // bd babelstone-v794 added product_code to DepositConstituted (additive, default ""). A
        // record written before v794 never carried it; constructing the C# record WITHOUT a
        // ProductCode (the default "") and round-tripping proves the .avsc default decodes — old
        // records still replay as "" rather than failing to decode (forward-only evolution,
        // ADR-IC-002 §P3, the same precedent as payment_period_months).
        var serializer = NewSerializer();
        var preV794 = new DepositConstituted(
            DepositId: Guid.NewGuid(),
            Principal: new Money(1_000_000),
            TanBasisPoints: 300,
            RateSheetVersionId: "rs-2026-01",
            TermDays: 364,
            StartDate: new DateOnly(2026, 1, 1),
            MaturityDate: new DateOnly(2026, 12, 31),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE");

        var decoded = (DepositConstituted)serializer.Decode(
            serializer.Encode(preV794).Bytes, typeof(DepositConstituted));

        Assert.Equal(preV794, decoded);
        Assert.Equal("", decoded.ProductCode);
    }

    [Fact]
    public void DepositConstituted_round_trips_the_catalogue_product_code_additive_field()
    {
        // bd babelstone-v794: a populated catalogue product_code must survive the wire — proving the
        // .avsc field, not just the C# record, carries it (otherwise the code would silently drop to
        // the "" default and the D.4 read model would denormalize an empty dimension).
        var serializer = NewSerializer();
        var original = new DepositConstituted(
            DepositId: Guid.NewGuid(),
            Principal: new Money(1_000_000),
            TanBasisPoints: 300,
            RateSheetVersionId: "pt-deposits-2026.1",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 1),
            MaturityDate: new DateOnly(2027, 1, 1),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            PaymentPeriodMonths: 0,
            ProductCode: "dpz_pt_12m_juros_venc");

        var decoded = (DepositConstituted)serializer.Decode(
            serializer.Encode(original).Bytes, typeof(DepositConstituted));

        Assert.Equal(original, decoded);
        Assert.Equal("dpz_pt_12m_juros_venc", decoded.ProductCode);
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
