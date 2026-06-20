using System.Reflection;
using Babelstone.Engine.Hosting;
using Babelstone.Packs;

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
    /// Cross-checks the discovered family host modules against the pinned pack's family-manifest
    /// (<see cref="VerifiedPack.Families"/>) and FAILS CLOSED on any skew (bd babelstone-9w2k.3 /
    /// ADR-PC-007 §P1 / ADR-PC-009 §P1). The pinned pack is the authoritative per-deployment family set;
    /// every module stamps its <see cref="IFamilyHostModule.SchemaVersion"/> onto every <c>EventEnvelope</c>
    /// (ADR-PC-009 §P1) and the registry resolves the pin through it on replay (§P2), so a family/schema
    /// skew between the code that loaded and the pack the instance is pinned to is an audit/replay hazard —
    /// NOT a tolerable degradation. Mirrors <see cref="HostPackLoading"/>'s fatal-on-load discipline: a
    /// mismatch throws here, at the composition seam, so the host exits non-zero before serving its first
    /// command rather than appending an event under a schema version its pinned pack does not recognise.
    /// </summary>
    /// <remarks>
    /// Two fail-closed directions, each naming the offending box:
    /// <list type="bullet">
    ///   <item>A discovered module whose <c>(FamilyName, AggregateType, SchemaVersion)</c> tuple is not
    ///   present — exactly — in the family-manifest (an unpinned family, or a pinned family at a different
    ///   schema version: the version-skew case).</item>
    ///   <item>A family the manifest pins for which no module loaded (a deployment that shipped the pin but
    ///   not the assembly — a saga that would silently never advance).</item>
    /// </list>
    /// The caller (<c>Program.cs</c>) logs the thrown message at <c>Critical</c> before the host exits.
    /// </remarks>
    public static void CrossCheckAgainstPackManifest(
        IReadOnlyList<IFamilyHostModule> modules, VerifiedPack pack)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(pack);

        var manifest = pack.Families;
        var manifestByFamily = manifest.ToDictionary(f => f.FamilyName, StringComparer.Ordinal);

        // Direction 1: every discovered module must be pinned, at the SAME schema version + aggregate_type.
        foreach (var module in modules)
        {
            if (!manifestByFamily.TryGetValue(module.FamilyName, out var pinned))
            {
                throw new PackLoadException(pack.VersionKey, null,
                    $"family host module '{module.FamilyName}' (assembly "
                    + $"'{module.GetType().Assembly.GetName().Name}') is not declared in the pinned pack's "
                    + $"family-manifest (families.yaml) for '{pack.VersionKey}'. The pinned pack is the "
                    + "authoritative per-deployment family set (ADR-PC-009 §P1); refusing to serve a family "
                    + "the pack does not pin.");
            }

            if (!string.Equals(pinned.SchemaVersion, module.SchemaVersion, StringComparison.Ordinal))
            {
                throw new PackLoadException(pack.VersionKey, null,
                    $"schema-version skew for family '{module.FamilyName}' (assembly "
                    + $"'{module.GetType().Assembly.GetName().Name}'): the loaded module composes "
                    + $"'{module.SchemaVersion}' but the pinned pack '{pack.VersionKey}' declares "
                    + $"'{pinned.SchemaVersion}'. Every module stamps SchemaVersion onto every EventEnvelope "
                    + "(ADR-PC-009 §P1); a skew would corrupt the audit/replay trail — refusing to serve.");
            }

            if (!string.Equals(pinned.AggregateType, module.AggregateType, StringComparison.Ordinal))
            {
                throw new PackLoadException(pack.VersionKey, null,
                    $"aggregate_type skew for family '{module.FamilyName}' (assembly "
                    + $"'{module.GetType().Assembly.GetName().Name}'): the loaded module writes under "
                    + $"'{module.AggregateType}' but the pinned pack '{pack.VersionKey}' declares "
                    + $"'{pinned.AggregateType}' — refusing to serve.");
            }
        }

        // Direction 2: every pinned family must have a loaded module — a pinned family with no module is a
        // deployment that shipped the pin but not the assembly. The saga keyed on its topic would silently
        // never advance (no replay-safe recovery), so this is fatal, not a warning.
        var loadedFamilies = new HashSet<string>(modules.Select(m => m.FamilyName), StringComparer.Ordinal);
        foreach (var pinned in manifest)
        {
            if (!loadedFamilies.Contains(pinned.FamilyName))
            {
                throw new PackLoadException(pack.VersionKey, null,
                    $"the pinned pack '{pack.VersionKey}' declares family '{pinned.FamilyName}' "
                    + $"(plugin_assembly '{pinned.PluginAssembly}', schema_version '{pinned.SchemaVersion}') "
                    + "in its family-manifest but no matching IFamilyHostModule was discovered — the "
                    + "deployment is missing the family assembly. Refusing to serve a pinned family with no "
                    + "loadable module (ADR-PC-009 §P1).");
            }
        }
    }

    /// <summary>
    /// The candidate assemblies to scan: the family-host assemblies shipped alongside the in-tree host. Two
    /// complementary anchors, both keyed off the <c>Babelstone.Families.</c> name prefix (the family-agnostic
    /// membership predicate — no family is named): (1) the host assembly's compile-reference graph
    /// (<c>host.GetReferencedAssemblies()</c>), the §A14 anchor — valid when the host still names a family
    /// type; and (2) the OUTPUT-directory probe (<c>Babelstone.Families.*.dll</c> in
    /// <see cref="AppContext.BaseDirectory"/>), the robust primary anchor. The probe matters because the C#
    /// compiler ELIDES a <c>ProjectReference</c> from the IL metadata reference list when no type in it is used
    /// in code, and the host's composition now names NO family type (bd babelstone-9w2k.5 relocated the last
    /// family wiring into the family module) — so the compile-graph anchor alone would discover ZERO families.
    /// The family <c>ProjectReference</c>s (kept per §A14) copy each <c>Babelstone.Families.*.dll</c> next to
    /// the host in the output dir, so the probe finds them by file. Both stay anchored to the in-tree COMPILE
    /// graph (a referenced assembly must be loadable to be scanned, ADR-PC-021 §A3) and out of an
    /// <c>Assembly.LoadFrom</c> plugin glob over an external directory (§A3 "out of scope here") — the probe
    /// reads only the host's OWN output dir, where its compile-referenced families land. Adding a family is its
    /// module + the host <c>ProjectReference</c>; THIS enumeration picks it up with no edit.
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

        // The host's compile-reference graph: the `Babelstone.Families.*` assemblies named in
        // host.GetReferencedAssemblies(). This is the §A14 compile-graph anchor — BUT the C# compiler
        // elides a `ProjectReference` from the IL metadata reference list when no type in it is used in
        // code, and the host's composition now names NO family type (bd babelstone-9w2k.5 relocated the
        // last family wiring into the family module). So this pass alone would discover ZERO families
        // post-relocation. We keep it (it is correct when the host DOES still reference a family type) and
        // add the base-directory probe below as the robust primary anchor.
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

        // The base-directory probe: the family `ProjectReference`s (§A14, kept as the load anchor) copy
        // their `Babelstone.Families.*.dll` next to the host in the OUTPUT directory — identically under
        // `dotnet run` (the host's own process) and `WebApplicationFactory<Program>` (the in-process test
        // boot). Discovering them HERE, by file, keeps assembly-scan working even though the host names no
        // family type in code (so the compiler emits no IL metadata reference to elide). The
        // `Babelstone.Families.` name prefix is the family-agnostic membership predicate — no family is
        // named. A DLL that fails to load is skipped (a satellite/native sidecar), never fatal here; a
        // genuinely missing family then surfaces at the pack family-manifest cross-check, fail-closed.
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
                // Not a loadable managed family assembly by simple name (e.g. a native/satellite sidecar
                // matching the glob) — skip it. A real family with a loadable module is still discovered;
                // a pack-pinned family whose assembly is genuinely absent fails the manifest cross-check.
            }
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
