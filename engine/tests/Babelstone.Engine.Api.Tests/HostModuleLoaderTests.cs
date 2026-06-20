using System.Reflection;
using Babelstone.Engine.Api;
using Babelstone.Engine.Hosting;
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
    public void Throws_on_a_module_without_a_public_parameterless_constructor()
    {
        var fixture = typeof(global::Babelstone.HostModuleLoader.NoCtorFixture.NoCtorFamilyModule).Assembly;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new HostModuleLoader().LoadAll([fixture]));

        Assert.Contains("public parameterless constructor", ex.Message);
    }
}

// Happy-path discoverable doubles — public, concrete, public parameterless ctor — so the scan over this
// test assembly finds them. They register nothing; the tests assert only the DISCOVERY + ordering behaviour.

internal sealed class AlphaHostModule : IFamilyHostModule
{
    public string FamilyName => "alpha";
    public void ConfigureServices(IServiceCollection services, FamilyHostContext ctx) { }
    public void MapEndpoints(IEndpointRouteBuilder app) { }
}

internal sealed class BetaHostModule : IFamilyHostModule
{
    public string FamilyName => "beta";
    public void ConfigureServices(IServiceCollection services, FamilyHostContext ctx) { }
    public void MapEndpoints(IEndpointRouteBuilder app) { }
}
