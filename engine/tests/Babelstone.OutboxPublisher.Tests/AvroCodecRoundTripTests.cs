using Avro;
using Avro.Generic;
using Avro.IO;
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

    // --- ADR-IC-002 §P2 optional-field ([null,T] union) path --------------------------------------
    // None of the 4 AT_MATURITY events carry an optional field, so these drive the codec's value
    // helpers (ToAvro/FromAvro/FieldName/RequireField) directly against a real null-first union
    // schema, wire-encoding with the SAME Apache.Avro writer/reader the serializer uses. The point is
    // that null is now EMITTED into [null,T] (not rejected) and read back through to the nullable
    // target — the precise behaviour change of this lane.

    // A record with two OPTIONAL fields: a [null,string] and a [null,long] (the Money substrate),
    // both null-first with default null (ADR-IC-002 §P2). Built in code (not a catalogued .avsc) so
    // the test exercises the optional path without minting a fixture event family.
    private static readonly RecordSchema OptionalFieldSchema = (RecordSchema)Schema.Parse(
        """
        {
          "type": "record",
          "namespace": "test.optional",
          "name": "OptionalFieldsProbe",
          "fields": [
            { "name": "note",         "type": ["null", "string"], "default": null },
            { "name": "bonus_cents",  "type": ["null", "long"],   "default": null }
          ]
        }
        """);

    // Mirror the serializer's WriteAvro/ReadAvro (writer schema == reader schema, same-version
    // assumption) so the value crosses the real [null,T] union on the wire.
    private static GenericRecord WireRoundTrip(GenericRecord record)
    {
        var writer = new GenericDatumWriter<GenericRecord>(OptionalFieldSchema);
        using var stream = new MemoryStream();
        var encoder = new BinaryEncoder(stream);
        writer.Write(record, encoder);
        encoder.Flush();

        var reader = new GenericDatumReader<GenericRecord>(OptionalFieldSchema, OptionalFieldSchema);
        using var input = new MemoryStream(stream.ToArray(), writable: false);
        return reader.Read(null!, new BinaryDecoder(input));
    }

    [Theory]
    [InlineData("a populated note")]
    [InlineData(null)]
    public void Optional_string_round_trips_both_null_and_value_through_null_string_union(string? note)
    {
        // RequireField must TOLERATE the optional field: a [null,string] field is declared by name,
        // so it is NOT a missing-required-field error (the union/null value is legal).
        AvroEventSerializer.RequireField(OptionalFieldSchema, "note", typeof(OptionalFieldsProbe));

        var record = new GenericRecord(OptionalFieldSchema);
        record.Add("note", AvroEventSerializer.ToAvro(note));            // null → Avro null (no throw)
        record.Add("bonus_cents", AvroEventSerializer.ToAvro(null));     // keep the other branch null

        var decoded = WireRoundTrip(record);
        Assert.True(decoded.TryGetValue("note", out var raw));
        var value = (string?)AvroEventSerializer.FromAvro(raw, typeof(string));

        Assert.Equal(note, value); // both the null and the non-null survive the [null,string] wire
    }

    [Fact]
    public void Optional_money_null_path_round_trips_without_ToAvro_throwing()
    {
        // The pre-lane ToAvro threw on null ("v1 events are all-required"); the §P2 path must EMIT
        // null into [null,long]. A nullable Money? field maps to the same _cents suffix.
        Assert.Equal("bonus_cents", AvroEventSerializer.FieldName("Bonus", typeof(Money?)));

        Money? noBonus = null;
        var toAvro = Record.Exception(() => AvroEventSerializer.ToAvro(noBonus));
        Assert.Null(toAvro);                                            // ToAvro(null) does NOT throw

        var record = new GenericRecord(OptionalFieldSchema);
        record.Add("note", AvroEventSerializer.ToAvro(null));
        record.Add("bonus_cents", AvroEventSerializer.ToAvro(noBonus)); // null Money → Avro null

        var decoded = WireRoundTrip(record);
        Assert.True(decoded.TryGetValue("bonus_cents", out var raw));
        var value = (Money?)AvroEventSerializer.FromAvro(raw, typeof(Money?));

        Assert.Null(value); // the null Money round-trips as null (not Money.Zero)
    }

    [Fact]
    public void Optional_money_value_path_round_trips_through_null_long_union()
    {
        // The populated branch of the same optional Money?: a non-null value must survive as itself.
        Money? bonus = new Money(12_345);

        var record = new GenericRecord(OptionalFieldSchema);
        record.Add("note", AvroEventSerializer.ToAvro(null));
        record.Add("bonus_cents", AvroEventSerializer.ToAvro(bonus));   // Money → Avro long (in the union)

        var decoded = WireRoundTrip(record);
        Assert.True(decoded.TryGetValue("bonus_cents", out var raw));
        var value = (Money?)AvroEventSerializer.FromAvro(raw, typeof(Money?));

        Assert.Equal(bonus, value);
    }

    private sealed record UnknownEvent : DomainEvent;

    // A test-only shape whose name labels the optional-field probe schema; never catalogued.
    private sealed record OptionalFieldsProbe(string? Note, Money? Bonus) : DomainEvent;
}
