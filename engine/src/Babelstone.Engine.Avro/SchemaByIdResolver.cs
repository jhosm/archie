using System.Collections.Concurrent;
using Confluent.SchemaRegistry;

namespace Babelstone.Engine.Avro;

/// <summary>
/// Resolves the WRITER Avro schema for a Confluent wire-format <c>schema_id</c> (the 4 big-endian
/// bytes after the magic byte, ADR-IC-004 §P3). This is the CONSUMER-side mirror of
/// <see cref="ISchemaIdResolver"/>: the writer embeds the id at publish time; the consumer hands that
/// id here to recover the schema the bytes were ACTUALLY written with — the runtime "resolve the schema
/// ID by … lookup" point (ADR-IC-002 §P3) — so the codec can do Avro schema RESOLUTION (writer→reader)
/// under forward-only/BACKWARD evolution (ADR-IC-002 §Consequences) instead of blindly assuming
/// writer == reader.
/// </summary>
public interface ISchemaByIdResolver
{
    /// <summary>
    /// The parsed writer schema for the given wire-format <c>schema_id</c>. Throws — fail loud — if the
    /// id cannot be resolved (an unknown id is undecodable: the caller treats the failure as poison).
    /// </summary>
    global::Avro.Schema ResolveWriterSchema(int schemaId);
}

/// <summary>
/// A Confluent Schema-Registry-backed <see cref="ISchemaByIdResolver"/> with a client-side cache: each
/// <c>schema_id</c> is fetched from the registry by id ONCE and the parsed <see cref="global::Avro.Schema"/> is
/// cached, so the hot decode path is a dictionary lookup, never an SR round-trip. (<see
/// cref="CachedSchemaRegistryClient"/> already caches the raw JSON by id; this layer additionally
/// caches the PARSED schema so each record decode skips a re-parse too.)
/// </summary>
/// <remarks>
/// This is what lets the InboxPump consumer/bus-decode path honour the consumer contract instead of
/// assuming writer == reader: ADR-IC-002 §Consequences holds that "the schema ID in the Avro message
/// header is meaningless without the registry", and §P3 adds the runtime "resolve the schema ID by …
/// lookup" point. A producer on a NEWER writer schema (a BACKWARD-compatible additive change, the
/// §Consequences compatibility default) embeds a different id; resolving it here lets the codec read
/// writer→reader rather than mis-decoding → poison. (Scope: the CONSUMER/bus path only — the event-store
/// replay/rebuild path still reads writer == reader; see the InboxPump class remarks.)
/// </remarks>
public sealed class ConfluentSchemaByIdResolver : ISchemaByIdResolver, IDisposable
{
    private readonly ISchemaRegistryClient _client;
    private readonly bool _ownsClient;
    private readonly ConcurrentDictionary<int, global::Avro.Schema> _byId = new();

    public ConfluentSchemaByIdResolver(ISchemaRegistryClient client, bool ownsClient = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
    }

    /// <summary>Convenience: build a resolver against an SR url with a fresh cached client it owns.</summary>
    public static ConfluentSchemaByIdResolver Create(string schemaRegistryUrl)
    {
        var client = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = schemaRegistryUrl });
        return new ConfluentSchemaByIdResolver(client, ownsClient: true);
    }

    public global::Avro.Schema ResolveWriterSchema(int schemaId)
        => _byId.GetOrAdd(schemaId, id =>
        {
            // GetSchemaAsync(id) returns the registered schema JSON for this id (CachedSchemaRegistryClient
            // caches the fetch). Parse it ONCE into an global::Avro.Schema and cache the parsed form. A genuinely
            // unknown id throws from the registry — surfaced to the caller as an undecodable record.
            var registered = _client.GetSchemaAsync(id).GetAwaiter().GetResult();
            return global::Avro.Schema.Parse(registered.SchemaString);
        });

    public void Dispose()
    {
        if (_ownsClient && _client is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
