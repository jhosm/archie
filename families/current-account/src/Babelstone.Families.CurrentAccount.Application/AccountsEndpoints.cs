using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.Families.CurrentAccount;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Babelstone.Families.CurrentAccount.Application;

/// <summary>
/// The current_account command / query endpoints (ADR-PC-021) — the demand-account sibling of
/// <c>DepositsEndpoints</c> / <c>LoansEndpoints</c>. A thin HTTP front door over the pure lifecycle
/// decider via <see cref="CurrentAccountLifecycleService"/>: the host owns the wall clock at the boundary
/// (it stamps a missing opened_at / marked_at / … so the decider stays pure, ADR-PC-010) and maps the
/// domain exceptions to HTTP.
/// <para>
/// NONE of these routes is step-up-SCA-gated. The lifecycle transitions (open / mark-dormant / reactivate
/// / close) move no money — they only relabel the account's lifecycle state. The synchronous AUTHORIZE
/// money-mover DOES earmark funds, but it is deliberately ungated too (ADR-PC-034): it is a machine/rail-
/// initiated, de-settled decision on the mTLS-only internal command surface (ADR-IC-006 Boundary 2 —
/// never a public Kong route), and strong customer authentication is an upstream stage-1/2 concern, not
/// the engine's stage-3/5 decision (ADR-PC-034) — adding an engine-side SCA gate here would contradict
/// that split. Every route stays idempotent (ADR-PC-029): an at-least-once retry replays the original
/// outcome rather than re-applying it.
/// </para>
/// <para>
/// The authorize route stays UNGATED here (no <c>ScaPreconditionFilter</c> wrap). The reserved
/// <see cref="Babelstone.Engine.Hosting.ScaServicePrincipal.AuthorizeOperation"/> /
/// <see cref="Babelstone.Engine.Hosting.ScaServicePrincipal.AuthorizeDebitScope"/> document the trust-scope
/// decision — why a debit-authorizing principal would carry a DISTINCT scope and why an engine-side gate is
/// not wired this run — see <see cref="Babelstone.Engine.Hosting.ScaServicePrincipal"/>.
/// </para>
/// </summary>
public static class AccountsEndpoints
{
    private const string OperatorActor = "ops:account-officer";
    private const string DpoActor = "ops:dpo";

    // The default acting principal recorded on an authorize append: a machine/rail authorize caller (the
    // authorize hot path is not human-initiated), a structural role, never PII.
    private const string AuthorizeActor = "svc:payment-authorize";

    // The default acting principal recorded on a hold-expiry append: the non-interactive ADR-PC-036
    // lifecycle-command driver (hold expiry is machine-fired off a projection, never human-initiated), a
    // structural role, never PII.
    private const string ExpiryActor = "svc:lifecycle-hold-expiry";

    // The default acting principal recorded on an overdraft-accrual append: the same non-interactive ADR-PC-036
    // lifecycle-command driver (accrual is machine-fired off the overdraft projection), a structural role,
    // never PII.
    private const string AccrualActor = "svc:lifecycle-overdraft-accrual";

    // The default acting principal recorded on a settlement credit / capture append: the substrate-owned
    // SettlementProcess saga's dispatcher (a machine/saga caller, never human-initiated), a structural role,
    // never PII (ADR-PC-043 / ADR-PC-004).
    private const string SettlementActor = "svc:settlement-dispatch";

    public static void Map(IEndpointRouteBuilder app)
    {
        // Open a new demand account (opens the stream). The Idempotency-Key is OPTIONAL here (the
        // new-stream append is naturally a one-shot), but honoured when supplied so a retry of the SAME
        // open dedupes (ADR-PC-029).
        app.MapPost("/v1/accounts", OpenAsync);

        // The operating lifecycle transitions on a live account. Each moves no money, so none is
        // SCA-gated; each carries a mandatory Idempotency-Key so an at-least-once retry replays rather
        // than re-transitions. {id:guid} is load-bearing: the :guid constraint excludes literal words, so
        // registration order among the prefix-sharing routes below is moot.
        app.MapPost("/v1/accounts/{id:guid}/dormancy", MarkDormantAsync);
        app.MapPost("/v1/accounts/{id:guid}/reactivate", ReactivateAsync);
        app.MapPost("/v1/accounts/{id:guid}/close", CloseAsync);

        // The synchronous AUTHORIZE money-mover (ADR-PC-037 / ADR-PC-034): place a hold or decline, in
        // real time. Ungated (see the class remarks) but carries a MANDATORY Idempotency-Key — a replayed
        // authorize returns the original verdict (same hold_id) with no second HoldPlaced.
        app.MapPost("/v1/accounts/{id:guid}/authorize", AuthorizeAsync);

        // The SETTLEMENT-facing money-movers (ADR-PC-043): the substrate SettlementProcess saga drives these
        // against the engine-owned CA. UNLIKE every other route, their append command_id is derived from the
        // BODY's economic-intent reference (NOT the HTTP Idempotency-Key — the scoped ADR-PC-029 carve-out),
        // so a saga reissue with a byte-identical body collapses to ONE append at command_dedup. /credit lands
        // a received Credit (admitting Active/Dormant, refusing Closed/Erased by construction); /capture turns
        // an authorize reservation into a real Debit (the spine HoldCaptured + the family AccountDebited in one
        // append). A DECLINED/rejected settlement is a 4xx (ADR-PC-043 — never a 200-with-Declined
        // the dispatcher would mis-classify as Applied).
        app.MapPost("/v1/accounts/{id:guid}/credit", ReceiveCreditAsync);
        app.MapPost("/v1/accounts/{id:guid}/capture", CaptureAsync);

        // The projection-derived HOLD-EXPIRY command (ADR-PC-037): append a HoldExpired for a hold the
        // ADR-PC-036 lifecycle-command driver found due against a value-date horizon. Moves no money (a
        // release with no posting), so ungated like the lifecycle transitions; the mandatory Idempotency-Key
        // (the driver's canonical dispatch id) makes an at-least-once retry replay rather than re-expire.
        // {id:guid} names the account stream; {holdId} is the hold's free-string lifecycle key.
        app.MapPost("/v1/accounts/{id:guid}/holds/{holdId}/expire", ExpireHoldAsync);

        // The projection-derived OVERDRAFT-ACCRUAL command (ADR-PC-037): append an OverdraftInterestAccrued
        // for an account the ADR-PC-036 lifecycle-command driver found drawn below zero as-of a value-date. It
        // posts a fee Movement (a Debit that deepens the overdraft), but it is an INTERNAL ledger charge, not a
        // rails money-mover (the fee is an Observed engine-internal-already-effected Movement, ADR-PC-043 — no
        // external counterparty, no cash leg to settle), so it is ungated like the hold-expiry release; the
        // mandatory Idempotency-Key (the driver's canonical dispatch id) makes an at-least-once re-POST replay
        // rather than re-accrue — one accrual per account per day.
        app.MapPost("/v1/accounts/{id:guid}/overdraft/accrue", AccrueOverdraftInterestAsync);

        // GDPR Article 17 right-to-be-forgotten (ADR-PC-004) — a DIFFERENT gate from the money-mover SCA
        // gate; governed by its own crypto-shred discipline. Mandatory Idempotency-Key (key destruction
        // is irreversible).
        app.MapPost("/v1/accounts/{id:guid}/erase-personal-data", ErasePersonalDataAsync);

        // The query surface: ONE canonical account resource, the structural half folded from the stream +
        // the two balances and active holds read from the spine-owned folds (ADR-PC-033). Read-only, ungated.
        app.MapGet("/v1/accounts/{id:guid}", GetAccountAsync);

        // The movement-statement read (ADR-PC-032): the account's recorded movement lines in stable order,
        // read from the SAME spine-owned movement ledger the accounting balance sums — the balance is the
        // fold's rollup, this is the fold's lines. Read-only, ungated; carries no PII (structural closed-enum
        // names + integer cents only, ADR-PC-004 §P2). {id:guid} is load-bearing (the :guid constraint
        // excludes literal words, so this shares the /v1/accounts/{id:guid}/… prefix cleanly).
        app.MapGet("/v1/accounts/{id:guid}/movements", GetMovementsAsync);
    }

    private static async Task<IResult> OpenAsync(
        OpenAccountRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CurrentAccountLifecycleService service,
        AggregateRuntime<AccountPosition> runtime,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (idempotencyKey is not null && !Guid.TryParse(idempotencyKey, out _))
        {
            return Results.Problem(
                "Idempotency-Key, when present, must be a UUID (ADR-PC-029).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var commandId = idempotencyKey is null ? (Guid?)null : Guid.Parse(idempotencyKey);
        // The host owns the wall clock at this boundary (ADR-PC-010): stamp a missing opened_at and pass
        // the derived value-date as an INPUT to the pure decider — no clock read in the decision.
        var openedAt = request.OpenedAt ?? clock.GetUtcNow();
        var command = new OpenAccountCommand(
            request.AccountId, request.ProductCode, request.Currency,
            DateOnly.FromDateTime(openedAt.UtcDateTime));

        long commitSequence;
        try
        {
            commitSequence = await service.OpenAsync(
                command, request.Actor ?? OperatorActor, openedAt, commandId, ct);
        }
        catch (DuplicateCommandException)
        {
            var replay = await runtime.LoadAsync(request.AccountId, ct);
            return Results.Ok(new AccountCommandResponse(request.AccountId, Status(replay.State), replay.Version));
        }
        catch (ConcurrencyException)
        {
            // A concurrent open of the same new stream won the race — surface a 409 rather than a spurious
            // "already exists" that a second open would otherwise get from the decider.
            return Results.Problem(
                $"Account {request.AccountId} was opened concurrently.",
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (DomainRejectedException e)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        return Results.Created(
            $"/v1/accounts/{request.AccountId}",
            new AccountCommandResponse(request.AccountId, Status((await runtime.LoadAsync(request.AccountId, ct)).State), commitSequence));
    }

    private static Task<IResult> MarkDormantAsync(
        Guid id,
        MarkAccountDormantRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CurrentAccountLifecycleService service,
        AggregateRuntime<AccountPosition> runtime,
        ICommandLog commandLog,
        TimeProvider clock,
        CancellationToken ct)
        => RunIdempotentAsync(
            id, idempotencyKey, runtime, commandLog,
            (commandId, markedAt) => service.MarkDormantAsync(
                new MarkAccountDormantCommand(id, DateOnly.FromDateTime(markedAt.UtcDateTime), request.Reason),
                request.Actor ?? OperatorActor, markedAt, commandId, ct),
            request.MarkedAt, clock, ct);

    private static Task<IResult> ReactivateAsync(
        Guid id,
        ReactivateAccountRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CurrentAccountLifecycleService service,
        AggregateRuntime<AccountPosition> runtime,
        ICommandLog commandLog,
        TimeProvider clock,
        CancellationToken ct)
        => RunIdempotentAsync(
            id, idempotencyKey, runtime, commandLog,
            (commandId, reactivatedAt) => service.ReactivateAsync(
                new ReactivateAccountCommand(id, DateOnly.FromDateTime(reactivatedAt.UtcDateTime)),
                request.Actor ?? OperatorActor, reactivatedAt, commandId, ct),
            request.ReactivatedAt, clock, ct);

    private static Task<IResult> CloseAsync(
        Guid id,
        CloseAccountRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CurrentAccountLifecycleService service,
        AggregateRuntime<AccountPosition> runtime,
        ICommandLog commandLog,
        TimeProvider clock,
        CancellationToken ct)
        => RunIdempotentAsync(
            id, idempotencyKey, runtime, commandLog,
            (commandId, closedAt) => service.CloseAsync(
                new CloseAccountCommand(id, DateOnly.FromDateTime(closedAt.UtcDateTime), request.ClosureReason),
                request.Actor ?? OperatorActor, closedAt, commandId, ct),
            request.ClosedAt, clock, ct);

    private static Task<IResult> ErasePersonalDataAsync(
        Guid id,
        ErasePersonalDataRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CurrentAccountLifecycleService service,
        AggregateRuntime<AccountPosition> runtime,
        ICommandLog commandLog,
        TimeProvider clock,
        CancellationToken ct)
        => RunIdempotentAsync(
            id, idempotencyKey, runtime, commandLog,
            (commandId, erasedAt) => service.ErasePersonalDataAsync(
                id, request.SubjectPseudonym, request.ErasureReason,
                request.Actor ?? DpoActor, erasedAt, commandId, ct),
            request.ErasedAt, clock, ct);

    private static async Task<IResult> GetAccountAsync(
        Guid id,
        AggregateRuntime<AccountPosition> runtime,
        AccountBalanceReader balances,
        CancellationToken ct)
    {
        var position = (await runtime.LoadAsync(id, ct)).State;
        if (position.Lifecycle == AccountLifecycle.Pending)
        {
            return Results.NotFound();
        }

        // The structural / lifecycle half comes from the folded position; the two balances and the active
        // holds are the SPINE-owned folds keyed by the account's opaque account_ref (ADR-PC-033) — the
        // family record carries neither. A just-opened account with no posted movements or holds reads
        // accounting = available = 0 with an empty hold set.
        var accountRef = position.AccountRef;
        var accountingCents = await balances.GetAccountingBalanceCentsAsync(accountRef, ct);
        var availableCents = await balances.GetAvailableBalanceCentsAsync(accountRef, ct);
        var holds = await balances.GetActiveHoldsAsync(accountRef, ct);

        return Results.Ok(new AccountResponse(
            AccountId: position.AccountId,
            ProductCode: position.ProductCode,
            Currency: position.Currency,
            OpenedOn: position.OpenedOn,
            Status: Status(position),
            AccountingBalanceCents: accountingCents,
            AvailableBalanceCents: availableCents,
            ActiveHolds: holds.Select(ToHoldView).ToList()));
    }

    /// <summary>
    /// The account movement-statement read (ADR-PC-032): the account's recorded movement lines in stable
    /// (stream, sequence, index) order — a read over the SPINE-owned movement ledger keyed by the account's
    /// opaque <c>account_ref</c> (the same fold <see cref="GetAccountAsync"/>'s accounting balance sums,
    /// here exposed as its lines). Mirrors the point-read: 404 while the account is still Pending (no
    /// AccountOpened appended yet) rather than a phantom empty statement; read-only and ungated. No PII
    /// (ADR-PC-004 §P2) — each line carries only structural closed-enum names + integer cents.
    /// </summary>
    private static async Task<IResult> GetMovementsAsync(
        Guid id,
        AggregateRuntime<AccountPosition> runtime,
        AccountBalanceReader balances,
        CancellationToken ct)
    {
        var position = (await runtime.LoadAsync(id, ct)).State;
        if (position.Lifecycle == AccountLifecycle.Pending)
        {
            return Results.NotFound();
        }

        // The movement lines are the SPINE-owned fold keyed by the account's opaque account_ref (ADR-PC-032)
        // — the family record carries no movements. A just-opened account with no posted movements reads an
        // empty statement.
        var statement = await balances.GetStatementAsync(position.AccountRef, ct);

        return Results.Ok(new MovementsResponse(
            AccountId: position.AccountId,
            Movements: statement.Select(ToMovementView).ToList()));
    }

    /// <summary>
    /// The synchronous authorize decision (ADR-PC-037 / ADR-PC-034): fold the available balance, apply
    /// the pack rules, and place a hold (authorized) or record a refusal fact (declined) — in real time,
    /// idempotently on the mandatory Idempotency-Key command id. A DECLINED verdict is a normal business
    /// outcome on the 200 body (the refusal is an appended auditable fact, not an HTTP error); only a bad
    /// key (400), a concurrency clash (409), and an illegal-from-lifecycle rejection (422) are errors. A
    /// replayed command id returns the ORIGINAL verdict (same hold_id) with no second HoldPlaced — the
    /// AUTHORIZATION_SYNC_IDEMPOTENT contract — reconstructed from the single appended event.
    /// </summary>
    private static async Task<IResult> AuthorizeAsync(
        Guid id,
        AuthorizeRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CurrentAccountAuthorizeService service,
        ICommandLog commandLog,
        TimeProvider clock,
        CancellationToken ct)
    {
        // The Idempotency-Key command id is MANDATORY on this money-mover (ADR-PC-029) — no silent one-shot.
        if (idempotencyKey is null || !Guid.TryParse(idempotencyKey, out var commandId))
        {
            return Results.Problem(
                "Idempotency-Key header is required and must be a UUID (ADR-PC-029).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // A non-positive debit is structurally not an authorization — reject the request before any read
        // or append, so it never becomes a business decline code.
        if (request.AmountCents <= 0)
        {
            return Results.Problem(
                "amount_cents must be a positive integer in cents (ADR-PC-010).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Pre-check BEFORE any side effect: a known command id replays the ORIGINAL verdict off the single
        // appended event, with no second decision and no second append (ADR-PC-029). The crash-atomic
        // guarantee is the in-transaction command_dedup below; this read keeps the common retry off the
        // write path.
        var receipt = await commandLog.TryGetAsync(commandId, ct);
        if (receipt is not null)
        {
            return Results.Ok(await service.ReconstructVerdictAsync(id, receipt.CommitSequence, ct));
        }

        // The host owns the wall clock at this boundary (ADR-PC-010): the value_date is the caller's
        // economic date; validTime is the envelope's stamped instant. The decider reads neither a clock.
        var command = new AuthorizeAccountCommand(
            id, request.AmountCents, request.ValueDate, request.Actor ?? AuthorizeActor, commandId);

        try
        {
            return Results.Ok(await service.AuthorizeAsync(command, clock.GetUtcNow(), ct));
        }
        catch (DuplicateCommandException e)
        {
            // A concurrent duplicate slipped past the pre-check: the in-transaction dedup rolled the append
            // back. Reconstruct the ORIGINAL verdict off the winner's appended event (idempotent replay).
            return Results.Ok(await service.ReconstructVerdictAsync(id, e.CommitSequence, ct));
        }
        catch (ConcurrencyException)
        {
            return Results.Problem($"Account {id} was modified concurrently.", statusCode: StatusCodes.Status409Conflict);
        }
        catch (DomainRejectedException e)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    /// <summary>
    /// The settlement CREDIT-receive command (ADR-PC-043): land
    /// a Credit into the account, admitting an Active/Dormant account (a Dormant one reactivates + credits in
    /// one atomic batch) and refusing a Closed/Erased one by construction. The append command_id is derived
    /// from the BODY's economic-intent reference (NOT the HTTP Idempotency-Key — the scoped ADR-PC-029
    /// carve-out), so a saga reissue with a byte-identical body collapses to ONE append at command_dedup. A
    /// rejected admission (Closed → ACCOUNT_CLOSED, Erased → ACCOUNT_ERASED) is a 422 — a 4xx the dispatcher
    /// classifies as needing the source to hold the funds (ADR-PC-043),
    /// NEVER a 200-with-Declined it would march to COMPLETED with zero landing.
    /// </summary>
    private static async Task<IResult> ReceiveCreditAsync(
        Guid id,
        ReceiveCreditRequest request,
        CurrentAccountCreditReceiveService service,
        AggregateRuntime<AccountPosition> runtime,
        ICommandLog commandLog,
        TimeProvider clock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The exactly-once key is the BODY's economic-intent reference, NOT the HTTP Idempotency-Key
        // (ADR-PC-043 — the scoped ADR-PC-029 inversion). It is MANDATORY: a settlement credit
        // has no fall-back key (the credit path rests SOLELY on command_dedup, single-guarded).
        if (string.IsNullOrWhiteSpace(request.IntentReference))
        {
            return Results.Problem(
                "intent_reference is required — the settlement credit's append command_id derives from it (ADR-PC-043).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.AmountCents <= 0)
        {
            return Results.Problem(
                "amount_cents must be a positive integer in cents (ADR-PC-010).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Derive the append command_id from the intent reference (ADR-PC-043 slot 4) — the same reference
        // always yields the same id, so a byte-identical reissue collapses to one append.
        var commandId = SettlementIntentKey.Derive(request.IntentReference);

        // Pre-check BEFORE any side effect: a known command id replays the ORIGINAL outcome with NO second
        // append (the crash-atomic guarantee is the in-transaction command_dedup; this read keeps the common
        // saga reissue off the write path).
        var receipt = await commandLog.TryGetAsync(commandId, ct);
        if (receipt is not null)
        {
            var replay = await runtime.LoadAsync(id, ct);
            return Results.Ok(new SettlementApplyResponse(id, Status(replay.State), receipt.CommitSequence));
        }

        // The value_date is the caller's economic date; validTime is the envelope's stamped instant (the host
        // owns the wall clock at this boundary, ADR-PC-010). The decider reads neither a clock.
        var command = new ReceiveCreditCommand(
            id, request.AmountCents, request.ValueDate, request.IntentReference,
            request.Actor ?? SettlementActor, commandId);

        long commitSequence;
        try
        {
            commitSequence = await service.ReceiveCreditAsync(command, clock.GetUtcNow(), ct);
        }
        catch (DuplicateCommandException)
        {
            // A concurrent duplicate slipped past the pre-check: the in-transaction dedup rolled the append
            // back. Return the ORIGINAL outcome off the authoritative fold (idempotent replay, ADR-PC-029).
            var replay = await runtime.LoadAsync(id, ct);
            return Results.Ok(new SettlementApplyResponse(id, Status(replay.State), replay.Version));
        }
        catch (DomainRejectedException e)
        {
            // A non-admitting account (Closed → ACCOUNT_CLOSED, Erased → ACCOUNT_ERASED) or a non-positive
            // amount — a 4xx, never a silent append (ADR-PC-043, the SETTLEMENT_CA_DECLINE_IS_4XX contract).
            // This is the settlement-facing decline shape the dispatcher classifies as a terminal Refused
            // (the outbox row flips FAILED) — DISTINCT from the customer authorize endpoint's 200-with-Declined
            // body, which the dispatcher would mis-read as Applied and march to COMPLETED with zero landing.
            // The source holds the funds.
            return Results.Problem(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var hydrated = await runtime.LoadAsync(id, ct);
        return Results.Ok(new SettlementApplyResponse(id, Status(hydrated.State), commitSequence));
    }

    /// <summary>
    /// The settlement CAPTURE command (ADR-PC-043 / ADR-PC-037): turn an authorize reservation into a real Debit — append the spine <c>HoldCaptured</c> (the
    /// earmark release, REUSED) + the family <c>AccountDebited</c> (the Debit Movement) in ONE append. The
    /// append command_id is derived from the BODY's economic-intent reference (NOT the HTTP Idempotency-Key),
    /// so a saga reissue collapses to ONE Debit at command_dedup; the capture also applies only WHERE the hold
    /// state is ACTIVE (the double guard). A capture whose target_hold_id names no active hold is a 422
    /// (ADR-PC-043; pinned by CurrentAccountCaptureTests). A partial / over-capture (ADR-PC-037) is surfaced on the response.
    /// </summary>
    private static async Task<IResult> CaptureAsync(
        Guid id,
        CaptureAccountRequest request,
        CurrentAccountCaptureService service,
        AggregateRuntime<AccountPosition> runtime,
        ICommandLog commandLog,
        TimeProvider clock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.IntentReference))
        {
            return Results.Problem(
                "intent_reference is required — the settlement capture's append command_id derives from it (ADR-PC-043).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(request.TargetHoldId))
        {
            return Results.Problem(
                "target_hold_id is required — it must match the authorize's hold (ADR-PC-043).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.AmountCents <= 0)
        {
            return Results.Problem(
                "amount_cents must be a positive integer in cents (ADR-PC-010).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // The exactly-once key is the intent reference, NOT the HTTP Idempotency-Key (ADR-PC-043 slot 4).
        var commandId = SettlementIntentKey.Derive(request.IntentReference);

        // Pre-check: a known command id replays the original commit with no second append (the second guard,
        // command_dedup — the first is the capture's WHERE state='ACTIVE'). A reissue lands EXACTLY ONE Debit.
        var receipt = await commandLog.TryGetAsync(commandId, ct);
        if (receipt is not null)
        {
            var replay = await runtime.LoadAsync(id, ct);
            return Results.Ok(new SettlementApplyResponse(id, Status(replay.State), receipt.CommitSequence));
        }

        var command = new CaptureAccountCommand(
            id, request.TargetHoldId, request.AmountCents, request.ValueDate, request.IntentReference,
            request.Actor ?? SettlementActor, commandId);

        CaptureOutcome outcome;
        try
        {
            outcome = await service.CaptureAsync(command, clock.GetUtcNow(), ct);
        }
        catch (DuplicateCommandException)
        {
            var replay = await runtime.LoadAsync(id, ct);
            return Results.Ok(new SettlementApplyResponse(id, Status(replay.State), replay.Version));
        }
        catch (ConcurrencyException)
        {
            return Results.Problem($"Account {id} was modified concurrently.", statusCode: StatusCodes.Status409Conflict);
        }
        catch (DomainRejectedException e)
        {
            // A hold-match failure (the target names no active authorization hold — a declined / frozen /
            // insufficient reserve on the debit path) or a non-positive amount — a 4xx, never a phantom debit
            // (ADR-PC-043 §5, the SETTLEMENT_CA_DECLINE_IS_4XX contract; the hold match is pinned by
            // CurrentAccountCaptureTests). The dispatcher classifies this 4xx as a Refused → ReserveRefused →
            // HIR park, never a 200-with-Declined it would march to COMPLETED with zero landing.
            return Results.Problem(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var hydrated = await runtime.LoadAsync(id, ct);
        return Results.Ok(new SettlementApplyResponse(
            id, Status(hydrated.State), outcome.CommitSequence, outcome.Reconciliation));
    }

    /// <summary>
    /// The projection-derived hold-expiry command (ADR-PC-037): append a <c>HoldExpired</c> for a hold
    /// the ADR-PC-036 lifecycle-command driver found due against a value-date horizon. It reuses the shared
    /// idempotent choreography — a HoldExpired moves no money (a posting-free release, ADR-PC-037), so
    /// it is ungated exactly like the lifecycle transitions, and the mandatory Idempotency-Key (the driver's
    /// canonical dispatch id) makes an at-least-once re-POST replay the original outcome rather than re-expire.
    /// The business valid_time is the hold's economic value-date, not the wall-clock tick the driver fired on,
    /// so a late/backfilled expiry records the correct economic date (ADR-PC-002 / ADR-PC-023).
    /// </summary>
    private static Task<IResult> ExpireHoldAsync(
        Guid id,
        string holdId,
        ExpireHoldRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CurrentAccountHoldExpiryService service,
        AggregateRuntime<AccountPosition> runtime,
        ICommandLog commandLog,
        TimeProvider clock,
        CancellationToken ct)
        => RunIdempotentAsync(
            id, idempotencyKey, runtime, commandLog,
            (commandId, validTime) => service.ExpireHoldAsync(
                new ExpireHoldCommand(id, holdId, request.ValueDate, request.Actor ?? ExpiryActor, commandId),
                validTime, ct),
            // The business valid_time is the hold's economic value-date (a HoldExpired is dated by when the
            // hold was due to expire, ADR-PC-023), passed as the override so the shared choreography stamps it.
            new DateTimeOffset(request.ValueDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            clock, ct);

    /// <summary>
    /// The projection-derived overdraft-interest accrual command (ADR-PC-037): append an
    /// <c>OverdraftInterestAccrued</c> for an account the ADR-PC-036 lifecycle-command driver found drawn below
    /// zero as-of a value-date. It reuses the shared idempotent choreography — the accrual posts a fee Movement
    /// but it is an INTERNAL ledger charge (an Observed engine-internal-already-effected Movement, ADR-PC-043 —
    /// no external counterparty, no cash leg to settle), so it is ungated like the hold-expiry release, and the
    /// mandatory Idempotency-Key (the driver's canonical dispatch id)
    /// makes an at-least-once re-POST replay the original outcome rather than re-accrue — one accrual per
    /// account per day. A no-applicable-accrual outcome (not Active / not drawn / no overdraft rate / a
    /// zero-rounding fee) appends nothing and returns the current head; a rate-declaring account whose sheet is
    /// undeployed throws (→ 422) so the driver retries. The business valid_time is the accrual's economic
    /// value-date, not the wall-clock tick the driver fired on (ADR-PC-002 / ADR-PC-023).
    /// </summary>
    private static Task<IResult> AccrueOverdraftInterestAsync(
        Guid id,
        OverdraftAccrualRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CurrentAccountOverdraftAccrualService service,
        AggregateRuntime<AccountPosition> runtime,
        ICommandLog commandLog,
        TimeProvider clock,
        CancellationToken ct)
        => RunIdempotentAsync(
            id, idempotencyKey, runtime, commandLog,
            (commandId, validTime) => service.AccrueOverdraftInterestAsync(
                new OverdraftAccrualCommand(id, request.AccrualDate, request.Actor ?? AccrualActor, commandId),
                validTime, ct),
            // The business valid_time is the accrual's economic value-date (the day the driver is accruing for,
            // ADR-PC-023), passed as the override so the shared choreography stamps it — a late/backfilled
            // accrual records the correct economic date.
            new DateTimeOffset(request.AccrualDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            clock, ct);

    /// <summary>
    /// The shared idempotent lifecycle-command choreography (ADR-PC-029), mirroring the deposit / loan
    /// money-mover endpoints: require a UUID Idempotency-Key, pre-check the command log for an
    /// already-applied id (replay the original outcome with no second append), stamp a missing valid-time
    /// from the host clock, invoke the service, and map the domain exceptions to HTTP — a concurrent
    /// duplicate to the replayed outcome, a concurrency clash to 409, and a lifecycle / domain rejection
    /// to 422.
    /// </summary>
    private static async Task<IResult> RunIdempotentAsync(
        Guid id,
        string? idempotencyKey,
        AggregateRuntime<AccountPosition> runtime,
        ICommandLog commandLog,
        Func<Guid, DateTimeOffset, Task<long>> apply,
        DateTimeOffset? validTimeOverride,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (idempotencyKey is null || !Guid.TryParse(idempotencyKey, out var commandId))
        {
            return Results.Problem(
                "Idempotency-Key header is required and must be a UUID (ADR-PC-029).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Pre-check BEFORE any side effect: a known command id replays the ORIGINAL outcome with NO second
        // append (the crash-atomic guarantee is the in-transaction command_dedup INSERT; this read keeps
        // the common sequential retry off the write path).
        var receipt = await commandLog.TryGetAsync(commandId, ct);
        if (receipt is not null)
        {
            var replay = await runtime.LoadAsync(id, ct);
            return Results.Ok(new AccountCommandResponse(id, Status(replay.State), receipt.CommitSequence));
        }

        var validTime = validTimeOverride ?? clock.GetUtcNow();

        long commitSequence;
        try
        {
            commitSequence = await apply(commandId, validTime);
        }
        catch (DuplicateCommandException)
        {
            // A concurrent duplicate slipped past the pre-check: the in-transaction dedup rolled the append
            // back. Return the ORIGINAL outcome off the authoritative fold (idempotent replay, ADR-PC-029).
            var replay = await runtime.LoadAsync(id, ct);
            return Results.Ok(new AccountCommandResponse(id, Status(replay.State), replay.Version));
        }
        catch (ConcurrencyException)
        {
            return Results.Problem($"Account {id} was modified concurrently.", statusCode: StatusCodes.Status409Conflict);
        }
        catch (DomainRejectedException e)
        {
            // An illegal lifecycle transition (e.g. closing a Dormant account) — surface as a 422, never
            // append on a silent default. Wiring faults throw other types and propagate as a 500.
            return Results.Problem(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var hydrated = await runtime.LoadAsync(id, ct);
        return Results.Ok(new AccountCommandResponse(id, Status(hydrated.State), commitSequence));
    }

    private static AccountHoldView ToHoldView(Hold hold) => new(
        HoldId: hold.HoldId,
        AmountCents: hold.Amount.Cents,
        ValueDate: hold.ValueDate,
        State: hold.State.ToString(),
        Kind: hold.Kind.ToString(),
        LegalReference: hold.LegalReference,
        ExpiresAt: hold.ExpiresAt);

    // Map a spine movement-ledger line to the read view — STRUCTURAL columns only. Direction / Operation /
    // Origin are already closed-enum member NAMES on the ledger entry (the storage boundary stores the
    // primitives, not the family enums), so they surface verbatim; AmountCents / ValueDate are the
    // integer-cents amount and economic date. No stream_id / sequence_number / command_id (internal
    // idempotency plumbing) and no free-text detail leak onto the wire — no PII (ADR-PC-004 §P2).
    private static MovementView ToMovementView(MovementLedgerEntry entry) => new(
        Direction: entry.Direction,
        AmountCents: entry.AmountCents,
        ValueDate: entry.ValueDate,
        Operation: entry.Operation,
        Origin: entry.Origin);

    private static string Status(AccountPosition position) =>
        position.Lifecycle.ToString().ToUpperInvariant();
}
