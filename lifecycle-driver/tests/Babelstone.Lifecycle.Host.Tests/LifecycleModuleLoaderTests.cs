using Babelstone.Lifecycle;
using Babelstone.Lifecycle.Host;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Babelstone.Lifecycle.Host.Tests;

/// <summary>
/// Tests for <see cref="LifecycleModuleLoader"/> — the host's assembly-scan discovery of family
/// <c>IFamilyLifecycleModule</c> contributions (ADR-PC-036; ADR-PC-021). They are the
/// fitness proof of the open/closed property the refactor buys: a family's clock-driven lifecycle is discovered
/// by scanning the <c>Babelstone.Families.*.Lifecycle</c> assemblies shipped beside the host, so adding one is
/// its module + a host <c>ProjectReference</c> — never an edit to the host's composition (ADR-PC-036). The
/// test names no family TYPE; it asserts the loader returns each family by NAME, exactly as the host boots it.
/// </summary>
public sealed class LifecycleModuleLoaderTests
{
    [Fact]
    public void Discovers_every_family_lifecycle_module_by_assembly_scan()
    {
        var modules = new LifecycleModuleLoader().LoadAll(LifecycleModuleLoader.FamilyLifecycleAssemblies());

        var families = modules.Select(m => m.FamilyName).ToList();

        // Every shipped family is discovered with no host-composition edit naming them — including
        // current_account, whose lifecycle contribution is the projection-derived hold-expiry rule
        // (ADR-PC-037).
        Assert.Contains("term_deposit", families);
        Assert.Contains("personal_loan", families);
        Assert.Contains("current_account", families);

        // Each family contributes exactly one module — the loader's duplicate-family guard would have thrown.
        Assert.Equal(families.Count, families.Distinct().Count());
    }

    [Fact]
    public void Returns_modules_in_a_stable_order_across_calls()
    {
        var loader = new LifecycleModuleLoader();

        var first = loader.LoadAll(LifecycleModuleLoader.FamilyLifecycleAssemblies())
            .Select(m => m.FamilyName).ToList();
        var second = loader.LoadAll(LifecycleModuleLoader.FamilyLifecycleAssemblies())
            .Select(m => m.FamilyName).ToList();

        // Stable (assembly-name, then type-name) ordering — independent of reflection's enumeration order — so
        // the host's per-module ConfigureServices loop composes identically across boots.
        Assert.Equal(first, second);
    }

    [Fact]
    public void Fails_loud_when_two_modules_claim_the_same_family()
    {
        // Scan THIS test assembly, which defines two IFamilyLifecycleModule types claiming the same family —
        // the load-time collision the loader must reject before composing (two modules would double-register a
        // family's rule + read-model store). Proves the fail-loud guard the discovery comments assert.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new LifecycleModuleLoader().LoadAll([typeof(LifecycleModuleLoaderTests).Assembly]));

        Assert.Contains("Duplicate family lifecycle module", ex.Message);
        Assert.Contains(DuplicateFamily, ex.Message);
    }

    private const string DuplicateFamily = "duplicate_family_fixture";

    // Two contributions claiming the SAME family, defined here so a scan of this test assembly trips the
    // loader's duplicate-FamilyName guard. They are not Babelstone.Families.* assemblies, so the real
    // FamilyLifecycleAssemblies() probe never discovers them — only the explicit LoadAll above does.
    public sealed class DuplicateFamilyModuleA : IFamilyLifecycleModule
    {
        public string FamilyName => DuplicateFamily;

        public void ConfigureServices(IServiceCollection services, LifecycleModuleContext ctx)
        {
        }
    }

    public sealed class DuplicateFamilyModuleB : IFamilyLifecycleModule
    {
        public string FamilyName => DuplicateFamily;

        public void ConfigureServices(IServiceCollection services, LifecycleModuleContext ctx)
        {
        }
    }
}
