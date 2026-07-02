using System.Collections;
using System.Reflection;
using System.Text;
using Avro;
using Avro.Generic;
using Avro.IO;
using Babelstone.Engine;
using Babelstone.FinancialTypes;

namespace Babelstone.Engine.Avro;

/// <summary>
/// A FAMILY-AGNOSTIC Avro <see cref="IEventSerializer"/> (ADR-PC-021): it serializes ANY
/// family's <see cref="DomainEvent"/> to/from Avro by binding the record's constructor parameters
/// to its <c>.avsc</c> fields by a fixed convention — it never names a family.
/// </summary>
/// <remarks>
/// Convention: a record parameter <c>Foo</c> maps to the Avro field <c>foo</c> (snake_case),
/// except a <see cref="Money"/> parameter <c>Foo</c> maps to <c>foo_cents</c> (the integer-cents
/// substrate, ADR-PC-010). Conversions are centralised: <c>Money↔long</c>, <c>Guid↔uuid</c>,
/// <c>DateOnly↔date</c>. The <c>.avsc</c> (contracts/avro/{domain}/{aggregate_type}/, governed by ADR-IC-002) stays the
/// authority — reflection only <i>binds</i> params to fields and throws if they do not line up; it
/// never derives the schema from the type. The bytes produced are the BARE Avro value; the
/// Confluent wire-format prefix is the relay's job (ADR-IC-004). Adding a family is adding its
/// <c>.avsc</c> — this codec is unchanged.
/// </remarks>
public sealed class AvroEventSerializer(AvroSchemaCatalog catalog, ISchemaIdResolver schemaIds) : IEventSerializer
{
    public EncodedPayload Encode(DomainEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var entry = catalog.ForRecordName(@event.GetType().Name);
        var record = ToRecord(@event, entry.Schema);
        var bytes = WriteAvro(entry.Schema, record);
        return new EncodedPayload(bytes, schemaIds.ResolveSchemaId(entry.EventType));
    }

    public DomainEvent Decode(ReadOnlyMemory<byte> payload, Type payloadType)
    {
        ArgumentNullException.ThrowIfNull(payloadType);
        var entry = catalog.ForRecordName(payloadType.Name);
        var record = ReadAvro(entry.Schema, payload);
        return FromRecord(payloadType, record);
    }

    /// <summary>
    /// Decode performing Avro schema RESOLUTION: the bytes were written with <paramref name="writerSchema"/>
    /// (recovered from the embedded wire-format <c>schema_id</c> via the Schema Registry), and are read
    /// against this consumer's local reader schema for <paramref name="payloadType"/>. This is the
    /// cross-context FORWARD/BACKWARD-evolution path (ADR-IC-002): a producer on a NEWER
    /// writer schema (an additive BACKWARD-compatible change) decodes correctly against the OLDER reader schema
    /// — a writer-added field the reader does not know is dropped; a reader field absent from the writer
    /// falls to its schema default. The single-argument <see cref="Decode(ReadOnlyMemory{byte}, Type)"/>
    /// is the writer == reader fast path; this overload is what the inbox consumer uses once the SR has
    /// resolved the real writer schema.
    /// </summary>
    public DomainEvent Decode(ReadOnlyMemory<byte> payload, Type payloadType, global::Avro.Schema writerSchema)
    {
        ArgumentNullException.ThrowIfNull(payloadType);
        ArgumentNullException.ThrowIfNull(writerSchema);
        var entry = catalog.ForRecordName(payloadType.Name);
        var record = ReadAvro(writerSchema, entry.Schema, payload);
        return FromRecord(payloadType, record);
    }

    private static GenericRecord ToRecord(DomainEvent @event, RecordSchema schema)
    {
        var type = @event.GetType();
        var record = new GenericRecord(schema);
        foreach (var parameter in PrimaryConstructor(type).GetParameters())
        {
            var fieldName = FieldName(parameter.Name!, parameter.ParameterType);
            RequireField(schema, fieldName, type);
            var value = type.GetProperty(parameter.Name!)!.GetValue(@event);

            // The Movement CARRIER (ADR-PC-032): a parameter typed IReadOnlyList<Movement> maps to the
            // .avsc field's Avro array-of-Movement-record. The nested Movement record schema is the
            // array's `items`, read off the carrying event's own schema — the codec stays family-agnostic
            // (it binds whatever the .avsc declares, it never derives the shape from the family).
            if (MovementCarrier.IsCarrierParameter(parameter.ParameterType))
            {
                record.Add(fieldName, MovementCarrier.ToAvroArray(value, SchemaFieldType(schema, fieldName), parameter.Name!));
                continue;
            }

            if (value is null && !IsNullableUnion(schema, fieldName))
            {
                // A null on a REQUIRED (non-[null,T]) field. ToAvro(null) returns Avro null for the
                // optional path, so without this pre-check the null reaches Apache.Avro's writer and
                // surfaces as a bare NullReferenceException with no field name. Fail clearly here
                // instead — the optional [null,T] path (IsNullableUnion true) still emits Avro null.
                throw new InvalidOperationException(
                    $"Event field '{parameter.Name}' is null; '{fieldName}' is a required (non-[null,T]) field.");
            }

            record.Add(fieldName, ToAvro(value));
        }

        return record;
    }

    private static DomainEvent FromRecord(Type payloadType, GenericRecord record)
    {
        var constructor = PrimaryConstructor(payloadType);
        var parameters = constructor.GetParameters();
        var arguments = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            var fieldName = FieldName(parameter.Name!, parameter.ParameterType);
            if (!record.TryGetValue(fieldName, out var avroValue))
            {
                throw new InvalidOperationException(
                    $"Avro record for '{payloadType.Name}' has no field '{fieldName}' for parameter '{parameter.Name}'.");
            }

            // The Movement CARRIER (ADR-PC-032): the Avro array-of-Movement-record field decodes back to
            // the IReadOnlyList<Movement> the carrying event declares.
            arguments[i] = MovementCarrier.IsCarrierParameter(parameter.ParameterType)
                ? MovementCarrier.FromAvroArray(avroValue, parameter.Name!)
                : FromAvro(avroValue, parameter.ParameterType);
        }

        return (DomainEvent)constructor.Invoke(arguments);
    }

    // A positional record exposes one public primary constructor (the copy ctor is protected);
    // pick the most-parameters one defensively.
    private static ConstructorInfo PrimaryConstructor(Type type)
        => type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();

    // Foo → foo; a Money Foo → foo_cents (integer-cents wire substrate, ADR-PC-010). A nullable
    // Money? Foo (an optional [null,long] field, ADR-IC-002) keeps the same _cents suffix — the
    // suffix tracks the Money substrate, not whether the field is required.
    internal static string FieldName(string parameterName, Type parameterType)
    {
        var snake = ToSnake(parameterName);
        return UnderlyingType(parameterType) == typeof(Money) ? $"{snake}_cents" : snake;
    }

    // null → Avro null (an optional [null,T] union, ADR-IC-002: null-first + default null). A
    // null is no longer rejected: the codec emits it into the union the .avsc declares. The .avsc
    // stays the authority — Apache.Avro's writer rejects a null against a non-union (required) field,
    // so a null here can only round-trip where the schema actually offers the null branch.
    internal static object? ToAvro(object? value) => value switch
    {
        Money money => money.Cents,                                          // → Avro long
        DateOnly date => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), // → Avro date (via DateTime)
        Guid guid => guid,                                                    // → Avro uuid (System.Guid)
        null => null,                                                         // → Avro null (optional [null,T] field, ADR-IC-002)
        _ => value,                                                           // int / long / string passthrough
    };

    internal static object? FromAvro(object? avroValue, Type targetType)
    {
        if (avroValue is null) return null; // optional [null,T] field absent/null → the nullable target's null (ADR-IC-002)
        var underlying = UnderlyingType(targetType);
        if (underlying == typeof(Money)) return new Money((long)avroValue);
        if (underlying == typeof(DateOnly)) return DateOnly.FromDateTime((DateTime)avroValue);
        return avroValue; // Guid (uuid), int, long, string round-trip as-is.
    }

    // Tolerates OPTIONAL fields (ADR-IC-002): a field whose .avsc type is a [null,T] union
    // (null-first + default null) is legitimately absent/null and must NOT be flagged missing — only
    // a field the schema does not declare AT ALL is a binding error.
    internal static void RequireField(RecordSchema schema, string fieldName, Type type)
    {
        if (!schema.Fields.Any(field => field.Name == fieldName))
        {
            throw new InvalidOperationException(
                $"Avro schema {schema.Fullname} has no field '{fieldName}' for event '{type.Name}'.");
        }
    }

    // True when the named field's .avsc type is an OPTIONAL [null,T] union (a union carrying a Null
    // branch, ADR-IC-002) — the one shape where a null value is legal and must emit Avro null.
    // Every other field is required: a null there is a binding error caught in ToRecord with the
    // field name, not a bare NullReferenceException deep in Apache.Avro's writer.
    internal static bool IsNullableUnion(RecordSchema schema, string fieldName)
        => schema.Fields.FirstOrDefault(field => field.Name == fieldName)?.Schema is UnionSchema union
            && union.Schemas.Any(branch => branch.Tag == Schema.Type.Null);

    // The named field's declared Avro type off the carrying event's own schema — the authority the
    // Movement carrier reads the nested array `items` (the Movement record) from, so the codec binds
    // whatever the .avsc declares and never derives the shape from the family type.
    internal static Schema SchemaFieldType(RecordSchema schema, string fieldName)
        => schema.Fields.FirstOrDefault(field => field.Name == fieldName)?.Schema
            ?? throw new InvalidOperationException(
                $"Avro schema {schema.Fullname} has no field '{fieldName}'.");

    // Unwrap Nullable<T> (a nullable value type, e.g. an optional Money? / DateOnly? field) to its
    // underlying T so the same Money/DateOnly conversions apply whether or not the field is optional.
    private static Type UnderlyingType(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static string ToSnake(string pascal)
    {
        var builder = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

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
        // Writer schema == reader schema (the local catalog schema for this record name). This is the
        // intra-process / same-version FAST PATH: cold replay reads the same family schema the runtime
        // wrote, and the inbox consumer's no-resolver fallback reads same-version intra-context topics.
        // It does NOT perform Avro schema RESOLUTION — a caller that must read a DIFFERENT writer schema
        // (cross-context BACKWARD/FORWARD evolution, ADR-IC-002) resolves the writer schema
        // by its embedded id from the Schema Registry and uses the Decode(payload, payloadType,
        // writerSchema) overload, which threads writer + reader into the resolving ReadAvro below. (The
        // event-store replay/rebuild path also uses THIS fast path, reading writer == reader.)
        var reader = new GenericDatumReader<GenericRecord>(schema, schema);
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        var decoder = new BinaryDecoder(stream);
        return reader.Read(null!, decoder);
    }

    private static GenericRecord ReadAvro(global::Avro.Schema writerSchema, RecordSchema readerSchema, ReadOnlyMemory<byte> payload)
    {
        // Avro schema RESOLUTION: the bytes were written with writerSchema (the producer's, recovered
        // from the embedded wire-format schema_id via the Schema Registry); they are READ against this
        // consumer's readerSchema (the local catalog schema for the resolved record name). Passing BOTH
        // schemas to the GenericDatumReader is what lets a NEWER writer's record decode against an OLDER
        // reader under forward-only/BACKWARD evolution (ADR-IC-002): a writer-only field is skipped,
        // a reader-only field falls back to its schema default. (The single-schema ReadAvro above stays
        // the writer == reader fast path for intra-process cold replay and same-version topics.)
        var reader = new GenericDatumReader<GenericRecord>(writerSchema, readerSchema);
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        var decoder = new BinaryDecoder(stream);
        return reader.Read(null!, decoder);
    }
}

/// <summary>
/// The Movement CARRIER mapping (ADR-PC-032): binds a carrying event's <c>IReadOnlyList&lt;Movement&gt;</c>
/// parameter to its <c>.avsc</c> field — an Avro <b>array</b> whose <c>items</c> is the nested
/// <c>Movement</c> record. In plain English: a money-moving event states a fact AND carries the money
/// legs it caused, as a list of movements inside the event's own opaque payload — no new events-table
/// column, no envelope change. This is the bus-codec half of that carry; the JSON store codec
/// (ADR-PC-028) carries the same list natively via System.Text.Json.
/// </summary>
/// <remarks>
/// FAMILY-AGNOSTIC (ADR-PC-021): the carrier reads the nested Movement record schema off the carrying
/// event's <c>.avsc</c> array <c>items</c> — it binds whatever the schema declares and never names a
/// family. The Movement field convention mirrors the flat codec's: <c>Money Amount</c> → <c>amount_cents</c>
/// (integer cents, ADR-PC-010), <c>Guid CommandId</c> → <c>command_id</c> (uuid), <c>DateOnly ValueDate</c>
/// → <c>value_date</c> (date), the two closed enums (<see cref="MovementOperation"/> /
/// <see cref="MovementOrigin"/>) → their member name as an Avro <c>string</c> (an enum, NOT a free string,
/// ADR-PC-032 slot 1; the closed set is enforced by the C# type, the wire carries the stable name), and
/// the opaque <c>AccountRef</c> → <c>account_ref</c> (a reference, NEVER PII, ADR-PC-004).
/// </remarks>
internal static class MovementCarrier
{
    /// <summary>A carrier parameter is exactly <c>IReadOnlyList&lt;Movement&gt;</c> — the one declared
    /// shape for a carrying event's movement legs (ADR-PC-032 / feature-design §2).</summary>
    public static bool IsCarrierParameter(Type parameterType)
        => parameterType.IsGenericType
           && parameterType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
           && parameterType.GetGenericArguments()[0] == typeof(Movement);

    /// <summary>Build the Avro array of nested Movement records for the carrier field. The carrier is a
    /// REQUIRED array field on the WIRE — never an optional union: an event with no movements is encoded as
    /// an EMPTY array, never null. A <c>null</c> C# carrier (the <c>IReadOnlyList&lt;Movement&gt;? = null</c>
    /// record default — the idiomatic "no movements" value a movement-free or pre-Movement event constructs
    /// with) is therefore NORMALIZED to the empty array here, so the wire stays "[] never null" while the C#
    /// default-construction path stays ergonomic. This preserves the wire contract (a decoder always reads an
    /// array, never null) without forcing every direct construction to spell out <c>Movements: []</c>.</summary>
    public static object ToAvroArray(object? value, Schema fieldSchema, string parameterName)
    {
        if (fieldSchema is not ArraySchema arraySchema || arraySchema.ItemSchema is not RecordSchema itemSchema)
        {
            throw new InvalidOperationException(
                $"Movement carrier '{parameterName}' maps to an Avro array-of-record, but its .avsc field is '{fieldSchema.Tag}'.");
        }

        // null ⇒ no movements ⇒ the empty wire array (never a null on the wire). A non-null list maps
        // element-wise to the nested Movement records.
        if (value is null)
        {
            return Array.Empty<object>();
        }

        var movements = ((IEnumerable)value).Cast<Movement>();
        return movements.Select(movement => ToMovementRecord(movement, itemSchema)).ToArray();
    }

    /// <summary>Decode the Avro array of nested Movement records back to the carrying event's
    /// <c>IReadOnlyList&lt;Movement&gt;?</c>. An EMPTY wire array (the "no movements" encoding — what a null or
    /// empty C# carrier is encoded as, see <see cref="ToAvroArray"/>) decodes back to <c>null</c>, the
    /// idiomatic C# "no movements" the record default constructs with — so a movement-free event round-trips
    /// to IDENTITY (null → [] wire → null) rather than to a non-equal empty list. A non-empty array decodes
    /// element-wise to the list.</summary>
    public static IReadOnlyList<Movement>? FromAvroArray(object? avroValue, string parameterName)
    {
        if (avroValue is not object[] array)
        {
            throw new InvalidOperationException(
                $"Movement carrier '{parameterName}' expected an Avro array; got '{avroValue?.GetType().Name ?? "null"}'.");
        }

        return array.Length == 0
            ? null
            : array.Cast<GenericRecord>().Select(FromMovementRecord).ToList();
    }

    private static GenericRecord ToMovementRecord(Movement movement, RecordSchema itemSchema)
    {
        var record = new GenericRecord(itemSchema);
        record.Add("account_ref", movement.AccountRef);
        // The three closed sets are Avro `enum` fields: Apache.Avro requires a GenericEnum wrapper (a
        // bare string is rejected), and the member NAME is the wire symbol (ADR-PC-032 slot 1's closed
        // type — the C# enum enforces the closed set; the symbol carries the stable name).
        record.Add("direction", Enum_(itemSchema, "direction", movement.Direction.ToString()));
        record.Add("amount_cents", movement.Amount.Cents);
        record.Add("value_date", movement.ValueDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        record.Add("operation", Enum_(itemSchema, "operation", movement.Operation.ToString()));
        record.Add("origin", Enum_(itemSchema, "origin", movement.Origin.ToString()));
        record.Add("command_id", movement.CommandId);
        return record;
    }

    private static Movement FromMovementRecord(GenericRecord record) => new(
        AccountRef: (string)Field(record, "account_ref"),
        Direction: Enum.Parse<SettlementDirection>(EnumValue(Field(record, "direction"))),
        Amount: new Money((long)Field(record, "amount_cents")),
        ValueDate: DateOnly.FromDateTime((DateTime)Field(record, "value_date")),
        Operation: Enum.Parse<MovementOperation>(EnumValue(Field(record, "operation"))),
        Origin: Enum.Parse<MovementOrigin>(EnumValue(Field(record, "origin"))),
        CommandId: (Guid)Field(record, "command_id"));

    // Wrap a closed-set member name as the Avro GenericEnum the named enum field declares.
    private static GenericEnum Enum_(RecordSchema itemSchema, string fieldName, string symbol)
    {
        var enumSchema = itemSchema.Fields.FirstOrDefault(field => field.Name == fieldName)?.Schema as EnumSchema
            ?? throw new InvalidOperationException($"Movement field '{fieldName}' is not an Avro enum.");
        return new GenericEnum(enumSchema, symbol);
    }

    // An Avro enum decodes as a GenericEnum (its .Value is the symbol); tolerate a bare string too.
    private static string EnumValue(object value) => value switch
    {
        GenericEnum genericEnum => genericEnum.Value,
        string symbol => symbol,
        _ => throw new InvalidOperationException($"Expected an Avro enum value, got '{value.GetType().Name}'."),
    };

    private static object Field(GenericRecord record, string name)
        => record.TryGetValue(name, out var value) && value is not null
            ? value
            : throw new InvalidOperationException($"Movement record has no field '{name}'.");
}
