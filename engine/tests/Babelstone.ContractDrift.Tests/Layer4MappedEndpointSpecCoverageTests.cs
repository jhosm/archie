using Xunit;

namespace Babelstone.ContractDrift.Tests;

/// <summary>
/// Layer 4 — the ROUTE-side drift sweep: every HTTP endpoint the engine estate actually MAPS must
/// appear in some catalogued OpenAPI spec (public or internal), or sit on this file's explicit,
/// documented allowlist. This closes the structural blind side PR #443 finding 4 surfaced — two
/// shipped engine reads (<c>/v1/deposits/withholding-statements</c>, <c>/v1/deposits/{id}/withholding-ledger</c>)
/// went UNCATALOGUED for weeks because Layers 1–3 only walk spec→code (and the meta-sweep only walks
/// spec→drift-case); nothing walked code→spec, so a mapped-but-undocumented route was invisible.
/// This sweep walks that missing direction: mapped-routes ⊆ (catalogued ∪ allowlist).
/// </summary>
/// <remarks>
/// <para>
/// <b>Hermetic (ADR-IC-020 §4 "the gate is hermetic and plane-separate").</b> The mapped-route table
/// is composed by replaying the hosts' own <c>MapEndpoints</c> against a collecting route builder —
/// no server, no sockets, no Postgres, no containers (see <see cref="EngineRouteTable"/>). The
/// catalogued surface is read from the committed <c>*.openapi.yaml</c> TEXT, the same source Layers
/// 1–3 and the governance gate (<c>scripts/openapi-catalog-validate.sh</c>) read. So all four sweeps
/// run in the Docker-free unit lane together.
/// </para>
/// <para>
/// <b>Auto-discovers a parallel-added spec.</b> The catalogued surface is GLOBBED from
/// <c>contracts/openapi/specs</c> and <c>contracts/openapi/internal</c>, so a spec file a concurrent
/// lane adds to either directory (e.g. <c>internal/engine-bulk-operations.openapi.yaml</c>) is picked
/// up with no edit here — its documented routes immediately satisfy any matching mapped routes.
/// </para>
/// <para>
/// <b>Direction and scope.</b> This is a SUBSET assertion (mapped ⊆ catalogued ∪ allowlist), not
/// equality: a spec may document a route no host maps in THIS composition (the orchestrator/SoR specs
/// live under <c>/api/v1</c> on other hosts; the engine composes only <c>/v1</c> routes). A documented
/// route with no mapping is Layer 1/meta-sweep's concern, not this one. The one direction this sweep
/// owns — a MAPPED route with NO documentation — is the finding it must never silently swallow.
/// </para>
/// </remarks>
public sealed class Layer4MappedEndpointSpecCoverageTests
{
    /// <summary>
    /// The explicit, documented allowlist of mapped (METHOD, route) pairs that are DELIBERATELY not (yet)
    /// in any catalogued OpenAPI spec. An entry is a KNOWN, REVIEWED divergence — never a silent one:
    /// it names the route, the reason, and its tracking follow-up, exactly the "divergence is allowed;
    /// silent divergence is not" discipline the estate's drift gates enforce (the ADR-PC-020 §D3 spirit,
    /// applied to the REST catalogue). Retiring an entry (by authoring the spec) is the intended endgame;
    /// adding one must carry a reason in the same change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Currently empty — the intended steady state.</b> The allowlist most recently held the
    /// <c>personal_loan</c> (credito_pessoal) family's six mapped routes
    /// (<c>families/personal-loan/.../LoansEndpoints.cs</c>), which had shipped ahead of their OpenAPI
    /// contract. That gap is now CLOSED (bd babelstone-ax0b.10): the surface is catalogued in
    /// <c>contracts/openapi/internal/engine-loan-commands.openapi.yaml</c> and
    /// <c>engine-loan-reads.openapi.yaml</c> (each operation with its Layer-1 DTO drift case and its
    /// meta-sweep entry), so the code→spec sweep re-covers those routes for real and the entries were
    /// DELETED in the same change. An empty allowlist means every mapped engine route is genuinely
    /// catalogued — the state this sweep exists to hold. A future entry must name its route, reason, and
    /// tracking follow-up, exactly the "divergence is allowed; silent divergence is not" discipline above.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlySet<MappedRoute> Allowlist = new HashSet<MappedRoute>();

    [Fact]
    public void Every_mapped_engine_endpoint_is_catalogued_or_explicitly_allowlisted()
    {
        var catalogued = CataloguedRoutes();

        var uncatalogued = EngineRouteTable.Routes
            .Where(route => !catalogued.Contains(route) && !Allowlist.Contains(route))
            .OrderBy(r => r.Route, StringComparer.Ordinal)
            .ThenBy(r => r.Method, StringComparer.Ordinal)
            .ToArray();

        Assert.True(uncatalogued.Length == 0,
            "these engine endpoints are MAPPED by a host but appear in no catalogued OpenAPI spec and "
            + "are not on the Layer-4 allowlist — a shipped-but-undocumented route (PR #443 finding 4): ["
            + string.Join(", ", uncatalogued.Select(r => $"{r.Method} {r.Route}"))
            + "]. Either document the route in the right spec under contracts/openapi/ (conservatively — "
            + "it also needs a Layer-1 drift case) or add it to the Allowlist with a reason.");
    }

    [Fact]
    public void The_allowlist_carries_no_route_that_is_actually_catalogued()
    {
        // Housekeeping the other way: once a route IS documented, its allowlist entry is dead weight and
        // must be removed — an allowlist that outlives its reason is the silent-exemption failure mode.
        var catalogued = CataloguedRoutes();
        var stale = Allowlist.Where(catalogued.Contains).ToArray();

        Assert.True(stale.Length == 0,
            "these routes are on the Layer-4 allowlist but are NOW catalogued in an OpenAPI spec — delete "
            + "their allowlist entries so the exemption cannot outlive its reason: ["
            + string.Join(", ", stale.Select(r => $"{r.Method} {r.Route}")) + "].");
    }

    [Fact]
    public void The_allowlist_carries_no_route_that_is_no_longer_mapped()
    {
        // And the third way: an allowlist entry for a route no host maps any more is also dead weight
        // (the route was removed but its exemption lingered). Keep the allowlist honest in every direction.
        var mapped = EngineRouteTable.Routes.ToHashSet();
        var orphaned = Allowlist.Where(r => !mapped.Contains(r)).ToArray();

        Assert.True(orphaned.Length == 0,
            "these routes are on the Layer-4 allowlist but are no longer mapped by any host — delete their "
            + "allowlist entries: [" + string.Join(", ", orphaned.Select(r => $"{r.Method} {r.Route}")) + "].");
    }

    /// <summary>
    /// The union of every (METHOD, path) operation across every catalogued spec, GLOBBED from the two
    /// catalogue directories so a parallel-added spec is discovered with no edit. Paths are used as the
    /// specs write them — the OpenAPI <c>{name}</c> template shape <see cref="EngineRouteTable"/> normalizes
    /// mapped route patterns into, so <c>/v1/deposits/{id}</c> matches <c>{id:guid}</c>.
    /// </summary>
    private static IReadOnlySet<MappedRoute> CataloguedRoutes()
    {
        var routes = new HashSet<MappedRoute>();
        foreach (var dir in new[] { "specs", "internal" })
        {
            var abs = Path.Combine(TestRepo.Root, "contracts", "openapi", dir);
            if (!Directory.Exists(abs))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(abs, "*.openapi.yaml"))
            {
                var spec = OpenApiSpec.Load($"contracts/openapi/{dir}/{Path.GetFileName(file)}");
                foreach (var operation in spec.Operations())
                {
                    routes.Add(new MappedRoute(operation.Method, operation.Path));
                }
            }
        }

        return routes;
    }
}
