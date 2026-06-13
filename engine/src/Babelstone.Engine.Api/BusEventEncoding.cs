using Babelstone.Engine;
using Babelstone.Engine.Avro;
using Confluent.SchemaRegistry;

namespace Babelstone.Engine.Api;

/// <summary>
/// The BUS codec the engine write half hands its outbox rows (ADR-PC-028 §Decision dual-encode):
/// real Avro bytes + a registered Schema-Registry <c>schema_id</c> (ADR-IC-002 §P3 / ADR-IC-004 §P3),
/// distinct from the self-describing JSON the <c>events.payload</c> book of record keeps. A marker
/// wrapper (not a bare <see cref="IEventSerializer"/>) so DI can tell the bus codec apart from the
/// store codec — both implement the same family-agnostic seam.
/// </summary>
/// <remarks>
/// The wrapper holds the concrete Avro codec; the engine kernel only ever sees the
/// <see cref="IEventSerializer"/> seam, so the Avro/Schema-Registry surface stays confined to the host
/// + <c>Babelstone.Engine.Avro</c> spine (EVENT_STORE_PAYLOAD_SELF_DESCRIBING / ENGINE_FAMILY_AGNOSTIC).
/// Construction is LAZY at the host's composition root (see <see cref="HostBusEncoding"/>): the Avro+SR
/// resolver only reaches the registry when this is first resolved, so a host that does not opt into
/// Avro-on-bus (the default JSON posture) never needs a live Schema Registry to boot.
/// </remarks>
public sealed record BusEventSerializer(IEventSerializer Inner);

/// <summary>
/// Composes the engine host's bus-encoding posture (bd <c>babelstone-36mk</c>). Two modes, selected by
/// the <c>Bus:Encoding</c> configuration key — never a silent fallback:
/// <list type="bullet">
/// <item><c>avro</c> (production): the outbox carries real Avro + a registered Schema-Registry
/// <c>schema_id</c> (<see cref="AvroEventSerializer"/> + <see cref="ConfluentSchemaIdResolver"/>),
/// while the store keeps self-describing JSON — the ADR-PC-028 dual-encode split.</item>
/// <item><c>json</c> (the default): no separate bus codec is registered; the runtime reuses the JSON
/// store codec for the outbox too — the pre-split single-encoding, so a dev/test host boots with no
/// Schema Registry.</item>
/// </list>
/// </summary>
public static class HostBusEncoding
{
    /// <summary>The <c>Bus:Encoding</c> value that turns on the Avro+Schema-Registry bus codec.</summary>
    public const string AvroMode = "avro";

    /// <summary>
    /// Registers the bus codec IFF <c>Bus:Encoding=avro</c>. The Avro+SR codec is built LAZILY (the SR
    /// round-trip happens on first resolve, not at registration), so a host that leaves the default
    /// JSON posture never touches the Schema Registry. The dev Schema-Registry URL defaults to the
    /// infra/compose.yaml external listener (<c>http://localhost:18081</c>); override with
    /// <c>Bus:SchemaRegistryUrl</c>. <c>Bus:RegisterSchemas</c> (default <c>true</c> for the walking
    /// skeleton, ADR-IC-002 §P3 register-if-absent convenience) controls register-if-absent vs pure
    /// lookup.
    /// </summary>
    public static void AddBusEncoding(IServiceCollection services, IConfiguration configuration)
    {
        var mode = configuration["Bus:Encoding"] ?? "json";
        if (!string.Equals(mode, AvroMode, StringComparison.OrdinalIgnoreCase))
        {
            return; // JSON posture: the runtime reuses the store codec for the outbox (the default).
        }

        var schemaRegistryUrl = configuration["Bus:SchemaRegistryUrl"] ?? "http://localhost:18081";
        var registerIfAbsent = configuration.GetValue("Bus:RegisterSchemas", true);

        // Lazy: the AvroSchemaCatalog parse is cheap, but the ConfluentSchemaIdResolver round-trips the
        // registry at construction, so build it only when the bus codec is first resolved (the first
        // catalogued append) — not at host build time. The catalog is the SAME embedded-schema
        // catalogue Program.cs registers as the IIntegrationEventCatalog gate; resolving it here keeps
        // the single source. The resolver owns its SR client (disposed with the singleton).
        services.AddSingleton(serviceProvider =>
        {
            var catalog = (AvroSchemaCatalog)serviceProvider.GetRequiredService<IIntegrationEventCatalog>();
            var schemaIds = ConfluentSchemaIdResolver.Create(catalog, schemaRegistryUrl, registerIfAbsent);
            return new BusEventSerializer(new AvroEventSerializer(catalog, schemaIds));
        });
    }
}
