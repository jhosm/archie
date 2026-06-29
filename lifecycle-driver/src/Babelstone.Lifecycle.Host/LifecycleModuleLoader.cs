using System.Reflection;
using Babelstone.Lifecycle;

namespace Babelstone.Lifecycle.Host;

/// <summary>
/// Discovers the <see cref="IFamilyLifecycleModule"/> contributions the lifecycle-command driver host composes,
/// by assembly-scan over the family assemblies shipped beside the host (ADR-PC-036; ADR-PC-021
/// "explicit-list-now, assembly-scan-later" / "composition is discovery at the host edge"). It is the
/// lifecycle-driver twin of the engine's <c>HostModuleLoader</c>: same public-parameterless-ctor activation,
/// same fail-loud duplicate-family diagnostics, same stable ordering — applied to the per-family lifecycle
/// modules.
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
/// (<c>Babelstone.Lifecycle.Host</c>) — the standing exemption that MAY name a family (ADR-PC-021) — never
/// in the driver core (which stays family-agnostic). It is an in-process scan over the host's OWN output
/// directory, NOT an <c>Assembly.LoadFrom</c> glob over an external plugin directory — keeping compile-time
/// type safety and greppability.
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

        var modules = new List<IFamilyLifecycleModule>();
        foreach (var assembly in sources)
        {
            foreach (var type in LoadableTypes(assembly))
            {
                if (type is not { IsAbstract: false, IsInterface: false }
                    || !typeof(IFamilyLifecycleModule).IsAssignableFrom(type))
                {
                    continue;
                }

                // A module with a constructor dependency would otherwise fail deep inside Activator with a bare
                // MissingMethodException naming no module. Surface a diagnosable error at the discovery seam.
                if (type.GetConstructor(Type.EmptyTypes) is null)
                {
                    throw new InvalidOperationException(
                        $"Family lifecycle module '{type.FullName}' must have a public parameterless constructor.");
                }

                modules.Add((IFamilyLifecycleModule)Activator.CreateInstance(type)!);
            }
        }

        // Fail loud on a duplicate (family) registration BEFORE composing — two modules claiming the same family
        // would each register that family's rule + read-model store, a silent double-wire. This is the
        // lifecycle-side analogue of the engine HostModuleLoader's duplicate-family throw: a collision at
        // composition is a build/wiring bug, not a runtime condition.
        var seenFamilies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            if (!seenFamilies.Add(module.FamilyName))
            {
                throw new InvalidOperationException(
                    $"Duplicate family lifecycle module for family '{module.FamilyName}'. Each family contributes "
                    + "exactly one IFamilyLifecycleModule (ADR-PC-036 / ADR-PC-021); two modules "
                    + "composing the same family would double-register its rule and read-model store.");
            }
        }

        // Stable order (assembly name, then full type name) so the host's per-module ConfigureServices loop runs
        // deterministically across boots rather than depending on reflection's unspecified type-enumeration order.
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
    /// The candidate assemblies to scan: the family assemblies shipped alongside the in-tree host. Two
    /// complementary anchors, both keyed off the <c>Babelstone.Families.</c> name prefix (the family-agnostic
    /// membership predicate — no family is named): (1) the host assembly's compile-reference graph
    /// (<c>host.GetReferencedAssemblies()</c>) — valid when the host still names a family type; and (2) the
    /// OUTPUT-directory probe (<c>Babelstone.Families.*.dll</c> in <see cref="AppContext.BaseDirectory"/>), the
    /// robust primary anchor.
    /// </summary>
    /// <remarks>
    /// The probe matters because the C# compiler ELIDES a <c>ProjectReference</c> from the IL metadata reference
    /// list when no type in it is used in code, and the host's composition now names NO family type — so the
    /// compile-graph anchor alone would discover ZERO families. The family <c>ProjectReference</c>s copy each
    /// <c>Babelstone.Families.*.dll</c> next to the host in the output dir, so the probe finds them by file. The
    /// anchor is <c>typeof(LifecycleModuleLoader).Assembly</c> — the host assembly that carries the family
    /// <c>ProjectReference</c>s — NOT <see cref="Assembly.GetEntryAssembly"/>, so discovery is identical whether
    /// the host is booted as a process (<c>dotnet run</c>) or referenced in-process by a test (whose entry
    /// assembly is the test runner, which does not carry the family references). A dll that fails to load by
    /// simple name (a native/satellite sidecar matching the glob) is skipped, never fatal.
    /// </remarks>
    public static IReadOnlyList<Assembly> FamilyLifecycleAssemblies()
    {
        var host = typeof(LifecycleModuleLoader).Assembly;

        var assemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal)
        {
            [host.GetName().Name!] = host,
        };

        // (1) The host's compile-reference graph: correct when the host DOES still name a family type, but the
        // compiler elides an unused ProjectReference, and the host's composition names no family type — so this
        // pass alone would discover nothing post-relocation. We keep it and add the base-directory probe below.
        foreach (var reference in host.GetReferencedAssemblies())
        {
            var name = reference.Name;
            if (name is null
                || !name.StartsWith("Babelstone.Families.", StringComparison.Ordinal)
                || assemblies.ContainsKey(name))
            {
                continue;
            }

            assemblies[name] = Assembly.Load(reference);
        }

        // (2) The base-directory probe: the family ProjectReferences copy their Babelstone.Families.*.dll next to
        // the host in the OUTPUT directory, identically under `dotnet run` and an in-process test. Discovering
        // them here, by FILE, keeps assembly-scan working even though the host names no family type in code.
        foreach (var dll in Directory.EnumerateFiles(
            AppContext.BaseDirectory, "Babelstone.Families.*.dll", SearchOption.TopDirectoryOnly))
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
                // Not a loadable managed family assembly by simple name (e.g. a native/satellite sidecar matching
                // the glob) — skip it. A real family with a loadable module is still discovered.
            }
        }

        return [.. assemblies.Values];
    }

    // One unloadable type in a scanned assembly must not abort discovery of all modules (the same loadable-types
    // guard the engine host's module loader uses).
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
}
