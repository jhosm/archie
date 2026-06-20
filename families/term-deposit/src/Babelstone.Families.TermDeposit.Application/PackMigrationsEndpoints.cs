using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Babelstone.Families.TermDeposit.Application;

// The operator pack-migration HTTP contract (ADR-PC-009 §P3, surface §3.6). snake_case on the wire
// (the host's JSON options). No PII (ADR-PC-004 §P2): version strings, a migration id, an operator
// actor reference, and structural instance ids only.

/// <summary>
/// An operator pack migration: re-pin a named instance set from one pack version to a newer one
/// (ADR-PC-009 §P3). The instance set is an EXPLICIT id list — the sound minimal filter; a predicate
/// <c>instance_filter</c> over the read model (surface §3.6) is a deferred follow-up.
/// </summary>
/// <param name="FromPackVersion">The pack version the matched instances are currently pinned to (e.g. <c>pt.2026.1</c>).</param>
/// <param name="ToPackVersion">The pack version to re-pin them to (e.g. <c>pt.2027.1</c>).</param>
/// <param name="InstanceIds">The explicit instances to consider; only those currently on <c>FromPackVersion</c> are migrated.</param>
/// <param name="MigrationId">The operator migration's id — the audit handle and the idempotency dedupe key (ADR-PC-009 §P3).</param>
/// <param name="OperatorActor">The operator/service actor initiating the migration (recorded on each event; never PII).</param>
/// <param name="Preview">When true, returns the matched set WITHOUT emitting any event (the pre-emission confirmation step).</param>
/// <param name="MigratedAt">Optional valid-time for the migration events; host-stamped from the wall clock when omitted.</param>
public sealed record PackMigrationRequest(
    string FromPackVersion,
    string ToPackVersion,
    IReadOnlyList<Guid> InstanceIds,
    string MigrationId,
    string OperatorActor,
    bool Preview = false,
    DateTimeOffset? MigratedAt = null);

/// <summary>
/// The migration outcome: which instances matched (and, when not a preview, were re-pinned). On a
/// preview the <paramref name="Migrated"/> flag is false and the set is the would-be-affected instances.
/// </summary>
/// <param name="MigrationId">Echoes the request's migration id (the audit handle).</param>
/// <param name="Migrated">True iff events were emitted (false for a preview).</param>
/// <param name="InstanceIds">The matched instances — re-pinned (when <paramref name="Migrated"/>) or previewed.</param>
public sealed record PackMigrationResponse(
    string MigrationId,
    bool Migrated,
    IReadOnlyList<Guid> InstanceIds);

/// <summary>
/// The operator pack-migration command surface (ADR-PC-009 §P3 / surface §3.6): <c>POST
/// /v1/pack-migrations</c>. In plain English — the only sanctioned way to move a live deposit to a
/// newer regulatory pack is this explicit, audited operator migration; this endpoint is how an operator
/// previews the affected instance set and then re-pins it. Distinct from adoption (which sets the pack
/// NEW constitutions pin — ADR-PC-009 §P4): there are no silent upgrades, and this endpoint never
/// touches the currently-active version for new constitutions.
/// </summary>
public static class PackMigrationsEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
        => app.MapPost("/v1/pack-migrations", MigrateAsync);

    private static async Task<IResult> MigrateAsync(
        PackMigrationRequest request,
        PackMigrationService service,
        TimeProvider clock,
        CancellationToken ct)
    {
        // Validate the operator's intent up front — fail loud on a malformed migration rather than
        // silently re-pin nothing or the wrong set.
        if (string.IsNullOrWhiteSpace(request.FromPackVersion)
            || string.IsNullOrWhiteSpace(request.ToPackVersion)
            || string.IsNullOrWhiteSpace(request.MigrationId)
            || string.IsNullOrWhiteSpace(request.OperatorActor))
        {
            return Results.Problem(
                "from_pack_version, to_pack_version, migration_id and operator_actor are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.Equals(request.FromPackVersion, request.ToPackVersion, StringComparison.Ordinal))
        {
            return Results.Problem(
                "from_pack_version and to_pack_version must differ — a migration moves the pin.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.InstanceIds is null || request.InstanceIds.Count == 0)
        {
            return Results.Problem(
                "instance_ids must name at least one instance to migrate.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Preview path (ADR-PC-009 Residual-risks: previewable before emission): the matched set, no
        // side effect. This is the operator's confirmation step before re-pinning.
        if (request.Preview)
        {
            var matched = await service.PreviewAsync(request.FromPackVersion, request.InstanceIds, ct);
            return Results.Ok(new PackMigrationResponse(request.MigrationId, Migrated: false, matched));
        }

        // Emit path: one PackVersionMigrated per matched instance, pinned to to_pack_version. The host
        // owns the wall clock at this boundary (ADR-PC-010 §P5) — it stamps a missing migrated_at; the
        // service threads it as the event valid-time. Idempotent on (migration_id, instance_id).
        var migratedAt = request.MigratedAt ?? clock.GetUtcNow();
        var migrated = await service.MigrateAsync(
            request.FromPackVersion,
            request.ToPackVersion,
            request.InstanceIds,
            request.MigrationId,
            request.OperatorActor,
            migratedAt,
            ct);

        return Results.Ok(new PackMigrationResponse(request.MigrationId, Migrated: true, migrated));
    }
}
