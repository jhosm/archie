using Babelstone.RateSheets;

namespace Babelstone.RateSheets.Api;

/// <summary>
/// The <c>POST /v1/rate-sheets</c> handler (ADR-PC-008 §P2): validate against the
/// pack bound, then apply the idempotency rule keyed on <c>rate_sheet_version_id</c> —
/// a new version is created (201), an identical re-POST is replayed (200), and a
/// different body under an existing version id is rejected (409), enforcing the
/// forward-only immutability guarantee (§P5) at the API boundary as well as the table.
/// </summary>
internal static class DeployRateSheetEndpoint
{
    public static async Task<IResult> HandleAsync(
        RateSheetDeployRequest request,
        HttpRequest http,
        IRateSheetStore store,
        RateSheetValidator validator,
        IRateBoundsSource bounds,
        CancellationToken ct)
    {
        // §P2: an Idempotency-Key header, when supplied, must equal the version id —
        // the version id IS the natural idempotency key, so no separate header is needed.
        if (http.Headers.TryGetValue("Idempotency-Key", out var key)
            && !string.IsNullOrEmpty(key)
            && !string.Equals(key.ToString(), request.RateSheetVersionId, StringComparison.Ordinal))
        {
            return Results.Problem(
                detail: "The Idempotency-Key header, when supplied, must equal rate_sheet_version_id.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // §P4 + Amendment A3: the deploying principal is the gateway-authenticated identity, not
        // a payload field a caller can spoof. It is recorded as published_by; the treasury approver
        // and sign-off reference travel in the body and are recorded as approved_by / approval_ref.
        if (!http.Headers.TryGetValue("X-Deploy-Actor", out var actor) || string.IsNullOrWhiteSpace(actor))
        {
            return Results.Problem(
                detail: "The X-Deploy-Actor header (the gateway-authenticated deploying principal) is required.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var body = new RateSheetBody { Products = request.Products };
        var validation = validator.Validate(body, bounds.For(request.PackVersion));
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
            // value, and the §P2 identity check (existing == incoming) would misclassify an
            // identical re-POST as a 409 (a forward-only-immutability breach that did not happen).
            ToMicroseconds(request.EffectiveFrom),
            body,
            request.ApprovedBy,
            request.ApprovalRef,
            PublishedBy: actor.ToString());

        // Happy idempotency path: a prior sheet under this version id short-circuits before
        // any write.
        var existing = await store.TryGetAsync(sheet.RateSheetVersionId, ct);
        if (existing is not null)
        {
            return Idempotent(existing, sheet);
        }

        try
        {
            await store.InsertAsync(sheet, ct);
        }
        catch (DuplicateRateSheetVersionException)
        {
            // Race: a concurrent deploy committed first under the same version id, or claimed
            // this family's effective_from. Re-read and apply the same §P2 rule.
            var raced = await store.TryGetAsync(sheet.RateSheetVersionId, ct);
            return raced is null
                ? Results.Conflict(new RateSheetConflict(
                    "effective_from is already claimed by a different rate_sheet_version_id."))
                : Idempotent(raced, sheet);
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

    // PostgreSQL TIMESTAMPTZ resolves to microseconds; .NET DateTimeOffset to 100ns ticks.
    // Normalise at the boundary (10 ticks = 1 microsecond) so stored and compared values match.
    private static DateTimeOffset ToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMicrosecond), value.Offset);

    private static IResult Idempotent(RateSheet existing, RateSheet incoming)
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
            : Results.Conflict(new RateSheetConflict(
                $"rate_sheet_version_id '{incoming.RateSheetVersionId}' already exists with a different definition; "
                + "corrections ship forward-only as a new version (ADR-PC-008 §P5)."));
    }

    private sealed record RateSheetConflict(string Error);
}
