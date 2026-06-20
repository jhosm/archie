using Babelstone.Engine.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Babelstone.HostModuleLoader.DuplicateFixture;

/// <summary>Two host modules claiming the same family — the duplicate-family collision case.</summary>
public sealed class DuplicateFamilyA : IFamilyHostModule
{
    public string FamilyName => "collision";
    public void ConfigureServices(IServiceCollection services, FamilyHostContext ctx) { }
    public void MapEndpoints(IEndpointRouteBuilder app) { }
}

/// <summary>The colliding twin of <see cref="DuplicateFamilyA"/>.</summary>
public sealed class DuplicateFamilyB : IFamilyHostModule
{
    public string FamilyName => "collision";
    public void ConfigureServices(IServiceCollection services, FamilyHostContext ctx) { }
    public void MapEndpoints(IEndpointRouteBuilder app) { }
}
