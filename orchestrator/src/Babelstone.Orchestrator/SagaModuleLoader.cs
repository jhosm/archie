using System.Reflection;
using Babelstone.Composition;
using Babelstone.Orchestrator.Saga;

namespace Babelstone.Orchestrator;

/// <summary>
/// Discovers the family-owned <see cref="ISagaModule"/> contributions the orchestrator host composes,
/// by assembly-scan over the family assemblies shipped beside the host — the realisation of
/// ADR-IC-018 §D6's "explicit-list-now, assembly-scan-later" posture (Revised 2026-07-02;
/// ADR-PC-040 §D3/§D4). It is the orchestrator twin of the engine's <c>HostModuleLoader</c>, the
/// lifecycle driver's <c>LifecycleModuleLoader</c>, and the notification host's
/// <c>NotificationModuleLoader</c> — the same shared <see cref="FamilyModuleScanner"/> mechanics
/// (fail-loud activation, duplicate-key throw, stable ordering, two-anchor assembly enumeration),
/// with one estate-specific twist: an <see cref="ISagaModule"/> takes its composition ingredients
/// (the <see cref="SagaModuleContext"/>) in its constructor, so this loader supplies a custom
/// activator instead of the parameterless default.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope: FAMILY saga modules only.</b> This scans the <c>Babelstone.Families.*</c> assemblies
/// (plus the host itself, which defines none) — deliberately NOT the substrate assembly: the
/// substrate-owned settlement saga (<c>SettlementSagaModule</c>, ADR-PC-032 / ADR-IC-018 Amendment
/// A1/A2) is family-AGNOSTIC and takes its Movement-bearing subscribe topics as a constructor
/// ingredient, so the host constructs it explicitly, feeding it the union of the DISCOVERED family
/// modules' <see cref="ISagaModule.FamilyIntegrationTopics"/> declarations. Naming a substrate type
/// is legal in the host (it is not a family name); the point of discovery is that no FAMILY is named.
/// </para>
/// <para>
/// <b>Reflection stays at the composition root.</b> This type lives in the host
/// (<c>Babelstone.Orchestrator</c>) — the ADR-IC-018 §D4 composition root, marked
/// <c>&lt;BabelstoneRole&gt;CompositionRoot&lt;/BabelstoneRole&gt;</c> (ADR-PC-040 §D2) — never in the
/// substrate, which stays family-agnostic (ORCH-1/ORCH-2).
/// </para>
/// </remarks>
public sealed class SagaModuleLoader
{
    /// <summary>
    /// Scan the given assemblies for concrete <see cref="ISagaModule"/> implementations, activate one
    /// of each through its <c>(SagaModuleContext)</c> constructor, and return them in a stable order.
    /// Throws fail-loud at this discovery seam on a module without that constructor shape or on a
    /// duplicate <see cref="ISagaModule.SagaType"/> (two modules governing the same saga type would
    /// double-register its machine/bridge/router and collide in the saga_type registries).
    /// </summary>
    public IReadOnlyList<ISagaModule> LoadAll(IReadOnlyList<Assembly> sources, SagaModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(context);

        return FamilyModuleScanner.LoadAll<ISagaModule>(
            sources,
            module => module.SagaType,
            "family saga module",
            "Each saga type is governed by exactly one ISagaModule (ADR-IC-018 §D1/§P4); two modules "
            + "for the same saga_type would double-register its machine, bridge, and router.",
            type =>
            {
                // The saga-module activation contract: a public constructor taking exactly the
                // host-supplied SagaModuleContext (the shape both shipped family modules use). Surface
                // a diagnosable error at the discovery seam — never a bare MissingMethodException deep
                // inside Activator.
                if (type.GetConstructor([typeof(SagaModuleContext)]) is null)
                {
                    throw new InvalidOperationException(
                        $"Family saga module '{type.FullName}' must have a public constructor taking "
                        + "(SagaModuleContext) — the host-supplied composition ingredients "
                        + "(ADR-IC-018 §P4).");
                }

                return (ISagaModule)Activator.CreateInstance(type, context)!;
            });
    }

    /// <summary>
    /// The candidate assemblies to scan: the family orchestration assemblies shipped alongside the
    /// in-tree host — the shared two-anchor enumeration
    /// (<see cref="FamilyModuleScanner.FamilyAssemblies"/>, ADR-PC-040 §D4): the host's
    /// compile-reference graph (valid while the host still names a family type) + the OUTPUT-directory
    /// <c>Babelstone.Families.*.dll</c> probe (the robust primary anchor — the compiler elides an
    /// unused <c>ProjectReference</c> from IL metadata, and this host's composition names no family
    /// type). Anchored on <c>typeof(SagaModuleLoader).Assembly</c>, never the entry assembly, so
    /// discovery is identical booted as a process or referenced in-process by a test. The substrate
    /// assembly is deliberately NOT scanned (see the class remarks).
    /// </summary>
    public static IReadOnlyList<Assembly> FamilySagaAssemblies()
    {
        return FamilyModuleScanner.FamilyAssemblies(typeof(SagaModuleLoader).Assembly);
    }
}
