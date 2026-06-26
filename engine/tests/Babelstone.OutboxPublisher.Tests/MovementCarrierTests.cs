using System.Text.Json;
using Avro;
using Avro.Generic;
using Avro.IO;
using Babelstone.Engine;
using Babelstone.Engine.Avro;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.OutboxPublisher.Tests;

/// <summary>
/// The Movement CARRIER round-trips (ADR-PC-032): a money-moving event carries its money legs as a list
/// of <see cref="Movement"/> records INSIDE its own opaque payload — an Avro array-of-Movement-record
/// field, no new events-table column, no envelope change. These tests pin the bus-codec half of that
/// carry (the <c>MovementCarrier</c> in <see cref="AvroEventSerializer"/>): a list of movements survives
/// the wire losslessly, an empty list stays empty (not null), multiple movements keep order, the opaque
/// <c>account_ref</c> + the two closed enums round-trip, and the governed <c>_shared/Movement.avsc.json</c>
/// carrier shape stays in lockstep with the field names + enum symbols the codec emits.
/// </summary>
/// <remarks>
/// No family event carries movements yet (that is a sibling issue), so these drive the carrier against a
/// HAND-BUILT carrying-event schema + a test-only carrying <see cref="DomainEvent"/> — the same idiom
/// <c>AvroCodecRoundTripTests</c> uses for the optional-field probe. The wire round-trip uses the SAME
/// Apache.Avro <see cref="GenericDatumWriter{T}"/>/<see cref="GenericDatumReader{T}"/> the serializer
/// uses, so the array-of-record actually crosses the wire — not merely an in-memory object compare.
/// Pure (no container), default CI lane.
/// </remarks>
public sealed class MovementCarrierTests
{
    // The canonical nested Movement record, inlined as a carrying event's array `items` would inline it
    // (verbatim from contracts/avro/_shared/Movement.avsc.json). A carrying event's schema declares a
    // REQUIRED array field whose items is this record.
    private const string CarrierEventJson = """
        {
          "type": "record",
          "namespace": "test.movement",
          "name": "MoneyMoved",
          "fields": [
            { "name": "loan_id", "type": { "type": "string", "logicalType": "uuid" } },
            { "name": "movements", "type": { "type": "array", "items": {
              "type": "record",
              "name": "Movement",
              "fields": [
                { "name": "account_ref", "type": "string" },
                { "name": "direction", "type": { "type": "enum", "name": "MovementDirection", "symbols": ["Debit", "Credit"] } },
                { "name": "amount_cents", "type": "long" },
                { "name": "value_date", "type": { "type": "int", "logicalType": "date" } },
                { "name": "operation", "type": { "type": "enum", "name": "MovementOperation", "symbols": ["Disburse", "CollectInstallment", "PayMaturity", "PayCoupon", "PayEarlyTermination", "RepayEarly", "RolloverDebit"] } },
                { "name": "origin", "type": { "type": "enum", "name": "MovementOrigin", "symbols": ["Originated", "Observed"] } },
                { "name": "command_id", "type": { "type": "string", "logicalType": "uuid" } }
              ]
            } } }
          ]
        }
        """;

    private static readonly RecordSchema CarrierSchema = (RecordSchema)Schema.Parse(CarrierEventJson);

    // A test-only carrying event: a money-moving fact that carries the movement legs it caused. Never
    // catalogued — it stands in for a future family event (e.g. LoanDisbursed) so the carrier can be
    // proven without modifying a family decider (t7o3.14 is the spine primitive + carrier mechanism only).
    private sealed record MoneyMoved(Guid LoanId, IReadOnlyList<Movement>? Movements) : DomainEvent;

    private static readonly Guid CommandId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static Movement SampleDebit() => new(
        AccountRef: "acct-ref-opaque-001",
        Direction: SettlementDirection.Debit,
        Amount: new Money(1_000_000),
        ValueDate: new DateOnly(2026, 6, 1),
        Operation: MovementOperation.Disburse,
        Origin: MovementOrigin.Originated,
        CommandId: CommandId);

    private static Movement SampleCredit() => new(
        AccountRef: "acct-ref-opaque-002",
        Direction: SettlementDirection.Credit,
        Amount: new Money(2_500),
        ValueDate: new DateOnly(2027, 1, 15),
        Operation: MovementOperation.PayCoupon,
        Origin: MovementOrigin.Observed,
        CommandId: Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public void A_single_movement_round_trips_through_the_carrier_array()
    {
        // Each Movement is itself a record, so per-element value-equality is the lossless predicate (the
        // carrying event's IReadOnlyList is array-vs-List by construction, an irrelevant container shape).
        var movement = SampleDebit();
        var original = new MoneyMoved(Guid.NewGuid(), [movement]);

        var decoded = WireRoundTrip(original);

        Assert.Equal(original.LoanId, decoded.LoanId);
        Assert.NotNull(decoded.Movements);
        Assert.Single(decoded.Movements);
        Assert.Equal(movement, decoded.Movements[0]);            // every Movement field, incl. Money cents
        Assert.Equal("acct-ref-opaque-001", decoded.Movements[0].AccountRef);
        Assert.Equal(SettlementDirection.Debit, decoded.Movements[0].Direction);
        Assert.Equal(new Money(1_000_000), decoded.Movements[0].Amount);
        Assert.Equal(new DateOnly(2026, 6, 1), decoded.Movements[0].ValueDate);
        Assert.Equal(MovementOperation.Disburse, decoded.Movements[0].Operation);
        Assert.Equal(MovementOrigin.Originated, decoded.Movements[0].Origin);
        Assert.Equal(CommandId, decoded.Movements[0].CommandId);
    }

    [Fact]
    public void Multiple_movements_keep_declared_order_through_the_carrier_array()
    {
        // A renewal-shaped event carries a rollover DEBIT and an interest CREDIT — per-account order
        // matters (feature-design §6), so the carrier must preserve the declared order.
        var debit = SampleDebit();
        var credit = SampleCredit();
        var original = new MoneyMoved(Guid.NewGuid(), [debit, credit]);

        var decoded = WireRoundTrip(original);

        Assert.Equal(original.LoanId, decoded.LoanId);
        Assert.NotNull(decoded.Movements);
        Assert.Equal(new[] { debit, credit }, decoded.Movements);    // order + every field preserved
        Assert.Equal(MovementOperation.Disburse, decoded.Movements[0].Operation);
        Assert.Equal(MovementOperation.PayCoupon, decoded.Movements[1].Operation);
        Assert.Equal(MovementOrigin.Observed, decoded.Movements[1].Origin);
    }

    [Fact]
    public void A_no_movements_carrier_round_trips_as_the_canonical_empty_wire_array()
    {
        // An event with no money legs carries the EMPTY array on the WIRE (never null on the wire — a decoder
        // always reads an array). The two C# "no movements" representations — an empty list [] and the
        // record-default null — both encode to that same empty wire array, and the codec decodes the empty
        // wire array back to the CANONICAL C# "no movements" value, null (the record default), so a
        // movement-free event round-trips to identity. (bd t7o3.13: encode null|[] → [] wire → decode null.)
        var fromEmpty = WireRoundTrip(new MoneyMoved(Guid.NewGuid(), []));
        Assert.Null(fromEmpty.Movements);

        var fromNull = WireRoundTrip(new MoneyMoved(Guid.NewGuid(), null));
        Assert.Null(fromNull.Movements);
    }

    [Fact]
    public void A_null_carrier_normalizes_to_the_empty_wire_array()
    {
        // A null C# carrier is the idiomatic "no movements" value the IReadOnlyList<Movement>? = null record
        // default constructs with (a movement-free or pre-Movement event). The codec NORMALIZES it to the
        // empty wire array — the wire stays "[] never null" (a decoder always reads an array, never null)
        // without forcing every direct construction to spell out Movements: []. (bd t7o3.13.)
        var encoded = MovementCarrier.ToAvroArray(
            null, AvroEventSerializer.SchemaFieldType(CarrierSchema, "movements"), "Movements");

        var array = Assert.IsAssignableFrom<System.Collections.IEnumerable>(encoded);
        Assert.Empty(array.Cast<object>());
    }

    [Fact]
    public void IsCarrierParameter_matches_only_IReadOnlyList_of_Movement()
    {
        Assert.True(MovementCarrier.IsCarrierParameter(typeof(IReadOnlyList<Movement>)));
        Assert.False(MovementCarrier.IsCarrierParameter(typeof(IReadOnlyList<string>)));
        Assert.False(MovementCarrier.IsCarrierParameter(typeof(Movement)));
        Assert.False(MovementCarrier.IsCarrierParameter(typeof(List<Movement>)));
    }

    [Fact]
    public void The_governed_carrier_shape_matches_the_codec_field_names_and_enum_symbols()
    {
        // The governed _shared/Movement.avsc.json is the reviewable carrier shape; the codec emits the
        // nested Movement record by hand (MovementCarrier.ToMovementRecord). This pins the two together
        // so the governed shape cannot silently drift from what the codec writes.
        var carrierShape = LoadGovernedCarrierShape();

        var fieldNames = carrierShape.GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("name").GetString())
            .ToArray();
        Assert.Equal(
            new[] { "account_ref", "direction", "amount_cents", "value_date", "operation", "origin", "command_id" },
            fieldNames);

        // The closed enums' symbols must equal the C# enum member names verbatim (the codec writes
        // movement.Operation.ToString() / .Origin.ToString() / .Direction.ToString()).
        AssertEnumSymbols(carrierShape, "operation", Enum.GetNames<MovementOperation>());
        AssertEnumSymbols(carrierShape, "origin", Enum.GetNames<MovementOrigin>());
        AssertEnumSymbols(carrierShape, "direction", Enum.GetNames<SettlementDirection>());
    }

    [Fact]
    public void The_governed_carrier_shape_carries_no_pii_field()
    {
        // The nested Movement record is NOT a `.avsc`, so the authoritative EmitContractFitnessTests
        // `.avsc`-glob PII gate never reaches it — this is its ONLY PII guard. Hold it to EXACTLY the
        // authoritative rule (same PiiKeyFragments incl. `account`, same `_ref`/`_id` opaque-reference
        // exclusion) so account_ref passes as the references-allowed case (ADR-PC-004 §P2) while a future
        // genuine identity field (account_holder, iban, …) is still caught. Never-PII-on-the-durable-bus.
        var carrierShape = LoadGovernedCarrierShape();

        // The authoritative fragment set (EmitContractFitnessTests.PiiKeyFragments) verbatim.
        string[] piiFragments =
            ["nif", "iban", "account", "name", "email", "client", "phone", "address", "tax_id", "customer", "depositor", "heir"];

        foreach (var field in carrierShape.GetProperty("fields").EnumerateArray())
        {
            var name = field.GetProperty("name").GetString()!.ToLowerInvariant();
            var bad = piiFragments.FirstOrDefault(f => FieldNameCarriesPii(name, f));
            Assert.True(bad is null, $"carrier field '{name}' reads as PII fragment '{bad}' (ADR-PC-004 §P2)");
        }
    }

    // The authoritative EmitContractFitnessTests.FieldNameCarriesPii rule: a fragment match is PII
    // UNLESS the field is an opaque reference handle (ends in _ref / _id), the references-allowed case
    // (ADR-PC-004 §P2) — so account_ref / command_id pass while a bare `account`/`iban` identity field
    // is caught.
    private static bool FieldNameCarriesPii(string loweredFieldName, string fragment)
    {
        if (!loweredFieldName.Contains(fragment, StringComparison.Ordinal))
        {
            return false;
        }

        return !loweredFieldName.EndsWith("_ref", StringComparison.Ordinal)
            && !loweredFieldName.EndsWith("_id", StringComparison.Ordinal);
    }

    // Encode → wire → decode the carrier through MovementCarrier, using the SAME Apache.Avro
    // writer/reader the serializer uses, against the hand-built carrying-event schema. This drives the
    // exact carrier code (MovementCarrier.ToAvroArray/FromAvroArray) the codec's ToRecord/FromRecord
    // invoke, with the array-of-record actually crossing the wire — no catalogued .avsc needed (no family
    // event carries movements yet; t7o3.14 is the spine primitive + carrier mechanism only).
    private static MoneyMoved WireRoundTrip(MoneyMoved original)
    {
        var moneyMovedSchema = CarrierSchema;
        var movementsField = AvroEventSerializer.SchemaFieldType(moneyMovedSchema, "movements");

        // Build the carrying event GenericRecord exactly as the codec's ToRecord would: the loan_id
        // scalar + the Movement carrier array.
        var writeRecord = new GenericRecord(moneyMovedSchema);
        writeRecord.Add("loan_id", original.LoanId);
        writeRecord.Add("movements", MovementCarrier.ToAvroArray(original.Movements, movementsField, "Movements"));

        var writer = new GenericDatumWriter<GenericRecord>(moneyMovedSchema);
        using var stream = new MemoryStream();
        var encoder = new BinaryEncoder(stream);
        writer.Write(writeRecord, encoder);
        encoder.Flush();

        var reader = new GenericDatumReader<GenericRecord>(moneyMovedSchema, moneyMovedSchema);
        using var input = new MemoryStream(stream.ToArray(), writable: false);
        var readRecord = reader.Read(null!, new BinaryDecoder(input));

        var loanId = (Guid)readRecord["loan_id"];
        var movements = MovementCarrier.FromAvroArray(readRecord["movements"], "Movements");
        return new MoneyMoved(loanId, movements);
    }

    private static JsonElement LoadGovernedCarrierShape()
    {
        var path = Path.Combine(RepoRoot(), "contracts", "avro", "_shared", "Movement.avsc.json");
        Assert.True(File.Exists(path), $"governed carrier shape not found on disk: {path}");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static void AssertEnumSymbols(JsonElement carrierShape, string fieldName, string[] expectedSymbols)
    {
        var field = carrierShape.GetProperty("fields").EnumerateArray()
            .First(f => f.GetProperty("name").GetString() == fieldName);
        var symbols = field.GetProperty("type").GetProperty("symbols").EnumerateArray()
            .Select(s => s.GetString())
            .ToArray();
        Assert.Equal(expectedSymbols, symbols);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "engine", "Babelstone.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"repo root (containing engine/Babelstone.slnx) not found from {AppContext.BaseDirectory}");
    }
}
