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

    // ---- Schema RESOLUTION (writer != reader) — ADR-IC-002 §Consequences BACKWARD evolution ----------

    [Fact]
    public void Decode_resolves_a_newer_writer_schema_with_an_added_defaulted_field_against_the_older_reader()
    {
        // The forward-only/BACKWARD-evolution path (ADR-IC-002 §Consequences): a producer ships a NEWER
        // writer schema that ADDS a trailing field WITH a default (a BACKWARD-compatible additive
        // change). This consumer still runs the OLDER reader schema (the local catalog). Decoding
        // writer→reader via Avro schema resolution must DROP the writer-only field and recover the two
        // shared fields exactly — instead of mis-decoding → poison (the consumer-path limitation now fixed).
        //
        // Deliberately NON-null-requiring: the added field is a plain string with a default, so this
        // does NOT depend on the parallel nullable-union lane (feat/avro-nullable-union).
        var serializer = NewSerializer();
        var catalog = new AvroSchemaCatalog();
        var readerSchema = catalog.ForRecordName(nameof(InterestAccrued)).Schema;

        // The writer's NEWER schema: the reader's two fields + a new defaulted trailing field the
        // reader does not know. (Hand-built so the test stands on the documented BACKWARD shape rather
        // than a future .avsc revision.)
        const string writerJson = """
            {
              "type": "record",
              "namespace": "deposits.term_deposit",
              "name": "InterestAccrued",
              "fields": [
                { "name": "gross_interest_cents", "type": "long" },
                { "name": "as_of", "type": { "type": "int", "logicalType": "date" } },
                { "name": "accrual_method", "type": "string", "default": "ACT_360" }
              ]
            }
            """;
        var writerSchema = (Avro.RecordSchema)Avro.Schema.Parse(writerJson);

        // Write a record under the NEWER writer schema (the new field populated), framed as the bare
        // Avro value the wire carries.
        var grossCents = 30_417L;
        var asOf = new DateOnly(2026, 12, 31);
        var written = WriteUnderWriterSchema(writerSchema, record =>
        {
            record.Add("gross_interest_cents", grossCents);
            record.Add("as_of", asOf.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            record.Add("accrual_method", "ACT_365");
        });

        // Decode writer→reader: the resolver-driven path the inbox consumer uses. The writer-only
        // accrual_method is dropped; the shared fields decode to the original values.
        var decoded = (InterestAccrued)serializer.Decode(written, typeof(InterestAccrued), writerSchema);

        Assert.Equal(new InterestAccrued(new Money(grossCents), asOf), decoded);
    }

    [Fact]
    public void Decode_resolves_a_writer_schema_with_reordered_fields_against_the_reader()
    {
        // Avro schema resolution matches fields by NAME, not position — so a writer that REORDERS the
        // fields (another BACKWARD-compatible, non-null-requiring change) must still decode correctly
        // against the reader. A position-blind decode (the old writer == reader assumption applied to a
        // reordered writer) would read the bytes in the wrong order → garbage/poison; resolution fixes
        // it. Reorder is chosen precisely because it does NOT need a nullable union (feat/avro-nullable-union).
        var serializer = NewSerializer();

        // The writer's schema: the reader's fields, ORDER SWAPPED.
        const string writerJson = """
            {
              "type": "record",
              "namespace": "deposits.term_deposit",
              "name": "InterestAccrued",
              "fields": [
                { "name": "as_of", "type": { "type": "int", "logicalType": "date" } },
                { "name": "gross_interest_cents", "type": "long" }
              ]
            }
            """;
        var writerSchema = (Avro.RecordSchema)Avro.Schema.Parse(writerJson);

        var grossCents = 12_345L;
        var asOf = new DateOnly(2026, 6, 30);
        var written = WriteUnderWriterSchema(writerSchema, record =>
        {
            record.Add("as_of", asOf.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            record.Add("gross_interest_cents", grossCents);
        });

        var decoded = (InterestAccrued)serializer.Decode(written, typeof(InterestAccrued), writerSchema);

        Assert.Equal(new InterestAccrued(new Money(grossCents), asOf), decoded);
    }

    [Fact(Skip = "Lands once feat/avro-nullable-union (evfk) merges: the codec needs nullable-union " +
                 "support (FromAvro/ToAvro) for an ADDED OPTIONAL field. This case is the BACKWARD " +
                 "evolution where the WRITER adds a nullable field the older reader drops — orthogonal " +
                 "to schema RESOLUTION (already covered above), so it is decoupled here rather than hard-depended on.")]
    public void Decode_resolves_a_newer_writer_schema_with_an_added_optional_nullable_field()
    {
        // Intentionally a no-op skeleton: enabling it requires the nullable-union codec support the
        // evfk lane adds. The defaulted-field and reorder cases above already prove writer→reader
        // resolution works WITHOUT that lane, so this lane stays auto-mergeable.
    }

    /// <summary>Write a GenericRecord under a specific WRITER schema and return the bare Avro value
    /// bytes — the producer side of a writer != reader resolution test (no Confluent framing; the
    /// codec's Decode takes the bare value).</summary>
    private static byte[] WriteUnderWriterSchema(Avro.RecordSchema writerSchema, Action<Avro.Generic.GenericRecord> populate)
    {
        var record = new Avro.Generic.GenericRecord(writerSchema);
        populate(record);
        var writer = new Avro.Generic.GenericDatumWriter<Avro.Generic.GenericRecord>(writerSchema);
        using var stream = new MemoryStream();
        var encoder = new Avro.IO.BinaryEncoder(stream);
        writer.Write(record, encoder);
        encoder.Flush();
        return stream.ToArray();
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
