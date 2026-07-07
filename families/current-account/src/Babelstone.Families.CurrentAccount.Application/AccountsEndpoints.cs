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
/// NONE of these routes is step-up-SCA-gated: the lifecycle transitions (open / mark-dormant / reactivate
/// / close) move no money — they only relabel the account's lifecycle state — so unlike the deposit /
/// loan money-movers there is no irreversible cash leg to gate. The synchronous AUTHORIZE money-mover is
/// a separate authorize surface on the ADR-PC-034 technique, not mapped here. The lifecycle commands stay
/// idempotent (ADR-PC-029): an at-least-once retry replays the original outcome rather than re-applying
/// the transition.
/// </para>
/// </summary>
public static class AccountsEndpoints
{
    private const string OperatorActor = "ops:account-officer";
    private const string DpoActor = "ops:dpo";

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

        // GDPR Article 17 right-to-be-forgotten (ADR-PC-004) — a DIFFERENT gate from the money-mover SCA
        // gate; governed by its own crypto-shred discipline. Mandatory Idempotency-Key (key destruction
        // is irreversible).
        app.MapPost("/v1/accounts/{id:guid}/erase-personal-data", ErasePersonalDataAsync);

        // The query surface: ONE canonical account resource, the structural half folded from the stream +
        // the two balances and active holds read from the spine-owned folds (ADR-PC-033). Read-only, ungated.
        app.MapGet("/v1/accounts/{id:guid}", GetAccountAsync);
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

    private static string Status(AccountPosition position) =>
        position.Lifecycle.ToString().ToUpperInvariant();
}
