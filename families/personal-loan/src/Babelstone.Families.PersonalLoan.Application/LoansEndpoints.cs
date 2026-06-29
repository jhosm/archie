using Babelstone.Engine;
using Babelstone.Engine.Hosting;
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
/// (ADR-PC-010 §P5). The money-movers are idempotent (ADR-PC-029 slot 4): an at-least-once retry replays the
/// original outcome rather than moving money twice. The <b>installment</b> path's key is SERVER-DERIVED and
/// number-pinned (ADR-PC-036 §Decision 1+3, LCD-1) — no caller key; the other money-movers (early-repayment /
/// write-off / erase) keep a mandatory caller-supplied <c>Idempotency-Key</c>. NO eager settlement on
/// any path — each money-moving event records its leg APPEND-FIRST as a Movement for the substrate-owned
/// settlement saga to effect, gated (ADR-PC-032 slot 5).
/// <para>
/// STEP-UP SCA on the irreversible money-movers (ADR-IC-010 §P8 / the <c>MCP_SCA_GATE_CANNOT_BYPASS</c>
/// commitment; bd babelstone-6cpq.14). <c>/installment</c> and <c>/early-repayment</c> sit behind the SHARED
/// <see cref="ScaPreconditionFilter"/> (relocated to <c>Babelstone.Engine.Hosting</c>, ADR-PC-021 §A9) on a
/// money-mover route group, exactly as <c>DepositsEndpoints</c> gates maturity / interest / terminate. The
/// filter fail-closes a money-mover with no fresh gateway-attested SCA <c>422 SCA_REQUIRED</c> BEFORE any
/// side effect, so an agent cannot collect an irreversible loan leg on its own word. <c>/installment</c> (a
/// clock-driven money-mover) also accepts the ADR-PC-036 scoped service principal; <c>/early-repayment</c>
/// (customer-initiated) is human-SCA-only. <c>/disburse</c>, <c>/write-off</c>, <c>/erase-personal-data</c>
/// and the read surface are ungated (see <see cref="Map"/>).
/// </para>
/// </summary>
public static class LoansEndpoints
{
    private const string OperatorActor = "ops:loan-officer";
    private const string DpoActor = "ops:dpo";

    // The stable command-kind code the installment idempotency key is derived under (ADR-PC-036 §Decision
    // 1+3, LCD-1) — never caller input, so the operator, the MCP agent, and the automated driver all
    // converge on the same number-pinned key for the same occurrence.
    private const string PayInstallmentCommandKind = "pay_installment";

    public static void Map(IEndpointRouteBuilder app)
    {
        // Disburse a new loan (opens the stream). The Idempotency-Key is OPTIONAL here (the new-stream
        // append is naturally a one-shot), but honoured when supplied so a retry of the SAME disbursement
        // dedupes (ADR-PC-029 slot 4). NOT step-up-SCA-gated: disbursement of an already-approved,
        // already-priced loan is an origination-side action (ADR-PC-030 / ADR-PC-024), not an agent-triggered
        // collection from the customer, so it stays on `app` outside the money-mover route group.
        app.MapPost("/v1/loans", DisburseAsync);

        // The irreversible money-movers carry the step-up-SCA gate as a ROUTE-GROUP property, not per-handler
        // boilerplate, exactly mirroring DepositsEndpoints: the SHARED ScaPreconditionFilter (relocated to
        // Babelstone.Engine.Hosting, ADR-PC-021 §A9 — ONE gate mechanism both families reference, never a
        // per-family copy) runs in the impure host shell BEFORE the handler (so before any side effect) and
        // authorises one of two ways —
        //   (1) HUMAN step-up: the gateway-attested X-SCA-Acr/X-SCA-Auth-Time fresh SCA proof (the MCP agent /
        //       customer flow), short-circuiting 422 SCA_REQUIRED on absent/stale proof — this is what makes
        //       the MCP_SCA_GATE_CANNOT_BYPASS commitment genuinely cover loans (ADR-IC-010 §P8; ADR-PC-010
        //       §P5 — the pure decider never sees the check); or
        //   (2) the NON-INTERACTIVE scoped service principal (ADR-PC-036, bd babelstone-6cpq.9/.14): the
        //       lifecycle-command driver firing the installment on its due date has no human acr, so a SCOPED
        //       gateway-attested X-SCA-Service-Principal claim authorises it — ROUTE-SCOPED to /installment
        //       only among the loan routes (ScaServicePrincipal.AuthorisedOperations), audited, never blanket.
        // /installment is a CLOCK-DRIVEN money-mover (it collects the scheduled installment from the
        // customer), so it is gated AND in the scoped principal's allowance — both the lifecycle driver and an
        // MCP agent with fresh human SCA can pay it. /early-repayment is ALSO an irreversible money-mover (it
        // collects a prepayment + commission), so it sits in the SAME gated group, but HUMAN-SCA ONLY: it is
        // CUSTOMER-INITIATED (not in AuthorisedOperations), the loan analogue of the deposit /terminate, so
        // the automated driver can never repay a loan early. Anything mapped on this group is gated by
        // construction, so the loan money-movers can't drift out of SCA parity with the deposit ones; the
        // ungated siblings below stay on `app`. The installment path's idempotency key is still SERVER-DERIVED
        // and number-pinned (ADR-PC-036 §Decision 1+3, LCD-1) — no caller Idempotency-Key — and early
        // repayment keeps its mandatory caller-supplied Idempotency-Key (ADR-PC-029 slot 4); SCA is a SEPARATE
        // axis layered in front of both (a money-mover needs BOTH a valid idempotency contract AND fresh SCA).
        var moneyMovers = app.MapGroup("/v1/loans/{id:guid}")
            .AddEndpointFilter<ScaPreconditionFilter>();
        moneyMovers.MapPost("/installment", PayInstallmentAsync);
        moneyMovers.MapPost("/early-repayment", RepayEarlyAsync);

        // Write-off recognises a loss — NO money moves (it is an operator-recorded accounting fact, not a
        // money-mover — ADR-PC-030 §P1 / the WriteOffLoanRequest contract), so it is NOT step-up-SCA-gated:
        // the same posture as the deposit operator-only /correction (also ungated). erase-personal-data is a
        // GDPR Article 17 surface governed by its OWN crypto-shred discipline (ADR-PC-004 §P3), a DIFFERENT
        // gate, not the money-mover SCA gate. Both stay on `app` and keep their mandatory Idempotency-Key
        // (ADR-PC-029 slot 4).
        app.MapPost("/v1/loans/{id:guid}/write-off", WriteOffAsync);
        app.MapPost("/v1/loans/{id:guid}/erase-personal-data", ErasePersonalDataAsync);

        // The query surface: ONE canonical loan resource, folded from the event stream (the bitemporal
        // projections suffice — no denormalized read-model table, ADR-PC-002 §P2). Read-only, ungated.
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

    /// <summary>
    /// Pay one scheduled installment — the SERVER-DERIVED, number-pinned idempotent money-mover
    /// (ADR-PC-036 §Decision 1+3, LCD-1; the loan half of the lifecycle-command driver's Layer-1
    /// safe-trigger foundation). Unlike the other money-movers, this path takes NO caller-supplied
    /// <c>Idempotency-Key</c>: the key is derived HERE from the occurrence's own identity —
    /// <c>(loan, "pay_installment", installment-number)</c> — so a manual operator, the MCP agent, and the
    /// automated driver paying the SAME installment all converge on the SAME key and dedupe to ONE money
    /// leg at <c>command_dedup</c> (ADR-PC-029 slot 4, AMENDED: the installment key's provenance inverts
    /// from caller-supplied to server-derived). The installment NUMBER is the stable occurrence key — never
    /// the <c>PaidAt</c> due-date — so a re-dated or backfilled retry of occurrence N is swallowed
    /// (number-pinned, ADR-PC-036 §Decision 3; safe only while ADR-PC-031 forbids re-amortization).
    /// <c>PayInstallment</c> is legal repeatedly from <c>Active</c>, so the legality gate gives no backstop —
    /// the number-pinned key is the only guard against a double-collection.
    /// </summary>
    private static async Task<IResult> PayInstallmentAsync(
        Guid id,
        PayInstallmentRequest request,
        PersonalLoanConstitutionService service,
        AggregateRuntime<LoanPosition> runtime,
        ICommandLog commandLog,
        TimeProvider clock,
        CancellationToken ct)
    {
        // Load the AUTHORITATIVE fold and derive the next stable installment number (InstallmentsPaid + 1;
        // the final-installment pairing is decided downstream in PersonalLoanConstitutionService). The
        // occurrence key is that NUMBER, never the due-date — so the derived command id is identical across
        // a re-dated retry of the same occurrence (LCD-1).
        var hydrated = await runtime.LoadAsync(id, ct);
        var installmentNumber = hydrated.State.InstallmentsPaid + 1;
        var commandId = LifecycleCommandKey.Derive(id, PayInstallmentCommandKind, installmentNumber);

        // Pre-check BEFORE any side effect: a known command id replays the ORIGINAL outcome with NO second
        // append. The crash-atomic guarantee is the in-transaction command_dedup INSERT below; this read just
        // keeps the common sequential retry off the write path (mirrors RunIdempotentAsync's pre-check).
        var receipt = await commandLog.TryGetAsync(commandId, ct);
        if (receipt is not null)
        {
            var replay = await runtime.LoadAsync(id, ct);
            return Results.Ok(new LoanCommandResponse(id, Status(replay.State), receipt.CommitSequence));
        }

        long commitSequence;
        try
        {
            commitSequence = await service.PayInstallmentAsync(
                new PayInstallmentCommand(
                    id, request.PaidAt ?? clock.GetUtcNow(), request.CollectionAccountRef,
                    request.Actor ?? OperatorActor, commandId),
                ct);
        }
        catch (DuplicateCommandException)
        {
            // A concurrent duplicate of the SAME occurrence slipped past the pre-check: the in-transaction
            // command_dedup INSERT rolled the append back. Return the ORIGINAL outcome off the authoritative
            // fold (the idempotent replay slot 4 mandates).
            var replay = await runtime.LoadAsync(id, ct);
            return Results.Ok(new LoanCommandResponse(id, Status(replay.State), replay.Version));
        }
        catch (ConcurrencyException)
        {
            // A concurrent writer reached the head between our load and our append. If that winner was a
            // concurrent firing of THIS SAME occurrence (its number-pinned key now exists), the intended
            // effect already happened — replay its outcome rather than surface a spurious 409. Otherwise it
            // is a genuine clash on a DIFFERENT command — surface 409.
            var raced = await commandLog.TryGetAsync(commandId, ct);
            if (raced is not null)
            {
                var replay = await runtime.LoadAsync(id, ct);
                return Results.Ok(new LoanCommandResponse(id, Status(replay.State), raced.CommitSequence));
            }

            return Results.Problem($"Loan {id} was modified concurrently.", statusCode: StatusCodes.Status409Conflict);
        }
        catch (DomainRejectedException e)
        {
            // An illegal lifecycle transition (e.g. paying an installment on a settled loan) — surface a 422,
            // never an append on a silent default. Wiring faults throw other types and propagate as a 500.
            return Results.Problem(e.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var hydratedAfter = await runtime.LoadAsync(id, ct);
        return Results.Ok(new LoanCommandResponse(id, Status(hydratedAfter.State), commitSequence));
    }

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
