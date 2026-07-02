using System.Reflection;
using Babelstone.Cadence;
using Babelstone.Lifecycle;

namespace Babelstone.Lifecycle.Host;

/// <summary>
/// Discovers the <see cref="IFamilyLifecycleModule"/> contributions the lifecycle-command driver host composes,
/// by assembly-scan over the family assemblies shipped beside the host (ADR-PC-036; ADR-PC-021
/// "explicit-list-now, assembly-scan-later" / "composition is discovery at the host edge"). It is the
/// lifecycle-driver twin of the engine's <c>HostModuleLoader</c>: same public-parameterless-ctor activation,
/// same fail-loud duplicate-family diagnostics, same stable ordering — all delegated to the shared
/// <see cref="FamilyModuleScanner"/> (ADR-PC-040 §D4), so the mechanics are written once and every host
/// inherits them; this loader keeps only the lifecycle estate's module contract and diagnostics vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why assembly-scan, not a hand-maintained list.</b> The host used to name each family's concrete rule in
/// <c>Program.cs</c> (<c>AddSingleton&lt;ILifecycleCommandRule, MaturityRule&gt;()</c>), so a new family meant
/// editing the host. Because every family contribution implements the same <see cref="IFamilyLifecycleModule"/>
/// with a public parameterless ctor, the host can DISCOVER them instead: adding a clock-driven lifecycle to a
/// new family is its own <c>.Lifecycle</c> module + the host's <c>ProjectReference</c> to it — never an edit to
/// the host's composition (ADR-PC-036 "a fourth rule with zero core diff").
/// </para>
/// <para>
/// <b>Reflection stays at the composition root.</b> This type lives in the host
/// (<c>Babelstone.Lifecycle.Host</c>) — the standing exemption that MAY name a family (ADR-PC-021; the
/// <c>&lt;BabelstoneRole&gt;CompositionRoot&lt;/BabelstoneRole&gt;</c> marker, ADR-PC-040 §D2) — never in the
/// driver core (which stays family-agnostic). It is an in-process scan over the host's OWN output directory,
/// NOT an <c>Assembly.LoadFrom</c> glob over an external plugin directory — keeping compile-time type safety
/// and greppability.
/// </para>
/// </remarks>
public sealed class LifecycleModuleLoader
{
    /// <summary>
    /// Scan the given assemblies for concrete <see cref="IFamilyLifecycleModule"/> implementations with a public
    /// parameterless constructor, activate one of each, and return them in a stable order. Throws fail-loud at
    /// this discovery seam — never deep inside <c>Activator</c> or at first tick — on a module that cannot be
    /// constructed or on a duplicate <see cref="IFamilyLifecycleModule.FamilyName"/> (two modules composing the
    /// same family would double-register its rule + read-model store).
    /// </summary>
    public IReadOnlyList<IFamilyLifecycleModule> LoadAll(IReadOnlyList<Assembly> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        return FamilyModuleScanner.LoadAll<IFamilyLifecycleModule>(
            sources,
            module => module.FamilyName,
            "family lifecycle module",
            "Each family contributes exactly one IFamilyLifecycleModule (ADR-PC-036 / ADR-PC-021); two modules "
            + "composing the same family would double-register its rule and read-model store.");
    }

    /// <summary>
    /// The candidate assemblies to scan: the family assemblies shipped alongside the in-tree host — the shared
    /// two-anchor enumeration (<see cref="FamilyModuleScanner.FamilyAssemblies"/>, ADR-PC-040 §D4): the host's
    /// compile-reference graph (valid while the host still names a family type) + the OUTPUT-directory
    /// <c>Babelstone.Families.*.dll</c> probe (the robust primary anchor — the compiler elides an unused
    /// <c>ProjectReference</c> from IL metadata, and this host's composition names no family type). Anchored on
    /// <c>typeof(LifecycleModuleLoader).Assembly</c>, never the entry assembly, so discovery is identical booted
    /// as a process or referenced in-process by a test.
    /// </summary>
    public static IReadOnlyList<Assembly> FamilyLifecycleAssemblies()
    {
        return FamilyModuleScanner.FamilyAssemblies(typeof(LifecycleModuleLoader).Assembly);
    }
}
