using System.Reflection;
using System.Text;
using Avro;
using Avro.Generic;
using Avro.IO;
using Babelstone.Engine;
using Babelstone.FinancialTypes;

namespace Babelstone.Engine.Avro;

/// <summary>
/// A FAMILY-AGNOSTIC Avro <see cref="IEventSerializer"/> (ADR-PC-021 §D2): it serializes ANY
/// family's <see cref="DomainEvent"/> to/from Avro by binding the record's constructor parameters
/// to its <c>.avsc</c> fields by a fixed convention — it never names a family.
/// </summary>
/// <remarks>
/// Convention: a record parameter <c>Foo</c> maps to the Avro field <c>foo</c> (snake_case),
/// except a <see cref="Money"/> parameter <c>Foo</c> maps to <c>foo_cents</c> (the integer-cents
/// substrate, ADR-PC-010 §P1). Conversions are centralised: <c>Money↔long</c>, <c>Guid↔uuid</c>,
/// <c>DateOnly↔date</c>. The <c>.avsc</c> (contracts/avro/{domain}/{aggregate_type}/, governed by ADR-IC-002) stays the
/// authority — reflection only <i>binds</i> params to fields and throws if they do not line up; it
/// never derives the schema from the type. The bytes produced are the BARE Avro value; the
/// Confluent wire-format prefix is the relay's job (ADR-IC-004 §P3). Adding a family is adding its
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

    private static GenericRecord ToRecord(DomainEvent @event, RecordSchema schema)
    {
        var type = @event.GetType();
        var record = new GenericRecord(schema);
        foreach (var parameter in PrimaryConstructor(type).GetParameters())
        {
            var fieldName = FieldName(parameter.Name!, parameter.ParameterType);
            RequireField(schema, fieldName, type);
            var value = type.GetProperty(parameter.Name!)!.GetValue(@event);
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

            arguments[i] = FromAvro(avroValue, parameter.ParameterType);
        }

        return (DomainEvent)constructor.Invoke(arguments);
    }

    // A positional record exposes one public primary constructor (the copy ctor is protected);
    // pick the most-parameters one defensively.
    private static ConstructorInfo PrimaryConstructor(Type type)
        => type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();

    // Foo → foo; a Money Foo → foo_cents (integer-cents wire substrate, ADR-PC-010 §P1). A nullable
    // Money? Foo (an optional [null,long] field, ADR-IC-002 §P2) keeps the same _cents suffix — the
    // suffix tracks the Money substrate, not whether the field is required.
    internal static string FieldName(string parameterName, Type parameterType)
    {
        var snake = ToSnake(parameterName);
        return UnderlyingType(parameterType) == typeof(Money) ? $"{snake}_cents" : snake;
    }

    // null → Avro null (an optional [null,T] union, ADR-IC-002 §P2: null-first + default null). A
    // null is no longer rejected: the codec emits it into the union the .avsc declares. The .avsc
    // stays the authority — Apache.Avro's writer rejects a null against a non-union (required) field,
    // so a null here can only round-trip where the schema actually offers the null branch.
    internal static object? ToAvro(object? value) => value switch
    {
        Money money => money.Cents,                                          // → Avro long
        DateOnly date => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), // → Avro date (via DateTime)
        Guid guid => guid,                                                    // → Avro uuid (System.Guid)
        null => null,                                                         // → Avro null (optional [null,T] field, ADR-IC-002 §P2)
        _ => value,                                                           // int / long / string passthrough
    };

    internal static object? FromAvro(object? avroValue, Type targetType)
    {
        if (avroValue is null) return null; // optional [null,T] field absent/null → the nullable target's null (ADR-IC-002 §P2)
        var underlying = UnderlyingType(targetType);
        if (underlying == typeof(Money)) return new Money((long)avroValue);
        if (underlying == typeof(DateOnly)) return DateOnly.FromDateTime((DateTime)avroValue);
        return avroValue; // Guid (uuid), int, long, string round-trip as-is.
    }

    // Tolerates OPTIONAL fields (ADR-IC-002 §P2): a field whose .avsc type is a [null,T] union
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
        // intra-process / same-version assumption: cold replay reads the same family schema the runtime
        // wrote (Epic E walking skeleton), and the G.2 inbox consumer reads same-version intra-context
        // topics. It does NOT perform Avro schema RESOLUTION — a caller that must read a DIFFERENT writer
        // schema (cross-context BACKWARD/FORWARD evolution, ADR-IC-002) must resolve the writer schema by
        // its embedded id from the Schema Registry and pass writer + reader here. See the KNOWN LIMITATION
        // in InboxPump's class remarks; that SR-resolution path is unfinished follow-up work, not silent
        // drift.
        var reader = new GenericDatumReader<GenericRecord>(schema, schema);
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        var decoder = new BinaryDecoder(stream);
        return reader.Read(null!, decoder);
    }
}
