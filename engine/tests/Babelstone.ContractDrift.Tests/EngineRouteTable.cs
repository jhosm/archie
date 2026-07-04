using System.Reflection;
using System.Text;
using Babelstone.Engine.Api;
using Babelstone.Engine.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Babelstone.ContractDrift.Tests;

/// <summary>
/// The engine estate's HTTP route table, composed HERMETICALLY — no server, no sockets, no
/// database, no containers — for the Layer-4 sweep (<see cref="Layer4MappedEndpointSpecCoverageTests"/>).
/// It replays the hosts' own endpoint-mapping composition against a collecting
/// <see cref="IEndpointRouteBuilder"/> and enumerates the built <see cref="RouteEndpoint"/>s into
/// (HTTP method, normalized route template) pairs.
/// </summary>
/// <remarks>
/// <para>
/// Three composition sources, mirroring where the real hosts register routes:
/// </para>
/// <list type="number">
///   <item><b>Family modules — the REAL discovery seam.</b> The same
///   <c>HostModuleLoader.LoadAll(FamilyHostAssemblies())</c> call the engine host makes
///   (<c>Babelstone.Engine.Api/Program.cs</c>), then each module's real <c>MapEndpoints</c>. A new
///   family (or a new route on an existing family) is swept automatically with no edit here —
///   the family surface is where route churn happens, so it is exactly the part that must not
///   be a hand-maintained list.</item>
///   <item><b>Host-level spine surfaces — convention scan.</b> Every public static class in
///   <c>Babelstone.Engine.Hosting</c> exposing <c>public static void Map(IEndpointRouteBuilder)</c>
///   (today: <see cref="PackMigrationsEndpoints"/>). The engine host registers these once at host
///   level (family-agnostic, ADR-PC-021); scanning the hosting assembly by the same signature
///   convention means a future host-level surface added there is swept automatically. The scan can
///   over-approximate (a Map the host stopped calling is still swept) — the conservative direction:
///   a documented-but-unmapped route never fails this sweep, an undocumented mapped one does.</item>
///   <item><b>The standalone rate-sheets host — a literal mirror.</b>
///   <c>Babelstone.RateSheets.Api/Program.cs</c> maps its single route inline in top-level
///   statements, which cannot be invoked without booting that host (pack preload from disk, OTel
///   wiring), so the one mapping is mirrored here verbatim. If that host gains a route, this
///   mirror must gain the same line — the accepted residue of keeping the suite hermetic.</item>
/// </list>
/// <para>
/// <b>Why the blanket <see cref="IServiceProviderIsService"/>.</b> Minimal-API endpoint building
/// (<c>RequestDelegateFactory</c>) classifies each unannotated complex handler parameter as either
/// a DI service or THE request body by asking <see cref="IServiceProviderIsService"/> — and a
/// handler with two "body" parameters fails to build. Registering the hosts' real services would
/// drag in Postgres/pack/clock wiring; instead every complex type is declared a service. That is
/// sound HERE because no request is ever dispatched: the sweep reads only the built route pattern
/// and method metadata, never the binding behaviour (Layers 1–3 assert the wire shapes).
/// </para>
/// </remarks>
internal static class EngineRouteTable
{
    // Lazy so a composition failure surfaces as a clean per-test exception (with this message
    // chain), not an opaque type-initializer error.
    private static readonly Lazy<IReadOnlyList<MappedRoute>> Cached = new(Compose);

    /// <summary>The composed engine route table: distinct (METHOD, template) pairs, stably ordered.</summary>
    public static IReadOnlyList<MappedRoute> Routes => Cached.Value;

    private static IReadOnlyList<MappedRoute> Compose()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouting();
        // The money-mover route groups attach AddEndpointFilter<ScaPreconditionFilter>, whose factory
        // constructs the filter (ctor: TimeProvider) at endpoint-BUILD time — i.e. when this sweep
        // materializes .Endpoints. Register a clock so that construction succeeds; the filter is never
        // invoked (no request is dispatched), so any wall clock is inert here.
        services.AddSingleton(TimeProvider.System);
        using var provider = services.BuildServiceProvider();

        // RequestDelegateFactory classifies each complex handler parameter as EITHER a DI service or THE
        // request body by consulting IServiceProviderIsService — and the container's OWN built-in
        // implementation (keyed off real registrations) WINS over one registered in the ServiceCollection,
        // so the handlers' unregistered services (PersonalLoanConstitutionService, AggregateRuntime<T>, …)
        // would classify as UNKNOWN and fail the endpoint build. Wrapping the provider so the classifier
        // lookup returns EveryTypeIsAService makes every complex parameter a service — sound because the
        // sweep only READS the built route pattern, never dispatches a request (the wire shapes are Layers 1–3).
        var classifyingProvider = new ClassifierOverrideProvider(provider, new EveryTypeIsAService());

        var builder = new CollectingRouteBuilder(classifyingProvider);

        // (1) The family surfaces, through the host's own discovery seam — never a hand list.
        var modules = new HostModuleLoader().LoadAll(HostModuleLoader.FamilyHostAssemblies());
        if (modules.Count == 0)
        {
            throw new InvalidOperationException(
                "HostModuleLoader discovered no family host modules from the test output directory — "
                + "the Layer-4 sweep would be vacuous. The Babelstone.Engine.Api ProjectReference must "
                + "copy the Babelstone.Families.*.dll assemblies next to this test assembly (the same "
                + "output-dir probe the real host uses, ADR-PC-021 §A3 Option B).");
        }

        foreach (var module in modules)
        {
            module.MapEndpoints(builder);
        }

        // (2) The host-level spine surfaces, by signature convention over Babelstone.Engine.Hosting.
        foreach (var map in HostLevelMapMethods())
        {
            map.Invoke(null, [builder]);
        }

        // (3) The standalone rate-sheets host's single route — a verbatim mirror of
        // engine/src/Babelstone.RateSheets.Api/Program.cs (`app.MapPost("/v1/rate-sheets", ...)`),
        // because that host maps inline in top-level statements and cannot be composed without
        // booting it. The handler is a stand-in: only (method, template) is swept.
        builder.MapPost("/v1/rate-sheets", () => Results.Empty);

        var routes = new List<MappedRoute>();
        foreach (var endpoint in builder.DataSources.SelectMany(ds => ds.Endpoints).OfType<RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods
                ?? throw new InvalidOperationException(
                    $"mapped endpoint '{endpoint.DisplayName}' declares no HTTP method metadata — "
                    + "the sweep cannot key it against a spec operation.");

            foreach (var method in methods)
            {
                routes.Add(new MappedRoute(method.ToUpperInvariant(), Template(endpoint.RoutePattern)));
            }
        }

        return routes
            .Distinct()
            .OrderBy(r => r.Route, StringComparer.Ordinal)
            .ThenBy(r => r.Method, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The host-level endpoint registrations the engine host makes once, at host level (not per
    /// family): every <c>public static void Map(IEndpointRouteBuilder)</c> on a public static class
    /// in <c>Babelstone.Engine.Hosting</c>. Fails loud if the convention scan comes back empty —
    /// PackMigrationsEndpoints.Map is known to exist, so an empty result means the scan (not the
    /// surface) broke.
    /// </summary>
    private static IReadOnlyList<MethodInfo> HostLevelMapMethods()
    {
        var maps = typeof(PackMigrationsEndpoints).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: true, IsSealed: true, IsPublic: true })
            .Select(t => t.GetMethod(
                "Map", BindingFlags.Public | BindingFlags.Static, [typeof(IEndpointRouteBuilder)]))
            .Where(m => m is not null && m.ReturnType == typeof(void))
            .Select(m => m!)
            .OrderBy(m => m.DeclaringType!.FullName, StringComparer.Ordinal)
            .ToArray();

        if (!maps.Any(m => m.DeclaringType == typeof(PackMigrationsEndpoints)))
        {
            throw new InvalidOperationException(
                "the host-level endpoint scan over Babelstone.Engine.Hosting no longer finds "
                + "PackMigrationsEndpoints.Map(IEndpointRouteBuilder) — the Map signature convention "
                + "this sweep relies on has drifted; update EngineRouteTable.HostLevelMapMethods.");
        }

        return maps;
    }

    /// <summary>
    /// The route pattern normalized to the OpenAPI path-template shape: literals verbatim, each
    /// parameter as <c>{name}</c> with route constraints stripped (<c>{id:guid}</c> → <c>{id}</c>).
    /// Parameter NAMES are kept and must match the spec's — the committed specs and the mapped
    /// routes deliberately share names (<c>{id}</c>), and a rename is a doc-visible change that
    /// should show up here rather than be silently tolerated.
    /// </summary>
    private static string Template(RoutePattern pattern)
    {
        var template = new StringBuilder();
        foreach (var segment in pattern.PathSegments)
        {
            template.Append('/');
            foreach (var part in segment.Parts)
            {
                template.Append(part switch
                {
                    RoutePatternLiteralPart literal => literal.Content,
                    RoutePatternParameterPart parameter => $"{{{parameter.Name}}}",
                    RoutePatternSeparatorPart separator => separator.Content,
                    _ => throw new InvalidOperationException(
                        $"route '{pattern.RawText}' uses an unrecognised pattern part "
                        + $"({part.GetType().Name}) the Layer-4 normalizer does not handle."),
                });
            }
        }

        return template.Length == 0 ? "/" : template.ToString();
    }

    // See the class remarks: every complex handler parameter is classified as a DI service so the
    // hosts' real MapEndpoints build without the hosts' real service registrations. Sound because
    // the sweep never dispatches a request.
    private sealed class EveryTypeIsAService : IServiceProviderIsService
    {
        public bool IsService(Type serviceType) => true;
    }

    /// <summary>
    /// Delegates every resolution to the real provider EXCEPT the <see cref="IServiceProviderIsService"/>
    /// lookup, which returns the supplied classifier — the only way to make RequestDelegateFactory use our
    /// "everything is a service" classification, since the container's built-in classifier (keyed off real
    /// registrations) otherwise wins over a ServiceCollection registration.
    /// </summary>
    private sealed class ClassifierOverrideProvider(
        IServiceProvider inner, IServiceProviderIsService classifier) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IServiceProviderIsService) ? classifier : inner.GetService(serviceType);
    }

    /// <summary>
    /// The minimal <see cref="IEndpointRouteBuilder"/> a MapEndpoints call needs: a data-source
    /// collection to collect into, and a service provider for endpoint building. Deliberately not a
    /// <c>WebApplication</c> — no host, no configuration load, no lifetime.
    /// </summary>
    private sealed class CollectingRouteBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;

        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }
}

/// <summary>One mapped route: the upper-cased HTTP method and the OpenAPI-shaped path template.</summary>
public sealed record MappedRoute(string Method, string Route);
