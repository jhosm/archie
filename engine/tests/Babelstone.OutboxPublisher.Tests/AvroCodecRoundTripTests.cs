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
    public void DepositConstituted_round_trips_the_role_and_funding_account_additive_fields()
    {
        // bd babelstone-mtto.5: populated role + funding_account must survive the wire — proving the
        // .avsc fields, not just the C# record, carry them (otherwise the engine could not recover the
        // renewal's (product, role) re-resolution + rollover funding from the closing deposit). Both are
        // STRUCTURAL: role is a pricing dimension, funding_account is an OPAQUE token (a reference, not
        // an IBAN) — references are allowed on the bus (ADR-PC-004 §P2), PII is not.
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
            AutoRenewalPolicy: "SAME_TERM_CURRENT_RATE",
            PaymentPeriodMonths: 0,
            ProductCode: "dpz_pt_12m_juros_venc",
            Role: "standard",
            FundingAccount: "PT50-DDA-001");

        var decoded = (DepositConstituted)serializer.Decode(
            serializer.Encode(original).Bytes, typeof(DepositConstituted));

        Assert.Equal(original, decoded);
        Assert.Equal("standard", decoded.Role);
        Assert.Equal("PT50-DDA-001", decoded.FundingAccount);
    }

    [Fact]
    public void DepositConstituted_round_trips_the_partial_withdrawal_policy_additive_fields()
    {
        // bd k6r8.8/qze9: the F.12 partial-withdrawal policy is PINNED on DepositConstituted at
        // constitution (like the rate) so a later config edit cannot change a live deposit's withdrawal
        // rights (ADR-PC-009). A populated policy must survive the wire — proving the .avsc fields, not
        // just the C# record, carry it (otherwise the withdrawal path would rebuild an Unrestricted
        // policy from dropped-to-0 gates). Structural config, not PII (ADR-PC-004 §P2).
        var serializer = NewSerializer();
        var original = new DepositConstituted(
            DepositId: Guid.NewGuid(),
            Principal: new Money(4_000_000),
            TanBasisPoints: 300,
            RateSheetVersionId: "pt-deposits-2026.1",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 1),
            MaturityDate: new DateOnly(2027, 1, 1),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            PaymentPeriodMonths: 0,
            ProductCode: "dpz_pt_12m_resgate_parcial",
            Role: "standard",
            FundingAccount: "PT50-DDA-001",
            MinWithdrawalCents: 50_000,
            MinRemainingBalanceCents: 100_000,
            CarenciaDays: 90);

        var decoded = (DepositConstituted)serializer.Decode(
            serializer.Encode(original).Bytes, typeof(DepositConstituted));

        Assert.Equal(original, decoded);
        Assert.Equal(50_000, decoded.MinWithdrawalCents);
        Assert.Equal(100_000, decoded.MinRemainingBalanceCents);
        Assert.Equal(90, decoded.CarenciaDays);
    }

    [Fact]
    public void DepositConstituted_decodes_a_pre_mtto5_record_as_the_empty_role_and_funding_defaults()
    {
        // bd babelstone-mtto.5 added role + funding_account (additive, default ""). A record written
        // before mtto.5 never carried them; constructing the C# record WITHOUT them (the "" defaults)
        // and round-tripping proves the .avsc defaults decode — old records still replay as "" rather
        // than failing to decode (forward-only evolution, ADR-IC-002 §P3, the same precedent as
        // product_code). A renewal of such a deposit defaults the empty role to standard and fails loud
        // on the empty funding token (TermDepositConstitutionService / decider), not here.
        var serializer = NewSerializer();
        var preFields = new DepositConstituted(
            DepositId: Guid.NewGuid(),
            Principal: new Money(1_000_000),
            TanBasisPoints: 300,
            RateSheetVersionId: "pt-deposits-2026.1",
            TermDays: 364,
            StartDate: new DateOnly(2026, 1, 1),
            MaturityDate: new DateOnly(2026, 12, 31),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE");

        var decoded = (DepositConstituted)serializer.Decode(
            serializer.Encode(preFields).Bytes, typeof(DepositConstituted));

        Assert.Equal(preFields, decoded);
        Assert.Equal("", decoded.Role);
        Assert.Equal("", decoded.FundingAccount);
    }

    [Fact]
    public void InterestPaid_round_trips_with_guid_money_legs_and_dateonly_preserved()
    {
        // InterestPaid is the ADR-IC-017 §P4 promoted coupon/advance payout fact — the integration
        // event that replaced the de-promoted InterestAccrued/WithholdingApplied accrual mechanics on
        // the bus. It carries the deposit reference + the three money legs (gross/withheld/net) + the
        // payment date; the codec must round-trip the uuid, the three longs, and the date logical type.
        var serializer = NewSerializer();
        var original = new InterestPaid(
            DepositId: Guid.NewGuid(),
            GrossInterest: new Money(30_417),
            WithholdingTax: new Money(8_517),
            NetInterest: new Money(21_900),
            PaidOn: new DateOnly(2026, 12, 31));

        var decoded = (InterestPaid)serializer.Decode(serializer.Encode(original).Bytes, typeof(InterestPaid));

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
    public void DepositMatured_round_trips_the_auto_renewal_policy_additive_field()
    {
        // The CE-header seam (ADR-IC-018 §P5) added auto_renewal_policy to DepositMatured's .avsc as an
        // optional [null,string] field (null default, BACKWARD-compatible per ADR-IC-002 §P2). A deposit
        // carrying a real policy must survive the wire — proving the .avsc field, not just the C# record,
        // carries it (otherwise the policy would silently drop and the relay would promote no
        // ce_autorenewalpolicy header). The C# field is a non-nullable string default ""; a non-empty
        // value rides the union's string branch and round-trips exactly.
        var serializer = NewSerializer();
        var original = new DepositMatured(
            PrincipalReturned: new Money(1_000_000),
            NetInterestPaid: new Money(21_900),
            TotalPayout: new Money(1_021_900),
            MaturedOn: new DateOnly(2026, 12, 31),
            AutoRenewalPolicy: "SAME_TERM_CURRENT_RATE");

        var decoded = (DepositMatured)serializer.Decode(serializer.Encode(original).Bytes, typeof(DepositMatured));

        Assert.Equal(original, decoded);
        Assert.Equal("SAME_TERM_CURRENT_RATE", decoded.AutoRenewalPolicy);
    }

    [Fact]
    public void DepositMatured_decodes_pre_field_bytes_written_without_auto_renewal_policy()
    {
        // BACKWARD compatibility (ADR-IC-002 §P2): bytes written by an OLD producer whose DepositMatured
        // schema has NO auto_renewal_policy field must still decode against the NEW reader schema. Avro
        // resolution fills the missing field from its schema default (null), so the decoded event carries
        // a null policy — for which DepositMatured.IntegrationHeaders declares no extension header, the
        // correct behaviour for a pre-seam stream. This is what guarantees the additive field does not
        // poison historical DepositMatured streams.
        var serializer = NewSerializer();

        // The OLD writer schema: DepositMatured's four original fields, NO auto_renewal_policy.
        const string writerJson = """
            {
              "type": "record",
              "namespace": "deposits.term_deposit",
              "name": "DepositMatured",
              "fields": [
                { "name": "principal_returned_cents", "type": "long" },
                { "name": "net_interest_paid_cents", "type": "long" },
                { "name": "total_payout_cents", "type": "long" },
                { "name": "matured_on", "type": { "type": "int", "logicalType": "date" } }
              ]
            }
            """;
        var writerSchema = (Avro.RecordSchema)Avro.Schema.Parse(writerJson);

        var maturedOn = new DateOnly(2026, 12, 31);
        var written = WriteUnderWriterSchema(writerSchema, record =>
        {
            record.Add("principal_returned_cents", 1_000_000L);
            record.Add("net_interest_paid_cents", 21_900L);
            record.Add("total_payout_cents", 1_021_900L);
            record.Add("matured_on", maturedOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        });

        // Decode old-writer → new-reader: the resolver-driven path the inbox consumer uses. The missing
        // auto_renewal_policy falls to the reader schema's null default.
        var decoded = (DepositMatured)serializer.Decode(written, typeof(DepositMatured), writerSchema);

        Assert.Equal(new Money(1_000_000), decoded.PrincipalReturned);
        Assert.Equal(new Money(21_900), decoded.NetInterestPaid);
        Assert.Equal(new Money(1_021_900), decoded.TotalPayout);
        Assert.Equal(maturedOn, decoded.MaturedOn);
        Assert.Null(decoded.AutoRenewalPolicy);            // absent field → null (schema default)
        Assert.Null(decoded.IntegrationHeaders);           // null/empty policy declares no ce_ header
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
        // does NOT depend on the parallel nullable-union lane (feat/avro-nullable-union). Driven on
        // InterestPaid — the ADR-IC-017 §P4 promoted coupon payout event — now that the de-promoted
        // InterestAccrued has no catalogued reader schema to resolve against.
        var serializer = NewSerializer();

        // The writer's NEWER schema: the reader's InterestPaid fields + a new defaulted trailing field
        // the reader does not know. (Hand-built so the test stands on the documented BACKWARD shape
        // rather than a future .avsc revision.)
        const string writerJson = """
            {
              "type": "record",
              "namespace": "deposits.term_deposit",
              "name": "InterestPaid",
              "fields": [
                { "name": "deposit_id", "type": { "type": "string", "logicalType": "uuid" } },
                { "name": "gross_interest_cents", "type": "long" },
                { "name": "withholding_tax_cents", "type": "long" },
                { "name": "net_interest_cents", "type": "long" },
                { "name": "paid_on", "type": { "type": "int", "logicalType": "date" } },
                { "name": "payment_method", "type": "string", "default": "COUPON" }
              ]
            }
            """;
        var writerSchema = (Avro.RecordSchema)Avro.Schema.Parse(writerJson);

        // Write a record under the NEWER writer schema (the new field populated), framed as the bare
        // Avro value the wire carries.
        var depositId = Guid.NewGuid();
        var grossCents = 30_417L;
        var taxCents = 8_517L;
        var netCents = 21_900L;
        var paidOn = new DateOnly(2026, 12, 31);
        var written = WriteUnderWriterSchema(writerSchema, record =>
        {
            record.Add("deposit_id", depositId);
            record.Add("gross_interest_cents", grossCents);
            record.Add("withholding_tax_cents", taxCents);
            record.Add("net_interest_cents", netCents);
            record.Add("paid_on", paidOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            record.Add("payment_method", "ADVANCE");
        });

        // Decode writer→reader: the resolver-driven path the inbox consumer uses. The writer-only
        // payment_method is dropped; the shared fields decode to the original values.
        var decoded = (InterestPaid)serializer.Decode(written, typeof(InterestPaid), writerSchema);

        Assert.Equal(
            new InterestPaid(depositId, new Money(grossCents), new Money(taxCents), new Money(netCents), paidOn),
            decoded);
    }

    [Fact]
    public void Decode_resolves_a_writer_schema_with_reordered_fields_against_the_reader()
    {
        // Avro schema resolution matches fields by NAME, not position — so a writer that REORDERS the
        // fields (another BACKWARD-compatible, non-null-requiring change) must still decode correctly
        // against the reader. A position-blind decode (the old writer == reader assumption applied to a
        // reordered writer) would read the bytes in the wrong order → garbage/poison; resolution fixes
        // it. Reorder is chosen precisely because it does NOT need a nullable union (feat/avro-nullable-union).
        // Driven on the ADR-IC-017 §P4 promoted InterestPaid (the de-promoted InterestAccrued has no
        // catalogued reader schema to resolve against).
        var serializer = NewSerializer();

        // The writer's schema: the reader's InterestPaid fields, ORDER SHUFFLED.
        const string writerJson = """
            {
              "type": "record",
              "namespace": "deposits.term_deposit",
              "name": "InterestPaid",
              "fields": [
                { "name": "paid_on", "type": { "type": "int", "logicalType": "date" } },
                { "name": "net_interest_cents", "type": "long" },
                { "name": "deposit_id", "type": { "type": "string", "logicalType": "uuid" } },
                { "name": "gross_interest_cents", "type": "long" },
                { "name": "withholding_tax_cents", "type": "long" }
              ]
            }
            """;
        var writerSchema = (Avro.RecordSchema)Avro.Schema.Parse(writerJson);

        var depositId = Guid.NewGuid();
        var grossCents = 12_345L;
        var taxCents = 3_456L;
        var netCents = 8_889L;
        var paidOn = new DateOnly(2026, 6, 30);
        var written = WriteUnderWriterSchema(writerSchema, record =>
        {
            record.Add("paid_on", paidOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            record.Add("net_interest_cents", netCents);
            record.Add("deposit_id", depositId);
            record.Add("gross_interest_cents", grossCents);
            record.Add("withholding_tax_cents", taxCents);
        });

        var decoded = (InterestPaid)serializer.Decode(written, typeof(InterestPaid), writerSchema);

        Assert.Equal(
            new InterestPaid(depositId, new Money(grossCents), new Money(taxCents), new Money(netCents), paidOn),
            decoded);
    }

    [Fact]
    public void Decode_resolves_a_newer_writer_schema_with_an_added_optional_nullable_field()
    {
        // The BACKWARD-evolution case the evfk lane (ADR-IC-002 §P2, PR #121) unblocked: a producer
        // ships a NEWER writer schema that ADDS a trailing OPTIONAL [null,T] field (null-first union,
        // default null) — a BACKWARD-compatible additive change. This consumer still runs the OLDER
        // reader schema (its local catalog), which has no such field. Decoding writer→reader via Avro
        // schema resolution must DROP the writer-only optional field and recover the two shared fields
        // exactly. Without the codec's nullable-union support, the [null,T] union on the wire would
        // mis-decode → poison; here it resolves cleanly. This is the OPTIONAL analogue of the
        // defaulted-field case above (which used a plain string default, not a union). Driven on the
        // ADR-IC-017 §P4 promoted InterestPaid (the de-promoted InterestAccrued has no catalogued
        // reader schema to resolve against).
        var serializer = NewSerializer();

        // The writer's NEWER schema: the reader's InterestPaid fields + a new trailing OPTIONAL
        // [null,string] field (null-first + default null, the ADR-IC-002 §P2 shape) the reader does
        // not know.
        const string writerJson = """
            {
              "type": "record",
              "namespace": "deposits.term_deposit",
              "name": "InterestPaid",
              "fields": [
                { "name": "deposit_id", "type": { "type": "string", "logicalType": "uuid" } },
                { "name": "gross_interest_cents", "type": "long" },
                { "name": "withholding_tax_cents", "type": "long" },
                { "name": "net_interest_cents", "type": "long" },
                { "name": "paid_on", "type": { "type": "int", "logicalType": "date" } },
                { "name": "coupon_note", "type": ["null", "string"], "default": null }
              ]
            }
            """;
        var writerSchema = (Avro.RecordSchema)Avro.Schema.Parse(writerJson);

        // Write under the NEWER writer schema with the optional field PRESENT (a non-null value), to
        // prove the [null,T] union is materialised on the wire and still dropped on resolution — not
        // merely absent. The bare Avro value bytes are the wire substrate (no Confluent framing).
        var depositId = Guid.NewGuid();
        var grossCents = 30_417L;
        var taxCents = 8_517L;
        var netCents = 21_900L;
        var paidOn = new DateOnly(2026, 12, 31);
        var written = WriteUnderWriterSchema(writerSchema, record =>
        {
            record.Add("deposit_id", depositId);
            record.Add("gross_interest_cents", grossCents);
            record.Add("withholding_tax_cents", taxCents);
            record.Add("net_interest_cents", netCents);
            record.Add("paid_on", paidOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            record.Add("coupon_note", "carried-over coupon");
        });

        // Decode writer→reader: the resolver-driven path the inbox consumer uses. The writer-only
        // optional coupon_note is dropped; the shared fields decode to the original values.
        var decoded = (InterestPaid)serializer.Decode(written, typeof(InterestPaid), writerSchema);

        Assert.Equal(
            new InterestPaid(depositId, new Money(grossCents), new Money(taxCents), new Money(netCents), paidOn),
            decoded);
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

    [Fact]
    public void Null_on_a_required_field_fails_loud_with_the_field_name_on_encode()
    {
        // A null on a REQUIRED (non-[null,T]) field must surface a clear, field-named error — NOT a
        // bare NullReferenceException deep in Apache.Avro's writer. ToAvro(null) returns Avro null for
        // the optional [null,T] path (ADR-IC-002 §P2), so ToRecord pre-checks the schema field and
        // fails here when the field is required. RateSheetVersionId is a plain Avro string (required).
        var serializer = NewSerializer();
        var withNullRequired = new DepositConstituted(
            DepositId: Guid.NewGuid(),
            Principal: new Money(1_000_000),
            TanBasisPoints: 300,
            RateSheetVersionId: null!,
            TermDays: 364,
            StartDate: new DateOnly(2026, 1, 1),
            MaturityDate: new DateOnly(2026, 12, 31),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE");

        var ex = Assert.Throws<InvalidOperationException>(() => serializer.Encode(withNullRequired));
        Assert.Contains(nameof(DepositConstituted.RateSheetVersionId), ex.Message); // the C# field name
        Assert.Contains("rate_sheet_version_id", ex.Message);                       // the Avro field name
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
