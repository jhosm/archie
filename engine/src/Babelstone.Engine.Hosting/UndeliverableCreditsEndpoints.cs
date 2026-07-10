using Babelstone.EventStore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Babelstone.Engine.Hosting;

// The operator undeliverable-credit (IOU / escheat) query surface (ADR-PC-043 slot 5). snake_case on
// the wire (the host's JSON options). No PII (ADR-PC-004): an opaque intent id, an opaque beneficiary
// ref, integer cents, a machine reason code, and dates only. Family-agnostic by construction — the
// contract names no family; the IOU ledger folds every family's undeliverable-credit facts (ADR-PC-021).

/// <summary>
/// One OUTSTANDING IOU in the query response (ADR-PC-043 slot 5): a credit that could not be delivered
/// and is still owed. In plain English: this is one line of the "which credits are still owed, to
/// whom, and how old" list an operator reads.
/// </summary>
/// <param name="IntentId">The undeliverable credit's economic-intent id (ADR-PC-043 slot 4) — an
/// opaque structural token, never PII.</param>
/// <param name="BeneficiaryRef">The opaque beneficiary the credit is owed to — never PII / an IBAN.</param>
/// <param name="AmountCents">The undeliverable amount, integer cents (ADR-PC-010).</param>
/// <param name="Reason">The stable machine reason code the credit was undeliverable — never PII.</param>
/// <param name="UnappliedAt">The economic date the credit was recorded unapplied (ADR-PC-023 input).</param>
/// <param name="AgeDays">Whole days between <see cref="UnappliedAt"/> and the query's <c>as_of</c>
/// horizon — how old the IOU is. Derived from the operator-supplied horizon, never a stored column
/// (ADR-PC-023): a rebuild reproduces every other field identically and re-derives age from the
/// horizon the operator passes.</param>
public sealed record UndeliverableCreditView(
    string IntentId,
    string BeneficiaryRef,
    long AmountCents,
    string Reason,
    DateOnly UnappliedAt,
    int AgeDays);

/// <summary>The list of OUTSTANDING IOUs and the <c>as_of</c> horizon their ages were computed
/// against (echoed so the caller knows which day the ages are relative to).</summary>
/// <param name="AsOf">The horizon the ages were computed against — the query's <c>as_of</c> param, or
/// the host clock's date when omitted.</param>
/// <param name="OutstandingCount">How many IOUs are still owed.</param>
/// <param name="Credits">The OUTSTANDING IOUs, oldest first.</param>
public sealed record UndeliverableCreditsResponse(
    DateOnly AsOf,
    int OutstandingCount,
    IReadOnlyList<UndeliverableCreditView> Credits);

/// <summary>
/// The operator undeliverable-credit query surface (ADR-PC-043 slot 5, bd babelstone-qa92.1):
/// <c>GET /v1/operations/undeliverable-credits</c> — list the OUTSTANDING IOUs (a
/// <c>CreditUnapplied</c> not yet matched by a <c>CreditReapplied</c> on the derived resolution key),
/// each with its beneficiary ref, amount, reason, unapplied date, and age. In plain English: this is
/// how an operator answers "which credits are still owed, to whom, and how old", so an ageing IOU can
/// be chased down and reapplied.
/// </summary>
/// <remarks>
/// Family-agnostic (it lives in the hosting spine, ADR-PC-021): the contract names no family; the IOU
/// ledger is the spine-owned fold over the cross-cutting <c>operations.CreditUnapplied</c> /
/// <c>operations.CreditReapplied</c> facts (mirroring the account-hold ledger). The AGE horizon is an
/// INPUT (ADR-PC-023): the optional <c>as_of</c> query parameter (an ISO date) drives it, defaulting to
/// the host clock's date when omitted — the clock read happens ONLY here in the impure HTTP shell, never
/// in the fold, so the stored IOU set stays replay-deterministic.
/// </remarks>
public static class UndeliverableCreditsEndpoints
{
    /// <summary>
    /// Map the read route ONCE at host level (family-agnostic), beside
    /// <see cref="BulkOperationsEndpoints.Map"/> in <c>Program.cs</c>.
    /// </summary>
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/operations/undeliverable-credits", ListOutstandingAsync);
    }

    /// <summary>
    /// Shape the OUTSTANDING IOU rows into the wire views against an <c>as_of</c> horizon — a PURE
    /// projection (no clock, no I/O), split out so the age arithmetic is unit-testable with no HTTP
    /// stack or database (the <see cref="BulkOperationsEndpoints.Plan"/> discipline). Ages are whole
    /// days from <c>unapplied_at</c> to <paramref name="asOf"/> (ADR-PC-023: the horizon is an input).
    /// </summary>
    internal static UndeliverableCreditsResponse Shape(
        IReadOnlyList<UndeliverableCreditRow> outstanding, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(outstanding);

        var views = outstanding
            .Select(row => new UndeliverableCreditView(
                IntentId: row.IntentId,
                BeneficiaryRef: row.BeneficiaryRef,
                AmountCents: row.AmountCents,
                Reason: row.Reason,
                UnappliedAt: row.UnappliedAt,
                AgeDays: row.UnappliedAt.DayNumber <= asOf.DayNumber
                    ? asOf.DayNumber - row.UnappliedAt.DayNumber
                    : 0))
            .ToList();

        return new UndeliverableCreditsResponse(asOf, views.Count, views);
    }

    private static async Task<IResult> ListOutstandingAsync(
        HttpRequest request,
        IIouLedgerStore store,
        TimeProvider clock,
        CancellationToken ct)
    {
        // The age horizon is an INPUT (ADR-PC-023): an operator may pin `as_of` to reproduce an ageing
        // snapshot; omitted, it defaults to today per the host clock. A malformed date fails loud (400)
        // rather than silently falling back to today and mis-labelling every age.
        DateOnly asOf;
        if (request.Query.TryGetValue("as_of", out var raw) && !string.IsNullOrEmpty(raw))
        {
            if (!DateOnly.TryParse(raw, out asOf))
            {
                return Results.Problem(
                    $"as_of '{raw}' is not a valid ISO date (yyyy-MM-dd).",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }
        else
        {
            asOf = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        }

        var outstanding = await store.GetOutstandingAsync(ct);
        return Results.Ok(Shape(outstanding, asOf));
    }
}
