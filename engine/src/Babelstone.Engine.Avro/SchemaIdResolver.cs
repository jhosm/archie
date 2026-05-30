using Confluent.SchemaRegistry;

namespace Babelstone.Engine.Avro;

/// <summary>
/// Resolves the Schema-Registry schema_id for an event_type's subject, so the codec can
/// embed it at WRITE time (ADR-IC-002 §P3 / ADR-IC-004 §P3). The id is what the relay
/// later puts in the Confluent wire-format header WITHOUT a runtime SR lookup.
/// </summary>
public interface ISchemaIdResolver
{
    /// <summary>The schema_id for the given event_type. Throws — fail loud — if it cannot be resolved.</summary>
    int ResolveSchemaId(string eventType);
}

/// <summary>
/// A Confluent Schema-Registry-backed resolver. At construction it REGISTERS-IF-ABSENT
/// every catalogued schema and caches each event_type → id (so the hot path is a dict
/// lookup, never an SR round-trip).
/// </summary>
/// <remarks>
/// Startup register-if-absent is a <b>walking-skeleton convenience</b>. ADR-IC-002 §P3 makes
/// registration a CI gate, never a producer-startup operation; the authoritative CI-gate
/// compatibility check is Epic G.3. This resolver is what the E.4/E.6 test path uses to make
/// outbox rows carry real ids against a Testcontainer SR; production registers in CI and this
/// resolver collapses to a pure lookup (RegisterIfAbsent: false).
/// </remarks>
public sealed class ConfluentSchemaIdResolver : ISchemaIdResolver, IDisposable
{
    private readonly ISchemaRegistryClient _client;
    private readonly bool _ownsClient;
    private readonly IReadOnlyDictionary<string, int> _idByEventType;

    public ConfluentSchemaIdResolver(
        AvroSchemaCatalog catalog, ISchemaRegistryClient client, bool registerIfAbsent, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;

        var idByEventType = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in catalog.Entries)
        {
            var schema = new Schema(entry.SchemaJson, SchemaType.Avro);
            // Register-if-absent (idempotent: an identical schema returns the existing id) OR
            // pure lookup. Either way the result is the authoritative id for the subject.
            var id = registerIfAbsent
                ? _client.RegisterSchemaAsync(entry.Subject, schema).GetAwaiter().GetResult()
                : _client.GetSchemaIdAsync(entry.Subject, schema).GetAwaiter().GetResult();
            idByEventType[entry.EventType] = id;
        }

        _idByEventType = idByEventType;
    }

    /// <summary>Convenience: build a resolver against an SR url with a fresh cached client this resolver owns.</summary>
    public static ConfluentSchemaIdResolver Create(AvroSchemaCatalog catalog, string schemaRegistryUrl, bool registerIfAbsent)
    {
        var client = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = schemaRegistryUrl });
        return new ConfluentSchemaIdResolver(catalog, client, registerIfAbsent, ownsClient: true);
    }

    public int ResolveSchemaId(string eventType)
        => _idByEventType.TryGetValue(eventType, out var id)
            ? id
            : throw new InvalidOperationException(
                $"No registered schema_id for event type '{eventType}'. " +
                "Its .avsc must be catalogued and registered before encode.");

    public void Dispose()
    {
        if (_ownsClient && _client is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
