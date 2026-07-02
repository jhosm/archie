using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Babelstone.Engine.Hosting;

// The operator pack-migration HTTP contract (ADR-PC-009, surface §3.6). snake_case on the wire
// (the host's JSON options). No PII (ADR-PC-004): version strings, a migration id, an operator
// actor reference, a product-family selector, and structural instance ids only. Family-agnostic by
// construction — the contract names no family; the single route dispatches on product_family to the
// right family's registered IPackMigrationService / IPackMigrationInstanceResolver (ADR-PC-021).

/// <summary>
/// An operator pack migration: re-pin a target instance set from one pack version to a newer one
/// (ADR-PC-009). The target set is named ONE of two ways (exactly one — they are mutually
/// exclusive): an EXPLICIT <see cref="InstanceIds"/> list, or a predicate <see cref="InstanceFilter"/>
/// resolved over the family read model (surface §3.6).
/// </summary>
/// <param name="FromPackVersion">The pack version the matched instances are currently pinned to (e.g. <c>pt.2026.1</c>).</param>
/// <param name="ToPackVersion">The pack version to re-pin them to (e.g. <c>pt.2027.1</c>).</param>
/// <param name="MigrationId">The operator migration's id — the audit handle and the idempotency dedupe key (ADR-PC-009).</param>
/// <param name="OperatorActor">The operator/service actor initiating the migration (recorded on each event; never PII).</param>
/// <param name="ProductFamily">The target family for the EXPLICIT-ids arm (e.g. <c>term_deposit</c>). Optional when exactly one family is hosted (the unambiguous default); required to disambiguate a multi-family host. Ignored for the predicate arm, which carries its own <c>product_family</c> inside <see cref="InstanceFilter"/>.</param>
/// <param name="InstanceIds">The explicit instances to consider; only those currently on <c>FromPackVersion</c> are migrated. Mutually exclusive with <see cref="InstanceFilter"/>.</param>
/// <param name="InstanceFilter">The surface §3.6 predicate <c>{ product_family, currently_active }</c>; resolved to a concrete id set over the family read model. Mutually exclusive with <see cref="InstanceIds"/>.</param>
/// <param name="Preview">When true, returns the matched set WITHOUT emitting any event (the pre-emission confirmation step).</param>
/// <param name="MigratedAt">Optional valid-time for the migration events; host-stamped from the wall clock when omitted.</param>
public sealed record PackMigrationRequest(
    string FromPackVersion,
    string ToPackVersion,
    string MigrationId,
    string OperatorActor,
    string? ProductFamily = null,
    IReadOnlyList<Guid>? InstanceIds = null,
    InstanceFilter? InstanceFilter = null,
    bool Preview = false,
    DateTimeOffset? MigratedAt = null);

/// <summary>
/// The migration outcome: which instances matched (and, when not a preview, were re-pinned). On a
/// preview the <paramref name="Migrated"/> flag is false and the set is the would-be-affected instances.
/// For a predicate request, <paramref name="InstanceIds"/> echoes the RESOLVED-and-on-<c>from</c> set, so
/// the operator sees exactly which concrete instances the predicate selected (audit-completeness,
/// ADR-PC-009).
/// </summary>
/// <param name="MigrationId">Echoes the request's migration id (the audit handle).</param>
/// <param name="Migrated">True iff events were emitted (false for a preview).</param>
/// <param name="InstanceIds">The matched instances — re-pinned (when <paramref name="Migrated"/>) or previewed.</param>
public sealed record PackMigrationResponse(
    string MigrationId,
    bool Migrated,
    IReadOnlyList<Guid> InstanceIds);

/// <summary>
/// The resolved plan for a migration request: either a validation ERROR (an HTTP status + message) or a
/// PROCEED carrying the selected family write-path and the chosen target-selection mode (explicit ids, or
/// a resolver + filter). A pure value (no clock, no I/O), so <see cref="PackMigrationsEndpoints.Plan"/>
/// is unit-testable without the HTTP stack or a database.
/// </summary>
internal sealed record PackMigrationPlan(
    int? ErrorStatus,
    string? ErrorMessage,
    IPackMigrationService? Service = null,
    IReadOnlyList<Guid>? ExplicitInstanceIds = null,
    IPackMigrationInstanceResolver? Resolver = null,
    InstanceFilter? Filter = null)
{
    public bool Ok => ErrorStatus is null;

    public static PackMigrationPlan Error(int status, string message) => new(status, message);
}

/// <summary>
/// The operator pack-migration command surface (ADR-PC-009 / surface §3.6): <c>POST
/// /v1/pack-migrations</c>. In plain English — the only sanctioned way to move a live instance to a
/// newer regulatory pack is this explicit, audited operator migration; this endpoint is how an operator
/// previews the affected instance set (by an explicit id list OR a <c>{ product_family, currently_active }</c>
/// predicate) and then re-pins it. Distinct from adoption (which sets the pack NEW constitutions pin —
/// ADR-PC-009): there are no silent upgrades, and this endpoint never touches the currently-active
/// version for new constitutions.
/// </summary>
/// <remarks>
/// Family-agnostic (it lives in the hosting spine, ADR-PC-021): the HTTP contract, the validation,
/// and the route all name no family. The route is registered ONCE at host level (not per family — a
/// per-family registration would collide on the identical <c>/v1/pack-migrations</c> path); the handler
/// DISPATCHES on the request's <c>product_family</c> to the family's registered
/// <see cref="IPackMigrationService"/> and <see cref="IPackMigrationInstanceResolver"/>, which a family
/// supplies from its host module. The <c>family → Babelstone.Engine.Hosting → Babelstone.Engine</c> arrow
/// stays one-way — the spine never references a family back.
/// </remarks>
public static class PackMigrationsEndpoints
{
    /// <summary>
    /// Map <c>POST /v1/pack-migrations</c> ONCE at host level (family-agnostic). The handler resolves
    /// every registered <see cref="IPackMigrationService"/> / <see cref="IPackMigrationInstanceResolver"/>
    /// and dispatches by the request's <c>product_family</c>. A host calls this once (e.g. from
    /// <c>Program.cs</c>), NOT once per family — a per-family call would register the same route twice and
    /// throw <c>AmbiguousMatchException</c> the moment a second family is hosted.
    /// </summary>
    public static void Map(IEndpointRouteBuilder app)
        => app.MapPost("/v1/pack-migrations", MigrateAsync);

    /// <summary>
    /// Validate the request and SELECT the family write-path (and, for the predicate arm, the resolver) —
    /// a pure decision over the registered services/resolvers, no I/O. Returns an error plan (status +
    /// message) or a proceed plan. Split out from the async handler so the validation + dispatch rules
    /// are unit-testable with stub services/resolvers and no HTTP stack.
    /// </summary>
    internal static PackMigrationPlan Plan(
        PackMigrationRequest request,
        IReadOnlyCollection<IPackMigrationService> services,
        IReadOnlyCollection<IPackMigrationInstanceResolver> resolvers)
    {
        // Malformed intent — fail loud with 400 rather than silently re-pin nothing or the wrong set.
        if (string.IsNullOrWhiteSpace(request.FromPackVersion)
            || string.IsNullOrWhiteSpace(request.ToPackVersion)
            || string.IsNullOrWhiteSpace(request.MigrationId)
            || string.IsNullOrWhiteSpace(request.OperatorActor))
        {
            return PackMigrationPlan.Error(
                StatusCodes.Status400BadRequest,
                "from_pack_version, to_pack_version, migration_id and operator_actor are required.");
        }

        if (string.Equals(request.FromPackVersion, request.ToPackVersion, StringComparison.Ordinal))
        {
            return PackMigrationPlan.Error(
                StatusCodes.Status400BadRequest,
                "from_pack_version and to_pack_version must differ — a migration moves the pin.");
        }

        // Exactly one selection mode (explicit ids XOR predicate). Both or neither is unprocessable.
        var hasIds = request.InstanceIds is { Count: > 0 };
        var hasFilter = request.InstanceFilter is not null;
        if (hasIds == hasFilter)
        {
            return PackMigrationPlan.Error(
                StatusCodes.Status422UnprocessableEntity,
                "provide exactly one of instance_ids (non-empty) or instance_filter.");
        }

        return hasFilter
            ? PlanPredicate(request.InstanceFilter!, services, resolvers)
            : PlanExplicit(request.ProductFamily, request.InstanceIds!, services);
    }

    // The predicate arm (surface §3.6): the family comes from the filter; resolve over the family read model.
    private static PackMigrationPlan PlanPredicate(
        InstanceFilter filter,
        IReadOnlyCollection<IPackMigrationService> services,
        IReadOnlyCollection<IPackMigrationInstanceResolver> resolvers)
    {
        if (string.IsNullOrWhiteSpace(filter.ProductFamily))
        {
            return PackMigrationPlan.Error(
                StatusCodes.Status422UnprocessableEntity, "instance_filter.product_family is required.");
        }

        // v1 migrates only the LIVE population: re-pinning a terminal instance accrues no further events,
        // so its pin is moot. The complement is a deliberate future decision (a new ADR-PC), not a default.
        if (!filter.CurrentlyActive)
        {
            return PackMigrationPlan.Error(
                StatusCodes.Status422UnprocessableEntity,
                "instance_filter.currently_active must be true — v1 migrates only the live population.");
        }

        var service = SelectByFamily(services, filter.ProductFamily);
        if (service is null)
        {
            return PackMigrationPlan.Error(
                StatusCodes.Status422UnprocessableEntity,
                $"no pack-migration write-path is registered for product_family '{filter.ProductFamily}'.");
        }

        var resolver = resolvers.FirstOrDefault(
            r => string.Equals(r.ProductFamily, filter.ProductFamily, StringComparison.Ordinal));
        if (resolver is null)
        {
            return PackMigrationPlan.Error(
                StatusCodes.Status422UnprocessableEntity,
                $"instance_filter is not supported for product_family '{filter.ProductFamily}'.");
        }

        return new PackMigrationPlan(null, null, Service: service, Resolver: resolver, Filter: filter);
    }

    // The explicit-ids arm: the family is the top-level product_family, optional when unambiguous (one host family).
    private static PackMigrationPlan PlanExplicit(
        string? productFamily,
        IReadOnlyList<Guid> instanceIds,
        IReadOnlyCollection<IPackMigrationService> services)
    {
        IPackMigrationService? service;
        if (!string.IsNullOrWhiteSpace(productFamily))
        {
            service = SelectByFamily(services, productFamily);
            if (service is null)
            {
                return PackMigrationPlan.Error(
                    StatusCodes.Status422UnprocessableEntity,
                    $"no pack-migration write-path is registered for product_family '{productFamily}'.");
            }
        }
        else if (services.Count == 1)
        {
            // Single-family host: product_family is unambiguous, so it may be omitted on the explicit arm.
            service = services.First();
        }
        else
        {
            return PackMigrationPlan.Error(
                StatusCodes.Status422UnprocessableEntity,
                "product_family is required to disambiguate explicit instance_ids across multiple families.");
        }

        return new PackMigrationPlan(null, null, Service: service, ExplicitInstanceIds: instanceIds);
    }

    private static IPackMigrationService? SelectByFamily(
        IReadOnlyCollection<IPackMigrationService> services, string productFamily)
        => services.FirstOrDefault(s => string.Equals(s.ProductFamily, productFamily, StringComparison.Ordinal));

    private static async Task<IResult> MigrateAsync(
        PackMigrationRequest request,
        IEnumerable<IPackMigrationService> services,
        IEnumerable<IPackMigrationInstanceResolver> resolvers,
        TimeProvider clock,
        CancellationToken ct)
    {
        var plan = Plan(request, AsCollection(services), AsCollection(resolvers));
        if (!plan.Ok)
        {
            return Results.Problem(plan.ErrorMessage, statusCode: plan.ErrorStatus);
        }

        var service = plan.Service!;

        // The predicate WIDENS to the live population (resolved over the read model); the explicit arm
        // supplies the ids directly. Either way the SAME id list flows into the unchanged preview/migrate
        // loop, so the head-pin guard + (migration_id, instance_id) idempotency are preserved verbatim.
        var instanceIds = plan.ExplicitInstanceIds ?? await plan.Resolver!.ResolveAsync(plan.Filter!, ct);

        // Preview path (ADR-PC-009 Residual-risks: previewable before emission): the matched set, no side
        // effect. The predicate widens, the per-head from_pack_version check narrows — so the operator
        // sees exactly which concrete instances would be re-pinned.
        if (request.Preview)
        {
            var matched = await service.PreviewAsync(request.FromPackVersion, instanceIds, ct);
            return Results.Ok(new PackMigrationResponse(request.MigrationId, Migrated: false, matched));
        }

        // Emit path: one PackVersionMigrated per matched instance, pinned to to_pack_version. The host
        // owns the wall clock at this boundary (ADR-PC-010) — it stamps a missing migrated_at; the
        // service threads it as the event valid-time. Idempotent on (migration_id, instance_id).
        var migratedAt = request.MigratedAt ?? clock.GetUtcNow();
        var migrated = await service.MigrateAsync(
            request.FromPackVersion,
            request.ToPackVersion,
            instanceIds,
            request.MigrationId,
            request.OperatorActor,
            migratedAt,
            ct);

        return Results.Ok(new PackMigrationResponse(request.MigrationId, Migrated: true, migrated));
    }

    private static IReadOnlyCollection<T> AsCollection<T>(IEnumerable<T> source)
        => source as IReadOnlyCollection<T> ?? source.ToArray();
}
