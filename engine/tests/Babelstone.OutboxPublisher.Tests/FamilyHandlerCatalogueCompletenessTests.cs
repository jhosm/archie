using System.Reflection;
using Babelstone.Engine;
using Babelstone.Engine.Avro;
using Xunit;

namespace Babelstone.OutboxPublisher.Tests;

/// <summary>
/// bd babelstone-e6fr.6 (the A.6b thin slice): a startup-style completeness guard that every event type
/// the engine ships an integration CONTRACT for (an embedded <c>.avsc</c> in the <see cref="AvroSchemaCatalog"/>,
/// == the AsyncAPI EventCatalog set) has a registered fold handler in some family module. This catches the
/// real, independent mistake — "shipped a wire contract but forgot to register its handler" — the moment a
/// schema enters the catalog, against an artefact (the contract set) authored for a DIFFERENT reason than the
/// handler list, so the check is genuinely independent (unlike a hand-copied taxonomy of the store-only events).
/// </summary>
/// <remarks>
/// <para>
/// This is the deliberately-scoped v1 form of A.6b. The originally-filed full bidirectional cross-check
/// (the loader proving a family module's declared event types are EXACTLY a CUE-declared event taxonomy) was
/// DROPPED, not deferred: (1) no CUE event-type taxonomy exists to check against — the CUE schemas are
/// variant-config, not an event taxonomy (ADR-PC-006); and (2) the aggregate fold is already FAIL-CLOSED —
/// <c>AggregateRuntime.FoldAsync</c> throws "No handler registered for event type" on any unhandled event at
/// append AND replay, and <c>HandlerRegistry</c> throws on duplicate registrations, so a missing/typo'd handler
/// can never silently mis-fold an event. The full check would only SHIFT-LEFT an already-loud failure, and for
/// the store-only events (which have no independent contract) its taxonomy would be a hand-copy of the handler
/// list — weak independence. This catalogue-driven check keeps the genuinely-valuable, genuinely-independent
/// half: the bus contracts already declare their event types, so a forgotten handler for one is caught here.
/// </para>
/// <para>
/// Direction (mirrors <see cref="AvroCatalogSweepTests"/>): driven by the CATALOG, not by the family modules.
/// An event with no <c>.avsc</c> is legitimately store-only (not on the wire) and is NOT required to be here.
/// </para>
/// </remarks>
public sealed class FamilyHandlerCatalogueCompletenessTests
{
    // One HandlerRegistry PER family. The cross-cutting (operations.*) handlers are projection-state-specific
    // (CrossCuttingEventRegistrations.For&lt;TState&gt;()), so a single MERGED registry across families would
    // reject them as duplicate registrations — the host likewise composes one registry per family. So a
    // catalogued event "has a handler" iff SOME family registry resolves it (its own events resolve in its
    // family; a cross-cutting event resolves in every family).
    private static readonly IReadOnlyList<HandlerRegistry> FamilyRegistries = BuildPerFamilyRegistries();

    private static IReadOnlyList<HandlerRegistry> BuildPerFamilyRegistries()
    {
        // Discover family assemblies by NAME from the test output directory (Babelstone.Families.*.dll) — the
        // same family-agnostic probe AvroCatalogSweepTests and the production HostModuleLoader use, so a NEW
        // family is picked up automatically without naming any family here. The csproj ProjectReferences copy
        // each Babelstone.Families.*.dll next to the test binary; FamilyModuleLoader then discovers every
        // IFamilyModule across them.
        var familyAssemblies = Directory
            .EnumerateFiles(AppContext.BaseDirectory, "Babelstone.Families.*.dll")
            .Select(dll =>
            {
                try
                {
                    return Assembly.Load(AssemblyName.GetAssemblyName(dll));
                }
                catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
                {
                    return null; // native/unmanaged or unresolvable sidecar — not a family assembly; skip.
                }
            })
            .Where(a => a is not null)
            .Cast<Assembly>()
            .ToList();

        var loader = new FamilyModuleLoader();
        var modules = loader.LoadAll(familyAssemblies);
        return [.. modules.Select(m => loader.BuildRegistry([m]))];
    }

    public static TheoryData<string> CataloguedEventTypes()
    {
        var data = new TheoryData<string>();
        foreach (var entry in new AvroSchemaCatalog().Entries)
        {
            // DOWNSTREAM-producer schemas (ADR-IC-017 amendment §3 — x-producer != engine, e.g.
            // notification's SCHEDULED NotificationDue) are engine-OWNED but not engine-EMITTED, so no
            // family module registers a fold handler for them. Exempt them from the handler-completeness
            // sweep exactly as the shell gate exempts them from the §P3 relay-capable-engine leg.
            if (DownstreamProducerSchemas.RecordNames.Contains(entry.Schema.Name))
                continue;
            data.Add(entry.EventType);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(CataloguedEventTypes))]
    public void Every_catalogued_event_type_has_a_registered_handler(string eventType)
    {
        var resolved = FamilyRegistries.Any(r => r.TryResolveByEventType(eventType, out _));
        Assert.True(
            resolved,
            $"Catalogued event '{eventType}' ships an .avsc / EventCatalog contract but no family module "
            + "registers a fold handler for it. Either register the handler in the owning family module "
            + "(or the cross-cutting registrations) or drop the schema if the event is not real.");
    }

    [Fact]
    public void At_least_one_family_and_one_catalogued_event_exist()
    {
        // Guard the guard: a zero-iteration theory or an empty registry list would make the completeness
        // check vacuously pass. Pin that both sides are non-empty so the sweep above is real (and that the
        // family-assembly discovery actually found the families copied next to the test binary).
        Assert.NotEmpty(FamilyRegistries);
        Assert.NotEmpty(new AvroSchemaCatalog().Entries);
    }
}
