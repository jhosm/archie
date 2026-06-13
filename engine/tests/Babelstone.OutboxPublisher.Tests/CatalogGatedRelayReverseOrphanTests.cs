using System.Reflection;
using Babelstone.Engine;
using Babelstone.Engine.Avro;
using Xunit;

namespace Babelstone.OutboxPublisher.Tests;

/// <summary>
/// NO_UNCATALOGUED_EVENT_ON_BUS (commitment catalogue row 22, ADR-IC-017 §P3) — the REVERSE orphan
/// check. In plain English: the existing AsyncAPI gate proves "every schema we wrote down has a
/// catalog page"; this proves the other direction — "every event the engine could actually publish
/// has a schema." Together they make <em>catalogued ⇔ on the bus</em> a hermetic biconditional rather
/// than a convention, so an event can never reach the durable bus without a deliberate promotion.
///
/// This anchors on the RUNTIME/CLR event set — the family's <see cref="DomainEvent"/> records — not on
/// the <c>.avsc</c> set, which is what makes it the mirror of the §P1 runtime gate and what catches a
/// SCHEMALESS event (the .avsc-driven <c>AvroCatalogSweepTests</c> sweep cannot see those). The
/// property: the catalog-gate predicate (<see cref="AvroSchemaCatalog.IsCataloguedIntegrationEvent"/>,
/// the family-agnostic membership test the relay gates on) admits a stored <c>event_type</c> if and
/// only if that event has a catalog/<c>.avsc</c> entry — i.e. an uncatalogued, relay-CAPABLE event is
/// store-only by construction, never publishable. Pure (no container), default CI lane.
/// </summary>
public sealed class CatalogGatedRelayReverseOrphanTests
{
    /// <summary>
    /// Every relay-CAPABLE <c>event_type</c> (one a loaded family registers a handler for, so the
    /// engine could append it and the relay could in principle publish it) is admitted by the gate
    /// predicate IFF it is catalogued. The gate is the runtime §P1 rule; this asserts its build-time
    /// mirror — no event_type the engine can produce slips onto the bus without a catalog entry, and
    /// (the forward direction) no catalogued event is wrongly excluded by the gate.
    /// </summary>
    [Fact]
    public void Every_relay_capable_event_type_is_gated_iff_catalogued()
    {
        var catalog = new AvroSchemaCatalog();
        var relayCapableEventTypes = RelayCapableEventTypes();

        // Non-vacuity: at least one family registered handlers, so the set is real.
        Assert.NotEmpty(relayCapableEventTypes);

        // The catalogued event_type set, read straight off the embedded schemas (the single governed
        // source). Every entry MUST be relay-capable (a registered handler) — a catalogued event with
        // no handler would be a schema for an event the engine cannot even append.
        var cataloguedEventTypes = catalog.Entries.Select(e => e.EventType).ToHashSet(StringComparer.Ordinal);

        var biconditionalViolations = new List<string>();
        foreach (var eventType in relayCapableEventTypes)
        {
            var gated = catalog.IsCataloguedIntegrationEvent(eventType);
            var catalogued = cataloguedEventTypes.Contains(eventType);
            if (gated != catalogued)
            {
                biconditionalViolations.Add(
                    $"{eventType}: gate admits={gated} but catalogued={catalogued}");
            }
        }

        Assert.True(
            biconditionalViolations.Count == 0,
            "ADR-IC-017 §P3 (NO_UNCATALOGUED_EVENT_ON_BUS): the relay's catalog-gate predicate must "
            + "admit a relay-capable event_type IFF it has a catalog/.avsc entry — catalogued ⇔ on the "
            + "bus. A mismatch means either an uncatalogued event could reach the bus (the §P1 leak) or "
            + "a catalogued event is wrongly excluded. Offending event_types:\n  "
            + string.Join("\n  ", biconditionalViolations));

        // And the catalogued set must be a SUBSET of the relay-capable set: every catalogued event_type
        // is one the engine can actually append (has a handler). A catalogued schema with no handler is
        // a promotion of an event that does not exist on the runtime — drift the other way.
        var orphanedCatalogEntries = cataloguedEventTypes
            .Where(et => !relayCapableEventTypes.Contains(et))
            .ToList();

        Assert.True(
            orphanedCatalogEntries.Count == 0,
            "ADR-IC-017 §P3: every catalogued event_type must be relay-capable (a loaded family registers "
            + "a handler for it). A catalogued schema with no handler promotes an event the engine cannot "
            + "append. Orphaned catalog entries:\n  " + string.Join("\n  ", orphanedCatalogEntries));
    }

    /// <summary>
    /// The set of stored <c>event_type</c>s the relay could in principle publish: the union of every
    /// loaded family module's handler registrations (<c>HandlerRegistration.EventType</c>). Discovered
    /// FAMILY-AGNOSTICALLY — every <see cref="IFamilyModule"/> in the loaded Babelstone assemblies — so
    /// no family is named here (this test project references one family only to load it, the same nudge
    /// <c>AvroCatalogSweepTests</c> uses).
    /// </summary>
    private static HashSet<string> RelayCapableEventTypes()
    {
        EnsureFamilyAssembliesLoaded();
        var eventTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var moduleType in FamilyModuleTypes())
        {
            var module = (IFamilyModule)Activator.CreateInstance(moduleType)!;
            foreach (var registration in module.Handlers)
            {
                eventTypes.Add(registration.EventType);
            }
        }

        return eventTypes;
    }

    private static IEnumerable<Type> FamilyModuleTypes()
        => AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(SafeGetTypes)
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                        && typeof(IFamilyModule).IsAssignableFrom(t)
                        && t.GetConstructor(Type.EmptyTypes) is not null);

    // A C# `using` is compile-time only: a family assembly loads into the AppDomain lazily, on first
    // runtime USE of one of its types. This test never touches a concrete family type (it is
    // family-agnostic), so nudge every Babelstone.* assembly in the output dir to load by NAME — the
    // same idiom AvroCatalogSweepTests uses. Assembly.Load is idempotent for already-loaded ones.
    private static void EnsureFamilyAssembliesLoaded()
    {
        foreach (var dll in Directory.EnumerateFiles(AppContext.BaseDirectory, "Babelstone.*.dll"))
        {
            try
            {
                Assembly.Load(AssemblyName.GetAssemblyName(dll));
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
            {
                // Native/unmanaged or unresolvable sidecar — not a family assembly; skip.
            }
        }
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
