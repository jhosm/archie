using System.Reflection;
using Avro;

namespace Babelstone.Engine.Avro;

/// <summary>
/// The parsed Avro schemas for the term-deposit events, keyed by stored event_type
/// (e.g. "term_deposit.DepositConstituted"), plus the derived Schema Registry subject
/// (ADR-IC-002 §P1: fully-qualified-name + "-value"). The .avsc files in contracts/avro
/// are the single source — they are embedded into this assembly as resources so a deploy
/// carries its own schemas (the same posture as the embedded migration SQL resources).
/// </summary>
public sealed class AvroSchemaCatalog
{
    // The four AT_MATURITY term-deposit events (E.1). The map is event_type → avsc resource
    // base name. The Avro namespace is fixed at "deposits.term_deposit" (ADR-IC-002 §P1);
    // the registry subject is "<namespace>.<Name>-value".
    private static readonly IReadOnlyList<(string EventType, string Avsc)> Events =
    [
        ("term_deposit.DepositConstituted", "deposits.term_deposit.DepositConstituted.avsc"),
        ("term_deposit.InterestAccrued", "deposits.term_deposit.InterestAccrued.avsc"),
        ("term_deposit.WithholdingApplied", "deposits.term_deposit.WithholdingApplied.avsc"),
        ("term_deposit.DepositMatured", "deposits.term_deposit.DepositMatured.avsc"),
    ];

    private readonly IReadOnlyDictionary<string, AvroSchemaEntry> _byEventType;

    public AvroSchemaCatalog()
    {
        var assembly = typeof(AvroSchemaCatalog).Assembly;
        var byEventType = new Dictionary<string, AvroSchemaEntry>(StringComparer.Ordinal);
        foreach (var (eventType, avsc) in Events)
        {
            var json = ReadResource(assembly, avsc);
            var schema = (RecordSchema)Schema.Parse(json);
            // The registry subject derives from the fully-qualified name (ADR-IC-002 §P1).
            var subject = $"{schema.Fullname}-value";
            byEventType[eventType] = new AvroSchemaEntry(eventType, subject, schema, json);
        }

        _byEventType = byEventType;
    }

    /// <summary>Every catalogued entry (event_type → subject/schema). Used to register all schemas up front.</summary>
    public IReadOnlyCollection<AvroSchemaEntry> Entries => (IReadOnlyCollection<AvroSchemaEntry>)_byEventType.Values;

    /// <summary>Resolves the schema entry for a stored event_type. Throws — fail loud — on an unknown type.</summary>
    public AvroSchemaEntry ForEventType(string eventType)
        => _byEventType.TryGetValue(eventType, out var entry)
            ? entry
            : throw new InvalidOperationException(
                $"No Avro schema catalogued for event type '{eventType}'. " +
                "Add the .avsc to contracts/avro and register it in AvroSchemaCatalog.");

    private static string ReadResource(Assembly assembly, string avscFileName)
    {
        // Embedded as "<RootNamespace>.<file>" — RootNamespace is Babelstone.Engine.Avro.
        var resourceName = $"Babelstone.Engine.Avro.contracts.avro.{avscFileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded Avro schema resource '{resourceName}' not found. " +
                $"Available: {string.Join(", ", assembly.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>One catalogued event: its stored event_type, the SR subject, the parsed schema, and the raw .avsc JSON.</summary>
public sealed record AvroSchemaEntry(string EventType, string Subject, RecordSchema Schema, string SchemaJson);
