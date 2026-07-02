using Babelstone.Engine;

namespace Babelstone.Engine.Hosting;

/// <summary>
/// The BUS codec the engine write half hands its outbox rows (ADR-PC-028 dual-encode):
/// real Avro bytes + a registered Schema-Registry <c>schema_id</c> (ADR-IC-002 / ADR-IC-004),
/// distinct from the self-describing JSON the <c>events.payload</c> book of record keeps. A marker
/// wrapper (not a bare <see cref="IEventSerializer"/>) so DI can tell the bus codec apart from the
/// store codec — both implement the same family-agnostic seam.
/// </summary>
/// <remarks>
/// The wrapper holds the concrete Avro codec; the engine kernel only ever sees the
/// <see cref="IEventSerializer"/> seam, so the Avro/Schema-Registry surface stays confined to the host
/// (<c>HostBusEncoding</c> in <c>Babelstone.Engine.Api</c>) + <c>Babelstone.Engine.Avro</c> spine
/// (EVENT_STORE_PAYLOAD_SELF_DESCRIBING / ENGINE_FAMILY_AGNOSTIC). This MARKER lives in the shared
/// hosting-contract assembly (relocated 2026-06-20) so a family host module can
/// resolve it without a <c>family → host</c> cycle; the Avro/SR composition that PRODUCES one stays in
/// the host. Construction is LAZY at the host's composition root (see <c>HostBusEncoding</c>): the
/// Avro+SR resolver only reaches the registry when this is first resolved, so a host that does not opt
/// into Avro-on-bus (the default JSON posture) never needs a live Schema Registry to boot.
/// </remarks>
public sealed record BusEventSerializer(IEventSerializer Inner);
