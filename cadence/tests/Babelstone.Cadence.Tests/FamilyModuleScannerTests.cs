using System.Reflection;
using Babelstone.Cadence;
using Xunit;

namespace Babelstone.Cadence.Tests;

/// <summary>
/// Tests for <see cref="FamilyModuleScanner"/> — the one shared family-module discovery mechanism
/// (ADR-PC-040 §D4) the estate loaders (engine <c>HostModuleLoader</c>, lifecycle
/// <c>LifecycleModuleLoader</c>, notification <c>NotificationModuleLoader</c>, orchestrator
/// <c>SagaModuleLoader</c>) delegate to. The mechanics pinned here are the ones every host relies on:
/// concrete-implementation scan, stable ordering, fail-loud duplicate-key and activation diagnostics,
/// and custom activation for module contracts with constructor ingredients. The estate-facing halves
/// (real family assemblies discovered beside a real host) stay pinned by each estate's own loader
/// tests — this is the mechanism-level layer.
/// </summary>
public sealed class FamilyModuleScannerTests
{
    /// <summary>A test-local module contract — the scanner is generic over the estate's own.</summary>
    public interface IFakeModule
    {
        string FamilyName { get; }
    }

    public sealed class AlphaModule : IFakeModule
    {
        public string FamilyName => "alpha";
    }

    public sealed class BetaModule : IFakeModule
    {
        public string FamilyName => "beta";
    }

    /// <summary>Abstract implementations are declarations, not contributions — never activated.</summary>
    public abstract class AbstractModule : IFakeModule
    {
        public abstract string FamilyName { get; }
    }

    /// <summary>A second contract, to prove the scan is contract-scoped (its implementations must not
    /// leak into an <see cref="IFakeModule"/> scan).</summary>
    public interface IOtherContract
    {
        string Name { get; }
    }

    public sealed class OtherModule : IOtherContract
    {
        public string Name => "other";
    }

    public interface IDuplicateKeyModule
    {
        string FamilyName { get; }
    }

    public sealed class DupOne : IDuplicateKeyModule
    {
        public string FamilyName => "collision";
    }

    public sealed class DupTwo : IDuplicateKeyModule
    {
        public string FamilyName => "collision";
    }

    public interface ICtorArgModule
    {
        string FamilyName { get; }
    }

    public sealed class NeedsContextModule : ICtorArgModule
    {
        public NeedsContextModule(string context)
        {
            FamilyName = context;
        }

        public string FamilyName { get; }
    }

    private static IReadOnlyList<Assembly> Self => [typeof(FamilyModuleScannerTests).Assembly];

    [Fact]
    public void Discovers_concrete_implementations_only_in_a_stable_order()
    {
        var modules = FamilyModuleScanner.LoadAll<IFakeModule>(
            Self, m => m.FamilyName, "fake module", "One per family.");

        // Both concrete implementations found; the abstract declaration is not activated; the
        // other-contract implementation does not leak in.
        Assert.Equal(2, modules.Count);
        Assert.Contains(modules, m => m.FamilyName == "alpha");
        Assert.Contains(modules, m => m.FamilyName == "beta");

        // Stable order: same assembly, so ordered by full type name — deterministic across runs
        // (the property the hosts' per-module composition loops rely on).
        var names = modules.Select(m => m.GetType().FullName).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToList(), names);

        // And re-running the scan yields the identical order.
        var again = FamilyModuleScanner.LoadAll<IFakeModule>(
            Self, m => m.FamilyName, "fake module", "One per family.");
        Assert.Equal(names, again.Select(m => m.GetType().FullName).ToList());
    }

    [Fact]
    public void Throws_on_a_duplicate_family_key_naming_the_kind_the_key_and_the_consequence()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FamilyModuleScanner.LoadAll<IDuplicateKeyModule>(
                Self, m => m.FamilyName, "fake saga module", "Each family contributes exactly one."));

        // The estate's own vocabulary, the colliding key, and the estate's consequence sentence all
        // surface — the diagnostics contract the estate loaders' messages are built from.
        Assert.Contains("Duplicate fake saga module", ex.Message);
        Assert.Contains("collision", ex.Message);
        Assert.Contains("Each family contributes exactly one.", ex.Message);
    }

    [Fact]
    public void Default_activation_throws_a_diagnosable_error_on_a_missing_parameterless_ctor()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FamilyModuleScanner.LoadAll<ICtorArgModule>(
                Self, m => m.FamilyName, "fake module", "One per family."));

        // Fail-loud AT THE DISCOVERY SEAM, naming the module — never a bare MissingMethodException
        // deep inside Activator.
        Assert.Contains("public parameterless constructor", ex.Message);
        Assert.Contains(nameof(NeedsContextModule), ex.Message);
        // The module-kind vocabulary is capitalized into sentence position.
        Assert.StartsWith("Fake module", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_activation_supports_constructor_ingredients()
    {
        // The orchestrator shape: modules take composition ingredients, so the loader supplies its
        // own activator instead of the parameterless default.
        var modules = FamilyModuleScanner.LoadAll<ICtorArgModule>(
            Self,
            m => m.FamilyName,
            "fake module",
            "One per family.",
            type => (ICtorArgModule)Activator.CreateInstance(type, "from-context")!);

        var module = Assert.Single(modules);
        Assert.Equal("from-context", module.FamilyName);
    }

    [Fact]
    public void FamilyAssemblies_always_includes_the_host_and_only_family_prefixed_references()
    {
        // This test assembly references no Babelstone.Families.* assembly and none ships beside it,
        // so the enumeration is exactly the host itself — the invariant half of the two-anchor
        // contract that CAN be pinned estate-independently. The discovered-beside-a-real-host half is
        // pinned by the estate loader tests (HostModuleLoaderTests / LifecycleModuleLoaderTests /
        // NotificationModuleLoaderTests / SagaModuleLoaderTests), which run with real family dlls in
        // their output directories.
        var host = typeof(FamilyModuleScannerTests).Assembly;

        var assemblies = FamilyModuleScanner.FamilyAssemblies(host);

        Assert.Contains(host, assemblies);
        foreach (var assembly in assemblies)
        {
            var name = assembly.GetName().Name!;
            Assert.True(
                assembly == host || name.StartsWith(FamilyModuleScanner.FamilyAssemblyNamePrefix, StringComparison.Ordinal),
                $"unexpected non-family assembly in the scan set: {name}");
        }
    }
}
