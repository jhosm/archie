using System.Text.Json.Nodes;
using Babelstone.RateSheets;
using Babelstone.Telemetry;
using Microsoft.Extensions.Logging;

namespace Babelstone.ProductConfigs.Api;

/// <summary>
/// The <c>POST /v1/product-configs</c> handler (ADR-PC-009 §A2, ADR-PC-008): the versioned
/// product-config deploy registry — the v2 successor to the interim content-hash pin (bd
/// babelstone-fk7m.9). It applies the idempotency rule keyed on <c>product_config_version_id</c> exactly
/// as the rate-sheet deploy applies it to <c>rate_sheet_version_id</c>: a new version is created (201),
/// an identical re-POST is replayed (200), and a different body under an existing version id is rejected
/// (409), enforcing forward-only immutability (ADR-PC-008 §P5) at the API boundary as well as the table.
///
/// Observability (ADR-IC-007 Layer 1): a 409 conflict and any unexpected exception leave a structured
/// server-side record under a stable <see cref="BabelstoneEvents"/> id, carrying the deploy context
/// (version id, product, effective_from, deploy actor) — none of it PII (a product-config deploy carries
/// no depositor data, ADR-PC-004). The OTel logging integration stamps trace_id/span_id, so the record
/// correlates to its trace. Mirrors <c>DeployRateSheetEndpoint</c>.
/// </summary>
internal static class DeployProductConfigEndpoint
{
    /// <summary>The structured-log category — the ILogger&lt;T&gt; default name for this handler.</summary>
    private const string LogCategory = "Babelstone.ProductConfigs.Api.DeployProductConfigEndpoint";

    public static async Task<IResult> HandleAsync(
        ProductConfigDeployRequest request,
        HttpRequest http,
        IProductConfigVersionStore store,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        // A static handler cannot be an ILogger<T> category, so name the category after it explicitly —
        // a stable log scope an operator filters on, matching the ILogger<T> default category name.
        var logger = loggerFactory.CreateLogger(LogCategory);

        // Envelope guard FIRST: System.Text.Json binds a missing member to null/default despite the
        // record's non-nullable declarations, so enforce that here as a clean 400 naming each offending
        // field, never a downstream NullReferenceException surfacing as an opaque 500. Runs before the
        // Idempotency-Key comparison because comparing against a null version id is meaningless.
        if (ValidateEnvelope(request) is { } invalidEnvelope)
        {
            return invalidEnvelope;
        }

        // ADR-PC-008: an Idempotency-Key header, when supplied, must equal the version id — the version
        // id IS the natural idempotency key, so no separate header is needed.
        if (http.Headers.TryGetValue("Idempotency-Key", out var key)
            && !string.IsNullOrEmpty(key)
            && !string.Equals(key.ToString(), request.ProductConfigVersionId, StringComparison.Ordinal))
        {
            return Results.Problem(
                detail: "The Idempotency-Key header, when supplied, must equal product_config_version_id.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // ADR-PC-008 Amendment A3: the deploying principal is the gateway-authenticated identity, not a
        // payload field a caller can spoof. It is recorded as published_by; the approver and sign-off
        // reference travel in the body and are recorded as approved_by / approval_ref.
        if (!http.Headers.TryGetValue("X-Deploy-Actor", out var actor) || string.IsNullOrWhiteSpace(actor))
        {
            return Results.Problem(
                detail: "The X-Deploy-Actor header (the gateway-authenticated deploying principal) is required.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var version = new ProductConfigVersion(
            request.ProductConfigVersionId,
            request.ProductId,
            request.PackVersion,
            // Truncate to PostgreSQL's microsecond resolution at the boundary so the value we store and
            // the value we compare on a re-POST share a precision — the same normalisation the rate-sheet
            // deploy applies, so an identical re-POST replays as 200, never a spurious 409.
            ToMicroseconds(request.EffectiveFrom),
            request.Body,
            // The registry mints the content hash server-side from the canonical body — the bridge to the
            // interim content-hash pin (bd babelstone-fk7m.9). A caller neither supplies nor spoofs it.
            ProductConfigJson.ContentHash(request.Body),
            request.ApprovedBy,
            request.ApprovalRef,
            PublishedBy: actor.ToString());

        // From here on a write is in play. An unexpected failure (a dropped DB, a serialization fault,
        // the read-back invariant below) is logged with the deploy context under a stable id BEFORE
        // UseExceptionHandler maps it to a 500 — the generic handler has only the bare exception, never
        // the version id / product / actor an operator needs (ADR-IC-007 Layer 1).
        try
        {
            // Happy idempotency path: a prior version under this id short-circuits before any write.
            var existing = await store.TryGetAsync(version.ProductConfigVersionId, ct);
            if (existing is not null)
            {
                return Idempotent(existing, version, logger);
            }

            try
            {
                await store.InsertAsync(version, ct);
            }
            catch (DuplicateProductConfigVersionException)
            {
                // Race: a concurrent deploy committed first under the same version id, or claimed this
                // product's effective_from. Re-read and apply the same ADR-PC-008 rule.
                var raced = await store.TryGetAsync(version.ProductConfigVersionId, ct);
                return raced is null
                    ? Conflict(version, logger,
                        "effective_from is already claimed by a different product_config_version_id.")
                    : Idempotent(raced, version, logger);
            }

            // Re-read so the response carries the database-assigned published_at. A just-committed row
            // that cannot be read back is an invariant violation (not a routine empty result), so fail
            // loud rather than silently returning the in-memory version with a null published_at.
            var stored = await store.TryGetAsync(version.ProductConfigVersionId, ct)
                ?? throw new InvalidOperationException(
                    $"Product config '{version.ProductConfigVersionId}' was inserted but could not be read back.");
            return Results.Created(
                $"/v1/product-configs/{version.ProductConfigVersionId}", ProductConfigDeployResponse.From(stored));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client aborted the request — a routine cancellation, not an unexpected deploy fault.
            // Rethrow so the pipeline maps it, keeping the Error log reserved for genuine faults.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                BabelstoneEvents.ProductConfigDeployUnexpectedError, ex,
                "Unexpected error deploying product config {ProductConfigVersionId} (product {ProductId}, "
                + "effective_from {EffectiveFrom:O}, actor {DeployActor}); returning 500.",
                version.ProductConfigVersionId, version.ProductId, version.EffectiveFrom, version.PublishedBy);
            throw;
        }
    }

    /// <summary>
    /// The deploy envelope's required-field guard: every envelope field the stored row needs must be
    /// present and non-blank (and <c>body</c> non-null and non-empty, <c>effective_from</c> non-default
    /// — the value a missing member binds to). Returns the 400 validation problem naming every offending
    /// field at once, or null when the envelope is well-formed. A pure function (no I/O, no clock) so the
    /// guard is unit-testable without the HTTP stack.
    /// </summary>
    internal static IResult? ValidateEnvelope(ProductConfigDeployRequest request)
    {
        var missing = new Dictionary<string, string[]>();
        AddIfBlank(missing, "product_config_version_id", request.ProductConfigVersionId);
        AddIfBlank(missing, "product_id", request.ProductId);
        AddIfBlank(missing, "pack_version", request.PackVersion);
        AddIfBlank(missing, "approved_by", request.ApprovedBy);
        AddIfBlank(missing, "approval_ref", request.ApprovalRef);
        if (request.EffectiveFrom == default)
        {
            missing["effective_from"] = ["effective_from is required (a missing member binds to the default instant)."];
        }

        if (request.Body is null || request.Body.Count == 0)
        {
            missing["body"] = ["body is required — the structural product-config the version defines (ADR-PC-009 §A2)."];
        }

        // TypedResults (not Results) so the guard's outcome is the concrete ValidationProblem type — the
        // unit tests assert on it directly; the wire shape is identical.
        return missing.Count > 0 ? TypedResults.ValidationProblem(missing) : null;
    }

    private static void AddIfBlank(Dictionary<string, string[]> missing, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            missing[field] = [$"{field} is required and must be non-blank."];
        }
    }

    // PostgreSQL TIMESTAMPTZ resolves to microseconds; .NET DateTimeOffset to 100ns ticks. Normalise at
    // the boundary (10 ticks = 1 microsecond) so stored and compared values match.
    private static DateTimeOffset ToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMicrosecond), value.Offset);

    private static IResult Idempotent(
        ProductConfigVersion existing, ProductConfigVersion incoming, ILogger logger)
    {
        var identical =
            string.Equals(existing.ProductId, incoming.ProductId, StringComparison.Ordinal)
            && string.Equals(existing.PackVersion, incoming.PackVersion, StringComparison.Ordinal)
            && existing.EffectiveFrom == incoming.EffectiveFrom
            && string.Equals(existing.ApprovedBy, incoming.ApprovedBy, StringComparison.Ordinal)
            && string.Equals(existing.ApprovalRef, incoming.ApprovalRef, StringComparison.Ordinal)
            && string.Equals(
                ProductConfigJson.Canonical(existing.Body),
                ProductConfigJson.Canonical(incoming.Body),
                StringComparison.Ordinal);

        return identical
            ? Results.Ok(ProductConfigDeployResponse.From(existing))
            : Conflict(incoming, logger,
                $"product_config_version_id '{incoming.ProductConfigVersionId}' already exists with a different definition; "
                + "corrections ship forward-only as a new version (ADR-PC-008 §P5).");
    }

    // A 409, recorded server-side under a stable id (ADR-IC-007 Layer 1) with the deploy context — a
    // forward-only-immutability breach (ADR-PC-008) the operator should see, not just a bare HTTP 409.
    // DeployActor is the version's PublishedBy, i.e. the gateway-authenticated X-Deploy-Actor header.
    private static IResult Conflict(ProductConfigVersion incoming, ILogger logger, string detail)
    {
        logger.LogWarning(
            BabelstoneEvents.ProductConfigDeployConflict,
            "Product-config deploy conflict (409) for {ProductConfigVersionId} (product {ProductId}, "
            + "effective_from {EffectiveFrom:O}, actor {DeployActor}): {Detail}",
            incoming.ProductConfigVersionId, incoming.ProductId, incoming.EffectiveFrom,
            incoming.PublishedBy, detail);
        return Results.Conflict(new ProductConfigConflict(detail));
    }
}
