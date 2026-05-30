using System.Reflection;
using Avro;

namespace Babelstone.Engine.Avro;

/// <summary>
/// The Avro schemas for the engine's emitted events, discovered from the embedded
/// <c>contracts/avro/{domain}/{aggregate_type}/{EventName}.avsc</c> — FAMILY-AGNOSTIC (ADR-PC-021 §D2): no family is named here.
/// Each schema yields a catalogued entry keyed both by the stored <c>event_type</c> (derived
/// from the Avro namespace — <c>{aggregate_type}.{Name}</c>) and by the Avro record name
/// (== the CLR event-type name), plus the Schema-Registry subject (ADR-IC-002 §P1:
/// fully-qualified-name + <c>-value</c>). The <c>.avsc</c> in <c>contracts/avro</c> are the
/// single governed source and are embedded so a deploy carries its own schemas. Adding a
/// family is adding its <c>.avsc</c> — nothing in this catalog changes.
/// </summary>
public sealed class AvroSchemaCatalog
{
    private readonly IReadOnlyDictionary<string, AvroSchemaEntry> _byEventType;
    private readonly IReadOnlyDictionary<string, AvroSchemaEntry> _byRecordName;

    public AvroSchemaCatalog()
    {
        var assembly = typeof(AvroSchemaCatalog).Assembly;
        var byEventType = new Dictionary<string, AvroSchemaEntry>(StringComparer.Ordinal);
        var byRecordName = new Dictionary<string, AvroSchemaEntry>(StringComparer.Ordinal);

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.EndsWith(".avsc", StringComparison.Ordinal))
            {
                continue;
            }

            var json = ReadResource(assembly, resourceName);
            var schema = (RecordSchema)Schema.Parse(json);
            var eventType = DeriveEventType(schema);
            var entry = new AvroSchemaEntry(eventType, $"{schema.Fullname}-value", schema, json);

            if (!byEventType.TryAdd(eventType, entry))
            {
                throw new InvalidOperationException(
                    $"Two embedded .avsc map to the same event_type '{eventType}'.");
            }

            if (!byRecordName.TryAdd(schema.Name, entry))
            {
                throw new InvalidOperationException(
                    $"Two embedded .avsc share the Avro record name '{schema.Name}' (event names must be unique).");
            }
        }

        _byEventType = byEventType;
        _byRecordName = byRecordName;
    }

    /// <summary>Every catalogued entry (used to register/look up all schema_ids up front).</summary>
    public IReadOnlyCollection<AvroSchemaEntry> Entries => (IReadOnlyCollection<AvroSchemaEntry>)_byEventType.Values;

    /// <summary>Resolves by stored <c>event_type</c> (the load path). Fail-loud on an unknown type.</summary>
    public AvroSchemaEntry ForEventType(string eventType)
        => _byEventType.TryGetValue(eventType, out var entry)
            ? entry
            : throw new InvalidOperationException(
                $"No Avro schema catalogued for event type '{eventType}'. Add its .avsc under contracts/avro/{{domain}}/{{aggregate_type}}/.");

    /// <summary>
    /// Resolves by the CLR event-type name, which equals the Avro record <c>name</c> (the codec's
    /// encode/decode entry point — it has a CLR instance/type, not the event_type). Fail-loud.
    /// </summary>
    public AvroSchemaEntry ForRecordName(string recordName)
        => _byRecordName.TryGetValue(recordName, out var entry)
            ? entry
            : throw new InvalidOperationException(
                $"No Avro schema catalogued for event '{recordName}'. Its .avsc record name must equal the event-type name.");

    // event_type = "{aggregate_type}.{Name}". The Avro namespace is "{domain}.{aggregate_type}"
    // (ADR-IC-002 §P1) but the engine's stored event_type omits the domain, so the aggregate_type
    // is the last namespace segment (e.g. "deposits.term_deposit" → "term_deposit").
    private static string DeriveEventType(RecordSchema schema)
    {
        var ns = schema.Namespace ?? string.Empty;
        var aggregateType = ns.Contains('.', StringComparison.Ordinal) ? ns[(ns.LastIndexOf('.') + 1)..] : ns;
        return aggregateType.Length == 0 ? schema.Name : $"{aggregateType}.{schema.Name}";
    }

    private static string ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded Avro schema resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>One catalogued event: its stored event_type, the SR subject, the parsed schema, and the raw .avsc JSON.</summary>
public sealed record AvroSchemaEntry(string EventType, string Subject, RecordSchema Schema, string SchemaJson);
