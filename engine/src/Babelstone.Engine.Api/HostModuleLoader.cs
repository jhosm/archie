using System.Reflection;
using Babelstone.Engine.Hosting;

namespace Babelstone.Engine.Api;

/// <summary>
/// Discovers the <see cref="IFamilyHostModule"/> implementations the host composes, by assembly-scan
/// over the host's compile-referenced assemblies (ADR-PC-021 §A3 Option B / §P4 "composition is
/// discovery at the host/test edge"). This is the host-side twin of the engine's
/// <see cref="FamilyModuleLoader"/> for fold modules: same in-process scan, same public-parameterless-ctor
/// activation, same fail-loud diagnostics — applied to the decider+endpoint host modules instead of the
/// fold modules.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this realizes the §A3-deferred discovery.</b> Until bd <c>babelstone-9w2k.2</c> the host held the
/// explicit Option-A list <c>[new TermDepositHostModule()]</c>; ADR-PC-021 §A3 promised that swapping it for
/// <c>FamilyModuleLoader</c>-style assembly-scan would be a localized change with ZERO change to any family,
/// because every module already implements the same contract with a public parameterless ctor. This loader
/// is that change. Adding a family is now its module + the host <c>ProjectReference</c> — no list edit, no
/// surgical thread through <c>Program.cs</c>.
/// </para>
/// <para>
/// <b>§A3-BLESSED in-process scan, not drop-in plugins.</b> It scans the COMPILE-REFERENCED assemblies the
/// host already loaded (a referenced assembly must be loadable to be composed or scanned — the accepted cost
/// of an in-tree host, ADR-PC-021 §A3). It is deliberately NOT an <c>Assembly.LoadFrom</c> glob over a plugin
/// directory: the in-process scan keeps compile-time type safety, AOT-friendliness, and greppability that the
/// drop-in model gives up (§A3 "out of scope here").
/// </para>
/// <para>
/// <b>Reflection stays at the composition root.</b> This type lives in <c>Babelstone.Engine.Api</c> — the host,
/// the standing exemption that MAY name a family (ADR-PC-021 §A2) — never in the engine spine (ADR-PC-010 §P5:
/// reflection is confined to the composition root, not the dispatch spine).
/// </para>
/// <para>
/// <b>Stable ordering preserves engine-before-family migration ordering.</b> Modules are returned in a STABLE
/// order (by assembly name, then full type name) so the host's per-module loops run deterministically across
/// boots. The engine event-store schema is applied by deployment machinery/tests BEFORE any family read-model
/// migration runs (ADR-PC-021 §A6); a family module's <c>ReadModelMigrationHostedService</c> assumes the engine
/// schema is present and fails loud if it is not, so a stable, deterministic module order keeps that ordering
/// reproducible rather than dependent on reflection's unspecified type-enumeration order.
/// </para>
/// </remarks>
public sealed class HostModuleLoader
{
    /// <summary>
    /// Scan the given assemblies for concrete <see cref="IFamilyHostModule"/> implementations with a public
    /// parameterless constructor, activate one of each, and return them in a stable order. Throws fail-loud at
    /// this discovery seam — never deep inside <c>Activator</c> or at first command — on a module that cannot be
    /// constructed or on a duplicate <see cref="IFamilyHostModule.FamilyName"/> (two modules composing the same
    /// family would double-register its runtime + endpoints, the host-module analogue of
    /// <see cref="HandlerRegistry"/>'s duplicate-<c>event_type</c> throw).
    /// </summary>
    public IReadOnlyList<IFamilyHostModule> LoadAll(IReadOnlyList<Assembly> sources)
    {
        var modules = new List<IFamilyHostModule>();
        foreach (var assembly in sources)
        {
            foreach (var type in LoadableTypes(assembly))
            {
                if (type is not { IsAbstract: false, IsInterface: false } || !typeof(IFamilyHostModule).IsAssignableFrom(type))
                {
                    continue;
                }

                // A module with a constructor dependency would otherwise fail deep inside Activator with a bare
                // MissingMethodException naming no module. Surface a diagnosable error at the discovery seam
                // instead — the same stance FamilyModuleLoader takes for fold modules.
                if (type.GetConstructor(Type.EmptyTypes) is null)
                {
                    throw new InvalidOperationException(
                        $"Family host module '{type.FullName}' must have a public parameterless constructor.");
                }

                modules.Add((IFamilyHostModule)Activator.CreateInstance(type)!);
            }
        }

        // Fail loud on a duplicate (family) registration BEFORE composing — two modules claiming the same
        // family would each register that family's AggregateRuntime + endpoints, a silent double-wire. This is
        // the host-module analogue of HandlerRegistry throwing on a duplicate event_type (FamilyModule.cs):
        // a collision at composition is a build/wiring bug, not a runtime condition.
        var seenFamilies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            if (!seenFamilies.Add(module.FamilyName))
            {
                throw new InvalidOperationException(
                    $"Duplicate family host module for family '{module.FamilyName}'. Each family contributes "
                    + "exactly one IFamilyHostModule (ADR-PC-021 §A3); two modules composing the same family "
                    + "would double-register its runtime and endpoints.");
            }
        }

        // Stable order (assembly name, then full type name) so the host's per-module ConfigureServices /
        // MapEndpoints loops run deterministically across boots, preserving the engine-before-family
        // migration ordering reproducibility (ADR-PC-021 §A6) rather than depending on reflection's
        // unspecified type-enumeration order.
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
    /// The candidate assemblies to scan: the family-host assemblies the host COMPILE-references. Derived from
    /// the HOST assembly's direct references named <c>Babelstone.Families.*</c> (the host's csproj
    /// <c>ProjectReference</c>s into <c>families/**</c>) plus the host assembly itself, each force-loaded so a
    /// lazily-bound reference is materialised before the scan. This keeps discovery anchored to the COMPILE
    /// graph — a referenced assembly must be loadable to be scanned, the accepted cost of an in-tree host
    /// (ADR-PC-021 §A3) — and out of an <c>Assembly.LoadFrom</c> plugin glob (§A3 "out of scope here").
    /// Adding a family is its module + the host <c>ProjectReference</c>; THIS enumeration picks it up with no
    /// edit.
    /// </summary>
    /// <remarks>
    /// The anchor is <c>typeof(HostModuleLoader).Assembly</c> — the <c>Babelstone.Engine.Api</c> host assembly
    /// that carries the family <c>ProjectReference</c>s — NOT <see cref="Assembly.GetEntryAssembly"/>. The two
    /// coincide when the host runs as its own process (<c>dotnet run</c>), but under
    /// <c>WebApplicationFactory&lt;Program&gt;</c> (the <c>DepositsApiIntegrationTests</c> in-process boot) the
    /// entry assembly is the test runner (<c>testhost</c>), which does NOT reference the family assemblies — so
    /// anchoring on the entry assembly would discover ZERO families and silently map no endpoints. Anchoring on
    /// the host assembly itself makes discovery identical whether the host is booted as a process or hosted
    /// in-process by a test.
    /// </remarks>
    public static IReadOnlyList<Assembly> FamilyHostAssemblies()
    {
        var host = typeof(HostModuleLoader).Assembly;

        var assemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal)
        {
            [host.GetName().Name!] = host,
        };

        foreach (var reference in host.GetReferencedAssemblies())
        {
            var name = reference.Name;
            if (name is null
                || !name.StartsWith("Babelstone.Families.", StringComparison.Ordinal)
                || assemblies.ContainsKey(name))
            {
                continue;
            }

            // Force-load the referenced family assembly so its IFamilyHostModule types are present for the
            // scan (a compile reference is otherwise resolved lazily on first use). A genuinely missing
            // assembly is a deployment fault — let it surface rather than silently dropping a family.
            assemblies[name] = Assembly.Load(reference);
        }

        return [.. assemblies.Values];
    }

    // One unloadable type in a scanned assembly must not abort discovery of all modules (mirrors
    // FamilyModuleLoader.LoadableTypes).
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
