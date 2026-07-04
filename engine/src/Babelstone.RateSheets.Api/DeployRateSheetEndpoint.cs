using Babelstone.Packs;
using Babelstone.RateSheets;
using Babelstone.Telemetry;
using Microsoft.Extensions.Logging;

namespace Babelstone.RateSheets.Api;

/// <summary>
/// The <c>POST /v1/rate-sheets</c> handler (ADR-PC-008): validate against the
/// pack bound, then apply the idempotency rule keyed on <c>rate_sheet_version_id</c> —
/// a new version is created (201), an identical re-POST is replayed (200), and a
/// different body under an existing version id is rejected (409), enforcing the
/// forward-only immutability guarantee (ADR-PC-008) at the API boundary as well as the table.
///
/// Observability (ADR-IC-007 Layer 1): a 409 conflict and any unexpected exception leave a
/// structured server-side record under a stable <see cref="BabelstoneEvents"/> id, carrying the
/// deploy context (version id, product family, effective_from, deploy actor) — none of it PII (a
/// rate-sheet deploy carries no depositor data). The OTel logging integration stamps
/// trace_id/span_id, so the record correlates to its trace.
/// </summary>
internal static class DeployRateSheetEndpoint
{
    /// <summary>The structured-log category — the ILogger&lt;T&gt; default name for this handler.</summary>
    private const string LogCategory = "Babelstone.RateSheets.Api.DeployRateSheetEndpoint";

    public static async Task<IResult> HandleAsync(
        RateSheetDeployRequest request,
        HttpRequest http,
        IRateSheetStore store,
        RateSheetValidator validator,
        IRateBoundsSource bounds,
        IProductConfigSource productConfigs,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        // A static handler cannot be an ILogger<T> category, so name the category after it explicitly
        // — a stable log scope an operator filters on, matching the ILogger<T> default category name.
        var logger = loggerFactory.CreateLogger(LogCategory);

        // Envelope guard FIRST: System.Text.Json binds a missing member to null/default despite the
        // record's non-nullable declarations, and the catalogued spec
        // (contracts/openapi/internal/engine-rate-sheets.openapi.yaml) marks every envelope field
        // required — so enforce that here as a clean 400 naming each offending field, never a
        // downstream NullReferenceException surfacing as an opaque 500. Runs before the
        // Idempotency-Key comparison because comparing against a null version id is meaningless.
        if (ValidateEnvelope(request) is { } invalidEnvelope)
        {
            return invalidEnvelope;
        }

        // ADR-PC-008: an Idempotency-Key header, when supplied, must equal the version id —
        // the version id IS the natural idempotency key, so no separate header is needed.
        if (http.Headers.TryGetValue("Idempotency-Key", out var key)
            && !string.IsNullOrEmpty(key)
            && !string.Equals(key.ToString(), request.RateSheetVersionId, StringComparison.Ordinal))
        {
            return Results.Problem(
                detail: "The Idempotency-Key header, when supplied, must equal rate_sheet_version_id.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // ADR-PC-008 + Amendment A3: the deploying principal is the gateway-authenticated identity, not
        // a payload field a caller can spoof. It is recorded as published_by; the treasury approver
        // and sign-off reference travel in the body and are recorded as approved_by / approval_ref.
        if (!http.Headers.TryGetValue("X-Deploy-Actor", out var actor) || string.IsNullOrWhiteSpace(actor))
        {
            return Results.Problem(
                detail: "The X-Deploy-Actor header (the gateway-authenticated deploying principal) is required.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // ADR-PC-008: the bound the sheet must honour comes from the VERIFIED pack keyed on the sheet's
        // pack_version (C.5), not a host-side config. A pack_version the engine has not loaded
        // (unknown/unpinned) is the caller's mistake — a stale or typo'd pin — so it is a 400
        // deploy rejection, not a 500: the pack abstractions surface it as a PackLoadException.
        RateBounds resolvedBounds;
        try
        {
            resolvedBounds = bounds.For(request.PackVersion);
        }
        catch (PackLoadException)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["pack_version"] =
                    [
                        $"pack_version '{request.PackVersion}' is not a loaded, verified pack; " +
                        "the rate-sheet bound cannot be resolved (ADR-PC-008 §P2, ADR-PC-007 §P4).",
                    ],
                });
        }

        var body = new RateSheetBody { Products = request.Products };
        var validation = validator.Validate(
            body, resolvedBounds, productConfigs.Active());
        if (!validation.IsValid)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["rate_sheet"] = [.. validation.Diagnostics] });
        }

        var sheet = new RateSheet(
            request.RateSheetVersionId,
            request.ProductFamily,
            request.PackVersion,
            // Truncate to PostgreSQL's microsecond resolution at the boundary so the value we
            // store and the value we compare on a re-POST share a precision. Without this, an
            // effective_from carrying sub-microsecond (100ns) ticks round-trips as a truncated
            // value, and the ADR-PC-008 identity check (existing == incoming) would misclassify an
            // identical re-POST as a 409 (a forward-only-immutability breach that did not happen).
            ToMicroseconds(request.EffectiveFrom),
            body,
            request.ApprovedBy,
            request.ApprovalRef,
            PublishedBy: actor.ToString());

        // From here on a write is in play. An unexpected failure (a dropped DB, a serialization
        // fault, the read-back invariant below) is logged with the deploy context under a stable
        // id BEFORE UseExceptionHandler maps it to a 500 — the generic handler has only the bare
        // exception, never the version id / family / actor an operator needs (ADR-IC-007 Layer 1).
        try
        {
            // Happy idempotency path: a prior sheet under this version id short-circuits before
            // any write.
            var existing = await store.TryGetAsync(sheet.RateSheetVersionId, ct);
            if (existing is not null)
            {
                return Idempotent(existing, sheet, logger);
            }

            try
            {
                await store.InsertAsync(sheet, ct);
            }
            catch (DuplicateRateSheetVersionException)
            {
                // Race: a concurrent deploy committed first under the same version id, or claimed
                // this family's effective_from. Re-read and apply the same ADR-PC-008 rule.
                var raced = await store.TryGetAsync(sheet.RateSheetVersionId, ct);
                return raced is null
                    ? Conflict(sheet, logger,
                        "effective_from is already claimed by a different rate_sheet_version_id.")
                    : Idempotent(raced, sheet, logger);
            }

            // Re-read so the response carries the database-assigned published_at. A just-committed
            // row that cannot be read back is an invariant violation (not a routine empty result),
            // so fail loud rather than silently returning the in-memory sheet with a null published_at.
            var stored = await store.TryGetAsync(sheet.RateSheetVersionId, ct)
                ?? throw new InvalidOperationException(
                    $"Rate sheet '{sheet.RateSheetVersionId}' was inserted but could not be read back.");
            return Results.Created(
                $"/v1/rate-sheets/{sheet.RateSheetVersionId}", RateSheetResponse.From(stored));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client aborted the request — a routine cancellation, not an unexpected deploy
            // fault. Don't log it under RateSheetDeployUnexpectedError as a 500; rethrow so the
            // pipeline maps it (the same cancellation-filter idiom as OutboxRelayService /
            // ProjectionRelay), keeping the Error log reserved for genuine faults.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                BabelstoneEvents.RateSheetDeployUnexpectedError, ex,
                "Unexpected error deploying rate sheet {RateSheetVersionId} (family {ProductFamily}, "
                + "effective_from {EffectiveFrom:O}, actor {DeployActor}); returning 500.",
                sheet.RateSheetVersionId, sheet.ProductFamily, sheet.EffectiveFrom, sheet.PublishedBy);
            throw;
        }
    }

    /// <summary>
    /// The deploy envelope's required-field guard: every envelope field the stored row needs must be
    /// present and non-blank (and <c>products</c> non-null, <c>effective_from</c> non-default — the
    /// value a missing member binds to). Returns the 400 validation problem
    /// naming every offending field at once, or null when the envelope is well-formed. A pure
    /// function (no I/O, no clock) so the guard is unit-testable without the HTTP stack.
    /// </summary>
    internal static IResult? ValidateEnvelope(RateSheetDeployRequest request)
    {
        var missing = new Dictionary<string, string[]>();
        AddIfBlank(missing, "rate_sheet_version_id", request.RateSheetVersionId);
        AddIfBlank(missing, "product_family", request.ProductFamily);
        AddIfBlank(missing, "pack_version", request.PackVersion);
        AddIfBlank(missing, "approved_by", request.ApprovedBy);
        AddIfBlank(missing, "approval_ref", request.ApprovalRef);
        if (request.EffectiveFrom == default)
        {
            missing["effective_from"] = ["effective_from is required (a missing member binds to the default instant)."];
        }

        if (request.Products is null)
        {
            missing["products"] = ["products is required — the sheet's priceable body (ADR-PC-008)."];
        }

        // TypedResults (not Results) so the guard's outcome is the concrete ValidationProblem
        // type — the unit tests assert on it directly; the wire shape is identical.
        return missing.Count > 0 ? TypedResults.ValidationProblem(missing) : null;
    }

    private static void AddIfBlank(Dictionary<string, string[]> missing, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            missing[field] = [$"{field} is required and must be non-blank."];
        }
    }

    // PostgreSQL TIMESTAMPTZ resolves to microseconds; .NET DateTimeOffset to 100ns ticks.
    // Normalise at the boundary (10 ticks = 1 microsecond) so stored and compared values match.
    private static DateTimeOffset ToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMicrosecond), value.Offset);

    private static IResult Idempotent(RateSheet existing, RateSheet incoming, ILogger logger)
    {
        var identical =
            string.Equals(existing.ProductFamily, incoming.ProductFamily, StringComparison.Ordinal)
            && string.Equals(existing.PackVersion, incoming.PackVersion, StringComparison.Ordinal)
            && existing.EffectiveFrom == incoming.EffectiveFrom
            && string.Equals(existing.ApprovedBy, incoming.ApprovedBy, StringComparison.Ordinal)
            && string.Equals(existing.ApprovalRef, incoming.ApprovalRef, StringComparison.Ordinal)
            && string.Equals(
                RateSheetJson.Canonical(existing.Body),
                RateSheetJson.Canonical(incoming.Body),
                StringComparison.Ordinal);

        return identical
            ? Results.Ok(RateSheetResponse.From(existing))
            : Conflict(incoming, logger,
                $"rate_sheet_version_id '{incoming.RateSheetVersionId}' already exists with a different definition; "
                + "corrections ship forward-only as a new version (ADR-PC-008 §P5).");
    }

    // A 409, recorded server-side under a stable id (ADR-IC-007 Layer 1) with the deploy context —
    // a forward-only-immutability breach (ADR-PC-008) the operator should see, not just a bare HTTP 409.
    // DeployActor is the sheet's PublishedBy, i.e. the gateway-authenticated X-Deploy-Actor header.
    private static IResult Conflict(RateSheet incoming, ILogger logger, string detail)
    {
        logger.LogWarning(
            BabelstoneEvents.RateSheetDeployConflict,
            "Rate-sheet deploy conflict (409) for {RateSheetVersionId} (family {ProductFamily}, "
            + "effective_from {EffectiveFrom:O}, actor {DeployActor}): {Detail}",
            incoming.RateSheetVersionId, incoming.ProductFamily, incoming.EffectiveFrom,
            incoming.PublishedBy, detail);
        return Results.Conflict(new RateSheetConflict(detail));
    }
}
