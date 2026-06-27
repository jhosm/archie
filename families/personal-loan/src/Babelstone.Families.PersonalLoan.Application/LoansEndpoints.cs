using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.Families.PersonalLoan;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Babelstone.Families.PersonalLoan.Application;

/// <summary>
/// The personal_loan command/query endpoints (ADR-PC-021 §D5; bd babelstone-9g77) — the closed-end-asset
/// sibling of <c>DepositsEndpoints</c>. Mirrors the term-deposit surface shape: a thin HTTP front door over
/// the pure decider via <see cref="PersonalLoanConstitutionService"/>, with the host owning the wall clock
/// at the boundary (it stamps a missing disbursed_at / paid_at / repaid_at) so the decider stays pure
/// (ADR-PC-010 §P5). The money-movers carry a mandatory <c>Idempotency-Key</c> (ADR-PC-029 slot 4): an
/// at-least-once retry replays the original outcome rather than moving money twice. NO eager settlement on
/// any path — each money-moving event records its leg APPEND-FIRST as a Movement for the substrate-owned
/// settlement saga to effect, gated (ADR-PC-032 slot 5).
/// </summary>
public static class LoansEndpoints
{
    private const string OperatorActor = "ops:loan-officer";
    private const string DpoActor = "ops:dpo";

    public static void Map(IEndpointRouteBuilder app)
    {
        // Disburse a new loan (opens the stream). The Idempotency-Key is OPTIONAL here (the new-stream
        // append is naturally a one-shot), but honoured when supplied so a retry of the SAME disbursement
        // dedupes (ADR-PC-029 slot 4).
        app.MapPost("/v1/loans", DisburseAsync);

        // The amortizing money-movers: each carries a mandatory Idempotency-Key (ADR-PC-029 slot 4).
        app.MapPost("/v1/loans/{id:guid}/installment", PayInstallmentAsync);
        app.MapPost("/v1/loans/{id:guid}/early-repayment", RepayEarlyAsync);

        // Write-off recognises a loss (no money moves); erase-personal-data records the GDPR Article 17 fact
        // after the host crypto-shredded the key. Both carry a mandatory Idempotency-Key (ADR-PC-029 slot 4).
        app.MapPost("/v1/loans/{id:guid}/write-off", WriteOffAsync);
        app.MapPost("/v1/loans/{id:guid}/erase-personal-data", ErasePersonalDataAsync);

        // The query surface: ONE canonical loan resource, folded from the event stream (the bitemporal
        // projections suffice — no denormalized read-model table, ADR-PC-002 §P2). Read-only.
        app.MapGet("/v1/loans/{id:guid}", GetLoanAsync);
    }

    private static async Task<IResult> DisburseAsync(
        DisburseLoanRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        PersonalLoanConstitutionService service,
        AggregateRuntime<LoanPosition> runtime,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (idempotencyKey is not null && !Guid.TryParse(idempotencyKey, out _))
        {
            return Results.Problem(
                "Idempotency-Key, when present, must be a UUID (ADR-PC-029 slot 4).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var commandId = idempotencyKey is null ? (Guid?)null : Guid.Parse(idempotencyKey);
        // The host owns the wall clock at this boundary (ADR-PC-010 §P5): stamp a missing disbursed_at, pass
        // it as an INPUT to the pure decider — there is no clock read in the decision.
        var command = new DisburseLoanCommand(
            LoanId: request.LoanId,
            PrincipalCents: request.PrincipalCents,
            ProductId: request.ProductId,
            Role: request.Role,
            TermMonths: request.TermMonths,
            StartDate: request.StartDate,
            DisbursedAt: request.DisbursedAt ?? clock.GetUtcNow(),
            Purpose: request.Purpose,
            DisbursementAccountRef: request.DisbursementAccountRef,
            Actor: request.Actor ?? OperatorActor,
            EarlyRepaymentCommissionBps: request.EarlyRepaymentCommissionBps,
            CommandId: commandId);

        long commitSequence;
        try
        {
            commitSequence = await service.DisburseAsync(command, ct);
        }
        catch (DuplicateCommandException)
        {
            var replay = await runtime.LoadAsync(request.LoanId, ct);
            return Results.Ok(new LoanCommandResponse(request.LoanId, Status(replay.State), replay.Version));
        }
        catch (DomainRejectedException e)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var hydrated = await runtime.LoadAsync(request.LoanId, ct);
        // A commercial-eligibility refusal appended LoanDisbursementFailed (the loan folds to Failed): no
        // loan was opened, so surface a 422 rather than a 201 even though the refusal fact is durably
        // recorded (ADR-PC-024 §5 — a refusal, not a compensation).
        if (hydrated.State.Lifecycle == LoanLifecycle.Failed)
        {
            return Results.Problem(
                $"Loan {request.LoanId} disbursement was refused (commercial-eligibility preconditions not met).",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        return Results.Created(
            $"/v1/loans/{request.LoanId}",
            new LoanCommandResponse(request.LoanId, Status(hydrated.State), commitSequence));
    }

    private static Task<IResult> PayInstallmentAsync(
        Guid id,
        PayInstallmentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        PersonalLoanConstitutionService service,
        AggregateRuntime<LoanPosition> runtime,
        ICommandLog commandLog,
        TimeProvider clock,
        CancellationToken ct)
        => RunIdempotentAsync(
            id, idempotencyKey, runtime, commandLog,
            commandId => service.PayInstallmentAsync(
                new PayInstallmentCommand(
                    id, request.PaidAt ?? clock.GetUtcNow(), request.CollectionAccountRef,
                    request.Actor ?? OperatorActor, commandId),
                ct),
            ct);

    private static Task<IResult> RepayEarlyAsync(
        Guid id,
        RepayEarlyRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        PersonalLoanConstitutionService service,
        AggregateRuntime<LoanPosition> runtime,
        ICommandLog commandLog,
        TimeProvider clock,
        CancellationToken ct)
        => RunIdempotentAsync(
            id, idempotencyKey, runtime, commandLog,
            commandId => service.RepayEarlyAsync(
                new RepayEarlyCommand(
                    id, request.RepaymentAmountCents, request.RepaidAt ?? clock.GetUtcNow(),
                    request.RepaymentAccountRef, request.Actor ?? OperatorActor, commandId),
                ct),
            ct);

    private static Task<IResult> WriteOffAsync(
        Guid id,
        WriteOffLoanRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        PersonalLoanConstitutionService service,
        AggregateRuntime<LoanPosition> runtime,
        ICommandLog commandLog,
        TimeProvider clock,
        CancellationToken ct)
        => RunIdempotentAsync(
            id, idempotencyKey, runtime, commandLog,
            commandId => service.WriteOffAsync(
                new WriteOffLoanCommand(
                    id, request.WrittenOffAt ?? clock.GetUtcNow(), request.WriteOffReason,
                    request.Actor ?? OperatorActor, commandId),
                ct),
            ct);

    private static Task<IResult> ErasePersonalDataAsync(
        Guid id,
        ErasePersonalDataRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        PersonalLoanConstitutionService service,
        AggregateRuntime<LoanPosition> runtime,
        ICommandLog commandLog,
        TimeProvider clock,
        CancellationToken ct)
        => RunIdempotentAsync(
            id, idempotencyKey, runtime, commandLog,
            commandId => service.ErasePersonalDataAsync(
                new ErasePersonalDataCommand(
                    id, request.SubjectPseudonym, request.ErasedAt ?? clock.GetUtcNow(),
                    request.ErasureReason, request.Actor ?? DpoActor, commandId),
                ct),
            ct);

    private static async Task<IResult> GetLoanAsync(
        Guid id, AggregateRuntime<LoanPosition> runtime, CancellationToken ct)
    {
        var position = (await runtime.LoadAsync(id, ct)).State;
        if (position.Lifecycle == LoanLifecycle.Pending)
        {
            return Results.NotFound();
        }

        return Results.Ok(new LoanResponse(
            LoanId: position.LoanId,
            PrincipalCents: position.Principal.Cents,
            TanBasisPoints: position.TanBasisPoints,
            RateSheetVersionId: position.RateSheetVersionId,
            TermMonths: position.TermMonths,
            InstallmentAmountCents: position.InstallmentAmount.Cents,
            StartDate: position.StartDate,
            Purpose: position.Purpose,
            ProductCode: position.ProductCode,
            OutstandingBalanceCents: position.OutstandingBalance.Cents,
            InstallmentsPaid: position.InstallmentsPaid,
            TotalInterestPaidCents: position.TotalInterestPaid.Cents,
            TotalCapitalRepaidCents: position.TotalCapitalRepaid.Cents,
            TotalCommissionChargedCents: position.TotalCommissionCharged.Cents,
            WrittenOffAmountCents: position.WrittenOffAmount.Cents,
            Status: Status(position)));
    }

    /// <summary>
    /// The shared idempotent money-mover choreography (ADR-PC-029 slot 4), mirroring the term-deposit
    /// money-mover endpoints: require a UUID Idempotency-Key, pre-check the command log for an
    /// already-applied id (replay the original outcome with no second append), invoke the service, and map
    /// the domain exceptions to HTTP — a concurrent duplicate to the replayed outcome, a concurrency clash
    /// to 409, and a lifecycle/domain rejection to 422.
    /// </summary>
    private static async Task<IResult> RunIdempotentAsync(
        Guid id,
        string? idempotencyKey,
        AggregateRuntime<LoanPosition> runtime,
        ICommandLog commandLog,
        Func<Guid, Task<long>> apply,
        CancellationToken ct)
    {
        if (idempotencyKey is null || !Guid.TryParse(idempotencyKey, out var commandId))
        {
            return Results.Problem(
                "Idempotency-Key header is required and must be a UUID (ADR-PC-029 slot 4).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Pre-check BEFORE any side effect: a known command id replays the ORIGINAL outcome with NO second
        // append (the crash-atomic guarantee is the in-transaction command_dedup INSERT; this read keeps the
        // common sequential retry off the write path).
        var receipt = await commandLog.TryGetAsync(commandId, ct);
        if (receipt is not null)
        {
            var replay = await runtime.LoadAsync(id, ct);
            return Results.Ok(new LoanCommandResponse(id, Status(replay.State), receipt.CommitSequence));
        }

        long commitSequence;
        try
        {
            commitSequence = await apply(commandId);
        }
        catch (DuplicateCommandException)
        {
            // A concurrent duplicate slipped past the pre-check: the in-transaction dedup rolled the append
            // back. Return the ORIGINAL outcome off the authoritative fold (the idempotent replay slot 4 mandates).
            var replay = await runtime.LoadAsync(id, ct);
            return Results.Ok(new LoanCommandResponse(id, Status(replay.State), replay.Version));
        }
        catch (ConcurrencyException)
        {
            return Results.Problem($"Loan {id} was modified concurrently.", statusCode: StatusCodes.Status409Conflict);
        }
        catch (DomainRejectedException e)
        {
            // An illegal lifecycle transition (e.g. paying an installment on a settled loan) — surface as a
            // 422, never append on a silent default. Wiring faults throw other types and propagate as a 500.
            return Results.Problem(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var hydrated = await runtime.LoadAsync(id, ct);
        return Results.Ok(new LoanCommandResponse(id, Status(hydrated.State), commitSequence));
    }

    private static string Status(LoanPosition position) =>
        position.Lifecycle.ToString().ToUpperInvariant();
}
