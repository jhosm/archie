using System.Reflection;

namespace Babelstone.Composition;

/// <summary>
/// The one shared family-module discovery mechanism (ADR-PC-040 §D4) — a generic primitive. In plain
/// terms: every Babelstone host composes product "family" plug-ins it must not name in code, so each
/// host discovers them by scanning the family assemblies shipped beside it for implementations of its
/// module contract. That scan was implemented three times, near-verbatim (the engine's
/// <c>HostModuleLoader</c>, the lifecycle driver's <c>LifecycleModuleLoader</c>, the notification
/// host's <c>NotificationModuleLoader</c>); this class is the extraction, so every host — present and
/// future — gets discovery for free and none can drift from the mechanics.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is shared here, and what stays estate-owned.</b> The scanner owns the mechanics: the
/// concrete-implementation scan, fail-loud activation diagnostics at the discovery seam (never deep
/// inside <c>Activator</c> or at first use), the duplicate-key throw BEFORE composing (two modules
/// claiming the same family would silently double-wire it), the stable assembly-then-type ordering
/// (so per-module composition loops run deterministically across boots, not in reflection's
/// unspecified enumeration order), and the two-anchor candidate-assembly enumeration
/// (<see cref="FamilyAssemblies"/>). Each estate keeps a thin, estate-named loader that delegates
/// here with its own module contract, diagnostics vocabulary, and any estate-specific cross-check
/// (e.g. the engine's pack-manifest fail-closed check, ADR-PC-007 §A1).
/// </para>
/// <para>
/// <b>Generic by construction.</b> Like everything in <c>Babelstone.Composition</c>, this names no family,
/// no engine type, and no module contract: the contract arrives as <c>TModule</c> on <see cref="LoadAll"/>, the
/// family key as a selector, and the diagnostics vocabulary as strings. The only product-adjacent
/// knowledge is the <see cref="FamilyAssemblyNamePrefix"/> — the family-agnostic membership predicate
/// (a NAME PATTERN, not a family) the whole plug-in design keys on (ADR-PC-021).
/// </para>
/// <para>
/// <b>Reflection stays at the composition root.</b> Only a host's loader calls this (the ADR-PC-040
/// §D2 <c>CompositionRoot</c> role — the standing exemption that MAY compose families); the scan is
/// in-process over the host's OWN compile graph and output directory, deliberately NOT an
/// <c>Assembly.LoadFrom</c> glob over an external plugin directory — keeping compile-time type safety
/// and greppability (the stance ADR-PC-021 blessed).
/// </para>
/// </remarks>
public static class FamilyModuleScanner
{
    /// <summary>
    /// The family-agnostic membership predicate: an assembly is a family contribution iff its simple
    /// name starts with this prefix. A pattern, never a family name — adding a family edits nothing.
    /// </summary>
    public const string FamilyAssemblyNamePrefix = "Babelstone.Families.";

    /// <summary>
    /// Scan the given assemblies for concrete <typeparamref name="TModule"/> implementations, activate
    /// one of each, and return them in a stable order (assembly simple name, then full type name).
    /// Throws fail-loud at this discovery seam on a module that cannot be activated or on a duplicate
    /// <paramref name="familyKey"/> value.
    /// </summary>
    /// <typeparam name="TModule">The estate's module contract (e.g. the engine's
    /// <c>IFamilyHostModule</c>).</typeparam>
    /// <param name="sources">The candidate assemblies — typically <see cref="FamilyAssemblies"/> of the
    /// host assembly.</param>
    /// <param name="familyKey">Selects the per-module uniqueness key (the module's family name, or the
    /// saga type for saga modules); two discovered modules with the same key fail loud BEFORE
    /// composition, since both would wire the same thing twice.</param>
    /// <param name="moduleKind">The estate's human vocabulary for a module (e.g.
    /// <c>"family host module"</c>), used verbatim in diagnostics so a failure names the estate's own
    /// concept, not a generic one.</param>
    /// <param name="duplicateExplanation">The estate-specific consequence sentence appended to the
    /// duplicate-key error (what exactly would be double-registered, and which ADR says one-per-family).</param>
    /// <param name="activate">How to construct a discovered module type. Null (the default) requires a
    /// public parameterless constructor — the engine/lifecycle/notification contract — and throws a
    /// diagnosable error naming the module when it is missing. An estate whose modules take
    /// composition ingredients (the orchestrator's <c>ISagaModule(SagaModuleContext)</c>) supplies its
    /// own activator with its own fail-loud diagnostic.</param>
    public static IReadOnlyList<TModule> LoadAll<TModule>(
        IReadOnlyList<Assembly> sources,
        Func<TModule, string> familyKey,
        string moduleKind,
        string duplicateExplanation,
        Func<Type, TModule>? activate = null)
        where TModule : class
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(familyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(duplicateExplanation);

        // The default activation contract: a public parameterless ctor. A module with a constructor
        // dependency would otherwise fail deep inside Activator with a bare MissingMethodException
        // naming no module — surface a diagnosable error at the discovery seam instead.
        activate ??= type =>
        {
            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new InvalidOperationException(
                    $"{CapitalizeFirst(moduleKind)} '{type.FullName}' must have a public parameterless constructor.");
            }

            return (TModule)Activator.CreateInstance(type)!;
        };

        var modules = new List<TModule>();
        foreach (var assembly in sources)
        {
            foreach (var type in LoadableTypes(assembly))
            {
                if (type is not { IsAbstract: false, IsInterface: false }
                    || !typeof(TModule).IsAssignableFrom(type))
                {
                    continue;
                }

                modules.Add(activate(type));
            }
        }

        // Fail loud on a duplicate key BEFORE composing — two modules claiming the same family/key
        // would each register that family's services, a silent double-wire. A collision at composition
        // is a build/wiring bug, not a runtime condition.
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            var key = familyKey(module);
            if (!seenKeys.Add(key))
            {
                // The key noun is the caller's (a family name for the host/lifecycle/notification
                // contracts, a saga type for saga modules) — the message stays key-noun-agnostic.
                throw new InvalidOperationException(
                    $"Duplicate {moduleKind} '{key}'. {duplicateExplanation}");
            }
        }

        // Stable order (assembly simple name, then full type name) so the host's per-module
        // composition loops run deterministically across boots rather than depending on reflection's
        // unspecified type-enumeration order (load-bearing for e.g. the engine's
        // engine-before-family migration-ordering reproducibility, ADR-PC-021).
        modules.Sort((left, right) =>
        {
            var byAssembly = string.CompareOrdinal(
                left.GetType().Assembly.GetName().Name,
                right.GetType().Assembly.GetName().Name);
            return byAssembly != 0
                ? byAssembly
                : string.CompareOrdinal(left.GetType().FullName, right.GetType().FullName);
        });

        return modules;
    }

    /// <summary>
    /// The candidate assemblies to scan: the family assemblies shipped alongside the given in-tree
    /// host, plus the host assembly itself. Two complementary anchors, both keyed off the
    /// <see cref="FamilyAssemblyNamePrefix"/> (no family is named): (1) the host assembly's
    /// compile-reference graph (<c>host.GetReferencedAssemblies()</c>) — valid when the host still
    /// names a family type; and (2) the OUTPUT-directory probe (<c>Babelstone.Families.*.dll</c> in
    /// <see cref="AppContext.BaseDirectory"/>), the robust primary anchor.
    /// </summary>
    /// <remarks>
    /// The probe is load-bearing: the C# compiler ELIDES a <c>ProjectReference</c> from the IL
    /// metadata reference list when no type in it is used in code, and a conformant host's composition
    /// names NO family type (ADR-PC-040 §D3) — so the compile-graph anchor alone would discover ZERO
    /// families. The family <c>ProjectReference</c>s (kept as the scan anchor — the §D2 composition-root
    /// exemption) copy each <c>Babelstone.Families.*.dll</c> next to the host in the output directory,
    /// identically under <c>dotnet run</c> and an in-process test host, so the probe finds them by
    /// file. Pass the HOST assembly (e.g. <c>typeof(MyLoader).Assembly</c>), never
    /// <see cref="Assembly.GetEntryAssembly"/>: under an in-process test host the entry assembly is
    /// the test runner, which carries no family references — anchoring there would silently discover
    /// nothing. A dll that fails to load by simple name (a native/satellite sidecar matching the glob)
    /// is skipped, never fatal; a genuinely missing family surfaces at the estate's own fail-closed
    /// checks.
    /// </remarks>
    public static IReadOnlyList<Assembly> FamilyAssemblies(Assembly hostAssembly)
    {
        ArgumentNullException.ThrowIfNull(hostAssembly);

        var assemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal)
        {
            [hostAssembly.GetName().Name!] = hostAssembly,
        };

        // (1) The compile-reference graph: correct when the host DOES still name a family type in code
        // (the compiler then emits the IL metadata reference). Kept for that case; the probe below is
        // the anchor that survives the reference being elided.
        foreach (var reference in hostAssembly.GetReferencedAssemblies())
        {
            var name = reference.Name;
            if (name is null
                || !name.StartsWith(FamilyAssemblyNamePrefix, StringComparison.Ordinal)
                || assemblies.ContainsKey(name))
            {
                continue;
            }

            assemblies[name] = Assembly.Load(reference);
        }

        // (2) The base-directory probe: the family ProjectReferences copy their
        // Babelstone.Families.*.dll next to the host in the OUTPUT directory. Discovering them here,
        // by FILE, keeps assembly-scan working even though the host names no family type in code.
        foreach (var dll in Directory.EnumerateFiles(
            AppContext.BaseDirectory, FamilyAssemblyNamePrefix + "*.dll", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(dll);
            if (assemblies.ContainsKey(name))
            {
                continue;
            }

            try
            {
                assemblies[name] = Assembly.Load(new AssemblyName(name));
            }
            catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException or FileLoadException)
            {
                // Not a loadable managed family assembly by simple name (e.g. a native/satellite
                // sidecar matching the glob) — skip it. A real family with a loadable module is still
                // discovered; a genuinely absent one fails the estate's own fail-closed check.
            }
        }

        return [.. assemblies.Values];
    }

    // One unloadable type in a scanned assembly must not abort discovery of all modules.
    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
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

    private static string CapitalizeFirst(string value)
    {
        return char.ToUpperInvariant(value[0]) + value[1..];
    }
}
