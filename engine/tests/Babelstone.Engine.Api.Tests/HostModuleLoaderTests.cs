using System.Reflection;
using Babelstone.Engine.Api;
using Babelstone.Engine.Hosting;
using Babelstone.Packs;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Babelstone.Engine.Api.Tests;

/// <summary>
/// Fitness tests for <see cref="HostModuleLoader"/> — the assembly-scan host-module discovery that
/// replaces the explicit Option-A list (ADR-PC-021 §A3 Option B / §P4, bd babelstone-9w2k.2). These are
/// pure-reflection tests (no host boot, no Postgres) so they run in the Docker-free tier: they prove the
/// loader DISCOVERS public-parameterless-ctor <see cref="IFamilyHostModule"/> types, returns them in a
/// STABLE order (so the engine-before-family migration ordering, §A6, stays reproducible), FAILS LOUD on a
/// duplicate-family collision (the host-module analogue of <c>HandlerRegistry</c>'s duplicate-event_type
/// throw), and FAILS LOUD on a module without a public parameterless ctor.
/// </summary>
/// <remarks>
/// The happy-path doubles (<see cref="AlphaHostModule"/> / <see cref="BetaHostModule"/>) live in THIS test
/// assembly. The two negative cases live in their OWN fixture assemblies
/// (<c>Babelstone.HostModuleLoader.DuplicateFixture</c> / <c>.NoCtorFixture</c>) so a colliding pair / a
/// non-default-ctor module is never in this assembly's own scan — each negative scan targets exactly one
/// fault. End-to-end discovery through the real host is additionally exercised by
/// <c>DepositsApiIntegrationTests</c> (the constitute→read→mature flow boots <c>Program</c>, which now
/// composes via this loader).
/// </remarks>
public sealed class HostModuleLoaderTests
{
    [Fact]
    public void Discovers_modules_in_the_scanned_assembly()
    {
        var modules = new HostModuleLoader().LoadAll([Assembly.GetExecutingAssembly()]);

        Assert.Contains(modules, m => m.FamilyName == "alpha");
        Assert.Contains(modules, m => m.FamilyName == "beta");
    }

    [Fact]
    public void Returns_modules_in_a_stable_order()
    {
        var first = new HostModuleLoader().LoadAll([Assembly.GetExecutingAssembly()]);
        var second = new HostModuleLoader().LoadAll([Assembly.GetExecutingAssembly()]);

        Assert.Equal(
            first.Select(m => m.GetType().FullName),
            second.Select(m => m.GetType().FullName));
    }

    [Fact]
    public void Throws_on_a_duplicate_family_registration()
    {
        var fixture = typeof(global::Babelstone.HostModuleLoader.DuplicateFixture.DuplicateFamilyA).Assembly;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new HostModuleLoader().LoadAll([fixture]));

        Assert.Contains("collision", ex.Message);
        Assert.Contains("Duplicate family host module", ex.Message);
    }

    [Fact]
    public void FamilyHostAssemblies_discovers_the_real_term_deposit_module_in_the_output_dir()
    {
        // Regression guard for the discovery anchor (bd babelstone-9w2k.5): once the host names no family
        // type in code, the C# compiler elides the family ProjectReference from the host's IL metadata, so
        // the compile-graph anchor alone discovers ZERO families. FamilyHostAssemblies() must therefore
        // ALSO probe the output directory for Babelstone.Families.*.dll — this test proves the real
        // term_deposit IFamilyHostModule is discovered WITHOUT a host boot (no Postgres), so a future change
        // that re-breaks discovery fails here in the fast lane, not only in the integration tests.
        var modules = new HostModuleLoader().LoadAll(HostModuleLoader.FamilyHostAssemblies());

        Assert.Contains(modules, m => m.FamilyName == "term_deposit");
        var termDeposit = modules.Single(m => m.FamilyName == "term_deposit");
        Assert.Equal("term_deposit@2026.1", termDeposit.SchemaVersion);
        Assert.Equal("term_deposit", termDeposit.AggregateType);

        // The personal_loan (credito_pessoal) family's host module is likewise discovered from the output
        // dir once the host carries its ProjectReference (bd babelstone-9g77) — proving the family is wired
        // into the running host with no per-family host edit beyond that reference (ADR-PC-031 §P5).
        Assert.Contains(modules, m => m.FamilyName == "personal_loan");
        var personalLoan = modules.Single(m => m.FamilyName == "personal_loan");
        Assert.Equal("personal_loan@2026.1", personalLoan.SchemaVersion);
        Assert.Equal("personal_loan", personalLoan.AggregateType);
    }

    [Fact]
    public void Throws_on_a_module_without_a_public_parameterless_constructor()
    {
        var fixture = typeof(global::Babelstone.HostModuleLoader.NoCtorFixture.NoCtorFamilyModule).Assembly;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new HostModuleLoader().LoadAll([fixture]));

        Assert.Contains("public parameterless constructor", ex.Message);
    }

    // ── Fail-closed family-manifest cross-check (bd babelstone-9w2k.3 / ADR-PC-007 §A1 / ADR-PC-009 §A1) ──
    // HOST_PACK_FAMILY_MANIFEST_CROSS_CHECK (commitment catalogue row 12c): the happy path + all four
    // fail-closed directions below ARE this commitment's test resolution — this anchor is what the
    // spec-coverage gate (ADR-PC-020 §P6) greps for to bind the Live catalogue row to its test.

    [Fact]
    public void Cross_check_passes_when_every_module_matches_a_pinned_family()
    {
        // The happy path: each discovered module's (family, aggregate_type, schema_version) tuple is
        // pinned in the manifest, and every pinned family has a module. No throw.
        var modules = new IFamilyHostModule[] { new AlphaHostModule(), new BetaHostModule() };
        var pack = PackWithFamilies(
            new PackFamily("alpha", "alpha", "alpha@2026.1", "Acme.Alpha"),
            new PackFamily("beta", "beta", "beta@2026.1", "Acme.Beta"));

        HostModuleLoader.CrossCheckAgainstPackManifest(modules, pack);
    }

    [Fact]
    public void Cross_check_fails_closed_on_a_module_the_pack_does_not_pin()
    {
        // A discovered family absent from the manifest — the host refuses to serve a family the pinned
        // pack does not declare (an unpinned family).
        var modules = new IFamilyHostModule[] { new AlphaHostModule(), new BetaHostModule() };
        var pack = PackWithFamilies(new PackFamily("alpha", "alpha", "alpha@2026.1", "Acme.Alpha"));

        var ex = Assert.Throws<PackLoadException>(() =>
            HostModuleLoader.CrossCheckAgainstPackManifest(modules, pack));
        Assert.Contains("beta", ex.Message);
        Assert.Contains("not declared in the pinned pack", ex.Message);
    }

    [Fact]
    public void Cross_check_fails_closed_on_a_schema_version_skew()
    {
        // The version-skew case the cross-check exists for: the loaded module composes a NEWER schema
        // version than the pinned pack declares. Stamping it onto an EventEnvelope would corrupt replay
        // (ADR-PC-009 §P1), so the host fails closed before serving its first command.
        var modules = new IFamilyHostModule[] { new AlphaHostModule() };
        var pack = PackWithFamilies(new PackFamily("alpha", "alpha", "alpha@2026.2", "Acme.Alpha"));

        var ex = Assert.Throws<PackLoadException>(() =>
            HostModuleLoader.CrossCheckAgainstPackManifest(modules, pack));
        Assert.Contains("schema-version skew", ex.Message);
        Assert.Contains("alpha@2026.1", ex.Message); // the loaded module's version
        Assert.Contains("alpha@2026.2", ex.Message); // the pinned version
    }

    [Fact]
    public void Cross_check_fails_closed_on_an_aggregate_type_skew()
    {
        var modules = new IFamilyHostModule[] { new AlphaHostModule() };
        var pack = PackWithFamilies(new PackFamily("alpha", "alpha_v2", "alpha@2026.1", "Acme.Alpha"));

        var ex = Assert.Throws<PackLoadException>(() =>
            HostModuleLoader.CrossCheckAgainstPackManifest(modules, pack));
        Assert.Contains("aggregate_type skew", ex.Message);
    }

    [Fact]
    public void Cross_check_fails_closed_on_a_pinned_family_with_no_loadable_module()
    {
        // A manifest pins a family the deployment shipped no assembly for — its saga would silently never
        // advance (no replay-safe recovery), so the host refuses to boot and names the missing assembly.
        var modules = new IFamilyHostModule[] { new AlphaHostModule() };
        var pack = PackWithFamilies(
            new PackFamily("alpha", "alpha", "alpha@2026.1", "Acme.Alpha"),
            new PackFamily("gamma", "gamma", "gamma@2026.1", "Acme.Gamma"));

        var ex = Assert.Throws<PackLoadException>(() =>
            HostModuleLoader.CrossCheckAgainstPackManifest(modules, pack));
        Assert.Contains("gamma", ex.Message);
        Assert.Contains("Acme.Gamma", ex.Message); // the missing plugin_assembly is named
        Assert.Contains("no matching IFamilyHostModule", ex.Message);
    }

    /// <summary>A minimal <see cref="VerifiedPack"/> carrying only the family-manifest the cross-check reads.</summary>
    private static VerifiedPack PackWithFamilies(params PackFamily[] families)
    {
        var manifest = new PackManifest(
            PackId: "pt",
            PackVersion: "2026.1",
            Namespace: "pt",
            ManifestSchemaVersion: 1,
            Publisher: "test@engine.internal",
            PackEffectiveFrom: new DateOnly(2026, 1, 1),
            BasedOnPackVersion: null,
            DeltaSummary: "test",
            BreakingChanges: [],
            EngineCompatibleVersions: ">=0.0.0",
            SchemaPins: new Dictionary<string, string>(),
            RateSheetRefNames: [],
            TemplateRefNames: [],
            TestCorpusRef: "stub");

        return new VerifiedPack(
            manifest,
            DayCounts: new Dictionary<string, PackDayCount>(),
            Withholdings: new Dictionary<string, PackWithholding>(),
            Fgds: new Dictionary<string, PackFgd>(),
            Reportings: new Dictionary<string, PackReporting>(),
            Parameters: new PackParameters(0, 14),
            RateSheetRefs: [],
            Families: families);
    }
}

// Happy-path discoverable doubles — public, concrete, public parameterless ctor — so the scan over this
// test assembly finds them. They register nothing; the tests assert only the DISCOVERY + ordering behaviour.

internal sealed class AlphaHostModule : IFamilyHostModule
{
    public string FamilyName => "alpha";
    public string SchemaVersion => "alpha@2026.1";
    public string AggregateType => "alpha";
    public void ConfigureServices(IServiceCollection services, FamilyHostContext ctx) { }
    public void MapEndpoints(IEndpointRouteBuilder app) { }
}

internal sealed class BetaHostModule : IFamilyHostModule
{
    public string FamilyName => "beta";
    public string SchemaVersion => "beta@2026.1";
    public string AggregateType => "beta";
    public void ConfigureServices(IServiceCollection services, FamilyHostContext ctx) { }
    public void MapEndpoints(IEndpointRouteBuilder app) { }
}
