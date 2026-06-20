using Babelstone.Engine.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Babelstone.HostModuleLoader.NoCtorFixture;

/// <summary>
/// A host module WITHOUT a public parameterless constructor — the loader must fail loud at the discovery
/// seam (a diagnosable error naming the module) rather than failing deep inside <c>Activator</c>.
/// </summary>
public sealed class NoCtorFamilyModule : IFamilyHostModule
{
    public NoCtorFamilyModule(string _) { }
    public string FamilyName => "needs_ctor_arg";
    public void ConfigureServices(IServiceCollection services, FamilyHostContext ctx) { }
    public void MapEndpoints(IEndpointRouteBuilder app) { }
}
