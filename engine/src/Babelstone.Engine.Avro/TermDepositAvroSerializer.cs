using Avro;
using Avro.Generic;
using Avro.IO;
using Babelstone.Engine;
using Babelstone.Families.TermDeposit;
using Babelstone.FinancialTypes;

namespace Babelstone.Engine.Avro;

/// <summary>
/// The real Avro <see cref="IEventSerializer"/> for the term-deposit family (Epic E.4).
/// Encode maps a <see cref="DomainEvent"/> to a <see cref="GenericRecord"/> per the
/// hand-authored .avsc (built EXPLICITLY per event type — no fragile reflection),
/// serializes the value bytes, and pairs them with the SR schema_id embedded at write
/// time (ADR-IC-002 §P3 / ADR-IC-004 §P3). Decode is the inverse.
/// </summary>
/// <remarks>
/// The bytes this produces are the BARE Avro value — NOT the Confluent wire format. The
/// magic byte + big-endian schema_id prefix is added by the relay (Babelstone.OutboxPublisher,
/// ADR-IC-004 §P3) from the embedded schema_id, so the wire-format concern lives in exactly
/// one place. Money serializes as its integer cents (Avro long); Guid as a uuid-logicalType
/// string; DateOnly as a date-logicalType field (on the wire: int days since epoch — the
/// Apache.Avro logical type carries it via DateTime, so we cross DateOnly↔DateTime here).
/// </remarks>
public sealed class TermDepositAvroSerializer(AvroSchemaCatalog catalog, ISchemaIdResolver schemaIds) : IEventSerializer
{

    public EncodedPayload Encode(DomainEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var (eventType, record) = ToRecord(@event);
        var entry = catalog.ForEventType(eventType);
        var bytes = WriteAvro(entry.Schema, record);
        var schemaId = schemaIds.ResolveSchemaId(eventType);
        return new EncodedPayload(bytes, schemaId);
    }

    // The (state-free) field switch; the RecordSchema is resolved from the instance catalog
    // so there is exactly ONE schema source per serializer.
    private (string EventType, GenericRecord Record) ToRecord(DomainEvent @event)
        => BuildRecord(@event, catalog);

    public DomainEvent Decode(ReadOnlyMemory<byte> payload, Type payloadType)
    {
        var eventType = EventTypeFor(payloadType);
        var entry = catalog.ForEventType(eventType);
        var record = ReadAvro(entry.Schema, payload);
        return FromRecord(payloadType, record);
    }

    // ---- Explicit per-event mapping (no reflection) -------------------------------------

    private static (string EventType, GenericRecord Record) BuildRecord(DomainEvent @event, AvroSchemaCatalog catalog)
    {
        RecordSchema SchemaOf(string eventType) => catalog.ForEventType(eventType).Schema;

        switch (@event)
        {
            case DepositConstituted e:
            {
                var entry = SchemaOf("term_deposit.DepositConstituted");
                var r = new GenericRecord(entry);
                // uuid logicalType: Apache.Avro carries it as a System.Guid (on the wire it is
                // the canonical 36-char string). Pass/read the Guid directly so encode↔decode is symmetric.
                r.Add("deposit_id", e.DepositId);
                r.Add("principal_cents", e.Principal.Cents);
                r.Add("tan_basis_points", e.TanBasisPoints);
                r.Add("rate_sheet_version_id", e.RateSheetVersionId);
                r.Add("term_days", e.TermDays);
                r.Add("start_date", ToAvroDate(e.StartDate));
                r.Add("maturity_date", ToAvroDate(e.MaturityDate));
                r.Add("interest_variant", e.InterestVariant);
                r.Add("auto_renewal_policy", e.AutoRenewalPolicy);
                return ("term_deposit.DepositConstituted", r);
            }

            case InterestAccrued e:
            {
                var entry = SchemaOf("term_deposit.InterestAccrued");
                var r = new GenericRecord(entry);
                r.Add("gross_interest_cents", e.GrossInterest.Cents);
                r.Add("as_of", ToAvroDate(e.AsOf));
                return ("term_deposit.InterestAccrued", r);
            }

            case WithholdingApplied e:
            {
                var entry = SchemaOf("term_deposit.WithholdingApplied");
                var r = new GenericRecord(entry);
                r.Add("tax_cents", e.Tax.Cents);
                r.Add("net_cents", e.Net.Cents);
                return ("term_deposit.WithholdingApplied", r);
            }

            case DepositMatured e:
            {
                var entry = SchemaOf("term_deposit.DepositMatured");
                var r = new GenericRecord(entry);
                r.Add("principal_returned_cents", e.PrincipalReturned.Cents);
                r.Add("net_interest_paid_cents", e.NetInterestPaid.Cents);
                r.Add("total_payout_cents", e.TotalPayout.Cents);
                r.Add("matured_on", ToAvroDate(e.MaturedOn));
                return ("term_deposit.DepositMatured", r);
            }

            default:
                throw new InvalidOperationException(
                    $"No Avro mapping for event type '{@event.GetType()}'. " +
                    "TermDepositAvroSerializer handles only the four term-deposit events.");
        }
    }

    private static DomainEvent FromRecord(Type payloadType, GenericRecord r)
    {
        if (payloadType == typeof(DepositConstituted))
        {
            return new DepositConstituted(
                DepositId: (Guid)r["deposit_id"],
                Principal: new Money((long)r["principal_cents"]),
                TanBasisPoints: (int)r["tan_basis_points"],
                RateSheetVersionId: (string)r["rate_sheet_version_id"],
                TermDays: (int)r["term_days"],
                StartDate: FromAvroDate(r["start_date"]),
                MaturityDate: FromAvroDate(r["maturity_date"]),
                InterestVariant: (string)r["interest_variant"],
                AutoRenewalPolicy: (string)r["auto_renewal_policy"]);
        }

        if (payloadType == typeof(InterestAccrued))
        {
            return new InterestAccrued(
                GrossInterest: new Money((long)r["gross_interest_cents"]),
                AsOf: FromAvroDate(r["as_of"]));
        }

        if (payloadType == typeof(WithholdingApplied))
        {
            return new WithholdingApplied(
                Tax: new Money((long)r["tax_cents"]),
                Net: new Money((long)r["net_cents"]));
        }

        if (payloadType == typeof(DepositMatured))
        {
            return new DepositMatured(
                PrincipalReturned: new Money((long)r["principal_returned_cents"]),
                NetInterestPaid: new Money((long)r["net_interest_paid_cents"]),
                TotalPayout: new Money((long)r["total_payout_cents"]),
                MaturedOn: FromAvroDate(r["matured_on"]));
        }

        throw new InvalidOperationException(
            $"No Avro mapping for payload type '{payloadType}'. " +
            "TermDepositAvroSerializer handles only the four term-deposit events.");
    }

    private static string EventTypeFor(Type payloadType)
    {
        if (payloadType == typeof(DepositConstituted)) return "term_deposit.DepositConstituted";
        if (payloadType == typeof(InterestAccrued)) return "term_deposit.InterestAccrued";
        if (payloadType == typeof(WithholdingApplied)) return "term_deposit.WithholdingApplied";
        if (payloadType == typeof(DepositMatured)) return "term_deposit.DepositMatured";
        throw new InvalidOperationException(
            $"No event_type known for payload type '{payloadType}'. " +
            "TermDepositAvroSerializer handles only the four term-deposit events.");
    }

    // ---- Avro value (de)serialization ---------------------------------------------------

    private static byte[] WriteAvro(RecordSchema schema, GenericRecord record)
    {
        var writer = new GenericDatumWriter<GenericRecord>(schema);
        using var stream = new MemoryStream();
        var encoder = new BinaryEncoder(stream);
        writer.Write(record, encoder);
        encoder.Flush();
        return stream.ToArray();
    }

    private static GenericRecord ReadAvro(RecordSchema schema, ReadOnlyMemory<byte> payload)
    {
        // Writer schema == reader schema here (the embedded .avsc the id resolves to). Cold
        // replay reads the same family schema the runtime wrote (Epic E walking skeleton).
        var reader = new GenericDatumReader<GenericRecord>(schema, schema);
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        var decoder = new BinaryDecoder(stream);
        return reader.Read(null!, decoder);
    }

    // Apache.Avro's `date` logical type round-trips through DateTime (it computes days-since-epoch
    // internally). We carry DateOnly in the domain, so cross at this single boundary: encode to a
    // UTC-midnight DateTime, decode back to DateOnly.
    private static DateTime ToAvroDate(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private static DateOnly FromAvroDate(object value) => DateOnly.FromDateTime((DateTime)value);
}
