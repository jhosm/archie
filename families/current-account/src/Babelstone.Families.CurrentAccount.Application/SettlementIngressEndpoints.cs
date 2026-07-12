using System.Text.Json.Serialization;
using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.Families.CurrentAccount;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Babelstone.Families.CurrentAccount.Application;

/// <summary>
/// The engine-CA SETTLEMENT INGRESS (ADR-PC-043). In plain English: the settlement
/// saga always POSTs to the SAME three counterparty-invariant paths (<c>/v1/reservations</c>,
/// <c>/v1/debits</c>, <c>/v1/credits</c>) and only flips the base URL to reach the engine-owned current
/// account instead of the legacy Core ACL. But the engine does not serve those paths — the current_account
/// family serves <c>/v1/accounts/{id}/authorize|capture|credit</c>. This adapter closes that gap: it serves
/// the three invariant settlement paths and maps each onto the CA family's real writer for the account the
/// leg's <c>account_ref</c> names, so a <c>ce_settlementtarget=engine-ca</c> leg lands on the customer's
/// actual conta à ordem.
/// </summary>
/// <remarks>
/// <para>
/// <b>The three mappings (ADR-PC-043).</b>
/// <list type="bullet">
///   <item><b><c>POST /v1/reservations</c> (ReserveAccountBalance) → the CA AUTHORIZE writer</b> — places
///   the reversible hold that the confirm leg later captures.</item>
///   <item><b><c>POST /v1/debits</c> (ConfirmDebit) → the CA CAPTURE writer</b> — turns the reservation into
///   a real Debit (<c>HoldCaptured</c> + <c>AccountDebited</c>).</item>
///   <item><b><c>POST /v1/credits</c> (ConfirmCredit) → the CA CREDIT-receive writer</b> — lands the value
///   as a Credit (admitting Active/Dormant, refusing Closed/Erased by construction).</item>
/// </list>
/// </para>
/// <para>
/// <b>Account resolution — the destination the WRITER reads, never the substrate router (ADR-PC-043).</b>
/// The leg's <c>account_ref</c> is the current-account family's opaque stream id
/// (<c>AccountRef == AccountId.ToString()</c>, ADR-PC-033), so the ingress resolves the destination account
/// by parsing it as the account GUID. This is the exact carve-out ADR-PC-043 sanctions: the SUBSTRATE
/// stayed payload-blind for routing (it chose the counterparty from the header alone), and only HERE — the
/// engine-CA WRITER side — is the promoted <c>account_ref</c> read as the destination. A body whose
/// <c>account_ref</c> is not a GUID (a legacy ACT-token that reached the engine-CA path by misconfiguration)
/// is a 400 — fail loud, never guess an account.
/// </para>
/// <para>
/// <b>Intent-derived idempotency + the deterministic hold link (ADR-PC-043 / ADR-PC-029).</b> The
/// exactly-once key on this surface is the BODY's economic-intent reference, NOT the HTTP Idempotency-Key
/// (the scoped ADR-PC-029 inversion): the CA-apply command_id is <see cref="SettlementIntentKey.Derive"/>d
/// from it, so a saga reissue with a byte-identical body collapses at <c>command_dedup</c> to ONE append. The
/// reserve→confirm HOLD LINK is deterministic the same way: the authorize command_id is derived from a
/// hold-namespaced projection of the intent reference, so the authorize places <c>hold-{id:N}</c> and the
/// confirm reconstructs the SAME id to target that hold — no round-trip of the returned hold_id
/// through the saga is needed.
/// </para>
/// <para>
/// <b>Family-agnostic hosting seam.</b> This is a family surface mapped by the family's own
/// <c>CurrentAccountHostModule.MapEndpoints</c> (ADR-PC-021), discovered by the host's assembly scan — the
/// host names no current-account type. Every route is mTLS-only internal ingress (ADR-IC-006 Boundary 2),
/// never a public Kong route: its only caller is the orchestrator's settlement dispatcher.
/// </para>
/// </remarks>
public static class SettlementIngressEndpoints
{
    /// <summary>The acting principal recorded on every settlement-ingress append (a machine/saga settlement
    /// principal). A role, never PII (ADR-PC-004).</summary>
    private const string SettlementActor = "svc:settlement-dispatch";

    /// <summary>The namespace tag that separates the reserve/authorize HOLD-linking command_id from the
    /// confirm/credit apply command_id for the SAME intent reference — so the authorize hold and the capture
    /// append get distinct ids while both are deterministic functions of the one intent reference (a reissue
    /// of either leg collapses to that leg's identical id). Prefixing the intent reference before the v5
    /// derivation, mirroring the leg-namespacing SettlementReferences does for the wire references.</summary>
    private const string AuthorizeHoldTag = "AUTHORIZE-HOLD:";

    /// <summary>Register the three counterparty-invariant settlement routes. Called by
    /// <c>CurrentAccountHostModule.MapEndpoints</c>; the Layer-4 route sweep discovers them through the same
    /// composition and matches them against the committed engine-settlement-ingress OpenAPI spec.</summary>
    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/v1/reservations", ReserveAsync);
        app.MapPost("/v1/debits", ConfirmDebitAsync);
        app.MapPost("/v1/credits", ConfirmCreditAsync);
    }

    private static async Task<IResult> ReserveAsync(
        SettlementLegRequest request,
        CurrentAccountAuthorizeService service,
        ICommandLog commandLog,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (Resolve(request) is not { } resolved)
        {
            return BadAccountOrAmount(request);
        }

        var (accountId, intentReference) = resolved;
        // The authorize command_id derives from a hold-namespaced projection of the intent reference, so the
        // placed hold id (hold-{authorizeCommandId:N}) is reproducible by the confirm leg WITHOUT round-tripping
        // it through the saga. Distinct from the confirm/credit apply key (the AuthorizeHoldTag namespace).
        var authorizeCommandId = SettlementIntentKey.Derive(AuthorizeHoldTag + intentReference);

        // Idempotent pre-check: a reissue replays the ORIGINAL verdict (same hold) with no second append.
        var receipt = await commandLog.TryGetAsync(authorizeCommandId, ct);
        if (receipt is not null)
        {
            var replay = await service.ReconstructVerdictAsync(accountId, receipt.CommitSequence, ct);
            return SettlementResult(accountId, replay.CommitSequence);
        }

        var command = new AuthorizeAccountCommand(
            accountId, request.AmountCents, ValueDate(request, clock), SettlementActor, authorizeCommandId);
        try
        {
            var verdict = await service.AuthorizeAsync(command, clock.GetUtcNow(), ct);
            // A DECLINED authorize is a 422 on THIS settlement surface, never a 200-with-Declined (which the
            // dispatcher would mis-read as Applied and march to COMPLETED with zero funds held). The source
            // holds the funds; a decline surfaces as a 4xx on this settlement surface (ADR-PC-043 error model).
            return verdict.Outcome == AuthorizeOutcomes.Authorized
                ? SettlementResult(accountId, verdict.CommitSequence)
                : DeclinedProblem(verdict.DeclinedReason);
        }
        catch (DuplicateCommandException e)
        {
            var replay = await service.ReconstructVerdictAsync(accountId, e.CommitSequence, ct);
            return SettlementResult(accountId, replay.CommitSequence);
        }
        catch (ConcurrencyException)
        {
            return Results.Problem(
                $"Account {accountId} was modified concurrently.", statusCode: StatusCodes.Status409Conflict);
        }
        catch (DomainRejectedException e)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> ConfirmDebitAsync(
        SettlementLegRequest request,
        CurrentAccountCaptureService service,
        AggregateRuntime<AccountPosition> runtime,
        ICommandLog commandLog,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (Resolve(request) is not { } resolved)
        {
            return BadAccountOrAmount(request);
        }

        var (accountId, intentReference) = resolved;
        // Reconstruct the SAME hold the reserve leg's authorize placed (target_hold_id = f(intent_reference)),
        // and derive the capture's append command_id from the intent reference (distinct namespace from the
        // authorize key). A reissue collapses to ONE Debit at command_dedup.
        var authorizeCommandId = SettlementIntentKey.Derive(AuthorizeHoldTag + intentReference);
        var targetHoldId = $"hold-{authorizeCommandId:N}";
        var captureCommandId = SettlementIntentKey.Derive(intentReference);

        var receipt = await commandLog.TryGetAsync(captureCommandId, ct);
        if (receipt is not null)
        {
            return SettlementResult(accountId, receipt.CommitSequence, await StatusAsync(runtime, accountId, ct));
        }

        var command = new CaptureAccountCommand(
            accountId, targetHoldId, request.AmountCents, ValueDate(request, clock), intentReference,
            SettlementActor, captureCommandId);
        try
        {
            var outcome = await service.CaptureAsync(command, clock.GetUtcNow(), ct);
            return SettlementResult(
                accountId, outcome.CommitSequence, await StatusAsync(runtime, accountId, ct), outcome.Reconciliation);
        }
        catch (DuplicateCommandException e)
        {
            return SettlementResult(accountId, e.CommitSequence, await StatusAsync(runtime, accountId, ct));
        }
        catch (ConcurrencyException)
        {
            return Results.Problem(
                $"Account {accountId} was modified concurrently.", statusCode: StatusCodes.Status409Conflict);
        }
        catch (DomainRejectedException e)
        {
            // An unmatched target hold or a non-positive amount — a 4xx the dispatcher classifies as terminal
            // Refused (the source holds the funds), never a phantom debit (ADR-PC-043).
            return Results.Problem(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> ConfirmCreditAsync(
        SettlementLegRequest request,
        CurrentAccountCreditReceiveService service,
        AggregateRuntime<AccountPosition> runtime,
        ICommandLog commandLog,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (Resolve(request) is not { } resolved)
        {
            return BadAccountOrAmount(request);
        }

        var (accountId, intentReference) = resolved;
        var creditCommandId = SettlementIntentKey.Derive(intentReference);

        var receipt = await commandLog.TryGetAsync(creditCommandId, ct);
        if (receipt is not null)
        {
            return SettlementResult(accountId, receipt.CommitSequence, await StatusAsync(runtime, accountId, ct));
        }

        var command = new ReceiveCreditCommand(
            accountId, request.AmountCents, ValueDate(request, clock), intentReference,
            SettlementActor, creditCommandId);
        try
        {
            var commitSequence = await service.ReceiveCreditAsync(command, clock.GetUtcNow(), ct);
            return SettlementResult(accountId, commitSequence, await StatusAsync(runtime, accountId, ct));
        }
        catch (DuplicateCommandException e)
        {
            return SettlementResult(accountId, e.CommitSequence, await StatusAsync(runtime, accountId, ct));
        }
        catch (DomainRejectedException e)
        {
            // A non-admitting account (Closed → ACCOUNT_CLOSED, Erased → ACCOUNT_ERASED) or a non-positive
            // amount — a 4xx, never a 200-with-Declined the dispatcher would march to COMPLETED (ADR-PC-043).
            return Results.Problem(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    // Resolve the destination account id (from the opaque account_ref = account GUID, ADR-PC-033) and the
    // exactly-once intent reference. Returns null when account_ref is not a GUID or the intent reference is
    // absent or the amount is non-positive — the caller surfaces the specific 400. The intent reference falls
    // back through the leg's own references so a body built before intent_reference was threaded still works.
    private static (Guid AccountId, string IntentReference)? Resolve(SettlementLegRequest request)
    {
        if (request is null || request.AmountCents <= 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.AccountRef) || !Guid.TryParse(request.AccountRef, out var accountId))
        {
            return null;
        }

        var intentReference = FirstNonBlank(
            request.IntentReference, request.ReservationRef, request.CoreHoldRef, request.CreditRef);
        return string.IsNullOrWhiteSpace(intentReference) ? null : (accountId, intentReference!);
    }

    // A settlement-facing DECLINED authorize as an RFC7807 problem+json 422 that carries the SPECIFIC bounded
    // machine code (ADR-PC-043 error model / ADR-PC-037 §D6 taxonomy) — LIMIT_EXCEEDED, INSUFFICIENT_AVAILABLE_
    // BALANCE, OVERDRAFT_LIMIT_EXCEEDED, ACCOUNT_NOT_ACTIVE — so the dispatcher/UI can name WHY the source held
    // the funds instead of a generic "precondition refused". The code rides a structural `code` extension member
    // (the machine token) alongside the human `detail`; both are STRUCTURAL only — no PII (ADR-PC-004). Fail-
    // closed is unchanged: this is still a 422, nothing was committed. A null reason (unreachable — a decline
    // always carries a code) degrades to the safe generic detail without an invented code.
    private static IResult DeclinedProblem(string? declinedReason)
    {
        var extensions = declinedReason is { Length: > 0 } code
            ? new Dictionary<string, object?> { ["code"] = code }
            : null;
        return Results.Problem(
            $"authorize declined ({declinedReason}) — the source holds the funds (ADR-PC-043).",
            statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: extensions);
    }

    private static IResult BadAccountOrAmount(SettlementLegRequest request)
    {
        if (request is null || request.AmountCents <= 0)
        {
            return Results.Problem(
                "amount_cents must be a positive integer in cents (ADR-PC-010).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(request.AccountRef) || !Guid.TryParse(request.AccountRef, out _))
        {
            return Results.Problem(
                "account_ref must be the engine current-account id (a GUID) on an engine-ca leg (ADR-PC-043).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.Problem(
            "intent_reference is required — the settlement append command_id derives from it (ADR-PC-043).",
            statusCode: StatusCodes.Status400BadRequest);
    }

    // The economic value-date: the caller's supplied date, or the host clock's date at this impure boundary
    // (ADR-PC-010 — the clock is read only in the HTTP shell, never in a decider/fold).
    private static DateOnly ValueDate(SettlementLegRequest request, TimeProvider clock) =>
        request.ValueDate ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private static async Task<string> StatusAsync(
        AggregateRuntime<AccountPosition> runtime, Guid accountId, CancellationToken ct) =>
        (await runtime.LoadAsync(accountId, ct)).State.Lifecycle.ToString().ToUpperInvariant();

    private static IResult SettlementResult(
        Guid accountId, long commitSequence, string? status = null, string? reconciliation = null) =>
        Results.Ok(new SettlementApplyResponse(accountId, status ?? "ACTIVE", commitSequence, reconciliation));

    private static string? FirstNonBlank(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>
/// The one settlement-leg request body the engine-CA ingress binds. The settlement
/// saga's reserve / confirm-debit / confirm-credit bodies all share this snake_case shape on the wire; the
/// ingress reads the destination <c>account_ref</c>, the amount, and the leg's exactly-once
/// <c>intent_reference</c> (with backward-compatible fall-backs to the reserve / debit / credit references a
/// pre-threading body carries). STRUCTURAL only — no PII (ADR-PC-004): the account_ref is the opaque account
/// id, the references are process/intent-derived tokens, money is integer cents.
/// </summary>
/// <param name="AccountRef">The destination account_ref — the engine current-account id (a GUID string) the
/// leg lands on (ADR-PC-043). Required; a non-GUID is a 400.</param>
/// <param name="AmountCents">The amount to land, integer cents (ADR-PC-010) — the source Movement.Amount (the
/// in-band WRONG-AMOUNT guard). The ingress rejects a non-positive amount (400).</param>
/// <param name="IntentReference">The ADR-PC-043 economic-intent reference — the exactly-once + hold-
/// linking key. Required in effect: absent, the ingress falls back to <paramref name="ReservationRef"/> /
/// <paramref name="CoreHoldRef"/> / <paramref name="CreditRef"/> before failing 400.</param>
/// <param name="ReservationRef">The reserve leg's reservation reference (fall-back intent key).</param>
/// <param name="CoreHoldRef">The confirm-debit leg's Core-hold reference (fall-back intent key).</param>
/// <param name="CreditRef">The confirm-credit leg's credit reference (fall-back intent key).</param>
/// <param name="SettlementTarget">The counterparty discriminator the substrate already routed on — carried
/// through informationally, never re-read here (the ingress is the engine-CA end of the wire).</param>
/// <param name="ValueDate">The leg's economic value-date (ADR-PC-023); the host clock's date when omitted.</param>
public sealed record SettlementLegRequest(
    [property: JsonPropertyName("account_ref")] string AccountRef,
    [property: JsonPropertyName("amount_cents")] long AmountCents,
    [property: JsonPropertyName("intent_reference")] string? IntentReference = null,
    [property: JsonPropertyName("reservation_ref")] string? ReservationRef = null,
    [property: JsonPropertyName("core_hold_ref")] string? CoreHoldRef = null,
    [property: JsonPropertyName("credit_ref")] string? CreditRef = null,
    [property: JsonPropertyName("settlement_target")] string? SettlementTarget = null,
    [property: JsonPropertyName("value_date")] DateOnly? ValueDate = null);
