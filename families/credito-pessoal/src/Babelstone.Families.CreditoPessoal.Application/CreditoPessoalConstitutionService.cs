using Babelstone.Engine;
using Babelstone.FinancialTypes;
using Babelstone.Packs;
using Babelstone.RateSheets;

namespace Babelstone.Families.CreditoPessoal.Application;

/// <summary>
/// The credito_pessoal decider's impure orchestration (ADR-PC-021): it resolves the rate sheet and pack
/// primitives, calls the pure <see cref="CreditoPessoalDecider"/>, settles the money leg, and appends
/// through the runtime. It depends only on generic engine ports (<see cref="AggregateRuntime{TState}"/>,
/// <see cref="IRateSheetStore"/>, <see cref="ISettlementPort"/>) plus the pinned <see cref="VerifiedPack"/>
/// — the dependency arrow is family→engine, never the reverse (ADR-PC-021 §D2).
/// </summary>
/// <remarks>
/// The shared resolve→stamp→settle→append choreography is kept as separable steps so it can lift into a
/// generic ConstitutionPipeline on the second decider (ADR-PC-021 §P5, bd babelstone-osv6) — this IS that
/// second decider, so the disbursement path is written to mirror the term-deposit constitution path's
/// shape. ORIGINATION stays UPSTREAM (ADR-PC-030 / ADR-PC-024): this service disburses an already-approved,
/// already-priced loan; it never models solvency/CRC/scoring.
/// </remarks>
public sealed class CreditoPessoalConstitutionService(
    AggregateRuntime<LoanPosition> runtime,
    IRateSheetStore rateSheets,
    ISettlementPort settlement,
    VerifiedPack pack,
    IReadOnlyCollection<string>? requiredPreconditions = null)
{
    private static readonly CreditoPessoalFamilyModule Family = new();

    // The product's required commercial-eligibility preconditions (ADR-PC-024 §1). Engine-instance config
    // for the walking skeleton, mirroring the term-deposit stand-in. Defaults to empty — v1 launch products
    // are NOT eligibility-gated, so the common path never refuses on preconditions.
    private readonly IReadOnlyCollection<string> _requiredPreconditions =
        requiredPreconditions ?? Array.Empty<string>();

    /// <summary>
    /// Disburse a loan from the full command: resolve the active rate sheet, stamp the TAN + version id,
    /// compute the amortization schedule, DEBIT the lump sum to the borrower's account, and append
    /// <see cref="LoanDisbursed"/> as the stream's first event. The closed-end-asset analogue of the term
    /// deposit's constitution — but a loan pays OUT at t=0 (a disbursement DEBIT against the lender), where a
    /// deposit takes the principal IN.
    /// </summary>
    /// <returns>The new stream's head version (the read-your-writes token / commit_sequence).</returns>
    public async Task<long> DisburseAsync(DisburseLoanCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // 0. Commercial-eligibility gate (ADR-PC-024 §5): refuse BEFORE any rate-sheet resolve or the
        //    irreversible disbursement, as a PURE function of the command's resolved verdicts — no upstream
        //    call, no in-engine evaluation. A required precondition that is absent or satisfied:false yields
        //    LoanDisbursementFailed appended as the stream's first and only event; no loan is opened, so
        //    there is nothing to unwind (a refusal, not a compensation).
        var refusal = CreditoPessoalDecider.CheckPreconditions(
            command.LoanId, _requiredPreconditions, command.Preconditions);
        if (refusal is not null)
        {
            return await runtime.AppendAsync(
                command.LoanId, expectedVersion: -1, [refusal],
                Context(command.Actor, command.DisbursedAt, command.CommandId), ct);
        }

        // 1. Resolve the rate sheet active at disbursement (ADR-PC-008 §P3); fail loud if none.
        var resolution = await rateSheets.ResolveAsync(Family.FamilyName, command.DisbursedAt, ct)
            ?? throw new DomainRejectedException(
                $"No rate sheet effective for '{Family.FamilyName}' at {command.DisbursedAt:O}.");

        // 2. Resolve the TAN for (product, role, principal); a null on a deployed sheet means the pair is
        //    genuinely unpriced — fail loud rather than disburse at a silent zero rate.
        var tan = resolution.ResolveTanBasisPoints(command.ProductId, command.Role, command.PrincipalCents)
            ?? throw new DomainRejectedException(
                $"Rate sheet '{resolution.RateSheetVersionId}' does not price " +
                $"({command.ProductId}, {command.Role}) at {command.PrincipalCents}c.");

        // 3. Decide (pure): build the disbursement event, computing the French amortization schedule and
        //    stamping the resolved TAN + the version it came from + the periodic rate + the level installment.
        var disbursed = CreditoPessoalDecider.DecideDisbursement(command, tan, resolution.RateSheetVersionId);

        // 4. Settle (ADR-PC-016): DEBIT the lump sum out to the borrower's disbursement account. A loan pays
        //    out at constitution (the closed-end-asset's t=0 cash flow CF(0) = +C to the borrower / −C to the
        //    lender's funding), where a deposit takes the principal IN. The settlement port throws on a
        //    refused debit, so a disbursement never proceeds without its money leg.
        await settlement.SettleAsync(
            new SettlementInstruction(
                command.LoanId, SettlementDirection.Debit, disbursed.Principal,
                command.DisbursementAccount, "disbursement"),
            ct);

        // 5. Append the new stream (expectedVersion -1) — events + outbox in one transaction. The command id
        //    (when supplied) makes this append idempotent (ADR-PC-029 slot 4).
        return await runtime.AppendAsync(
            command.LoanId, expectedVersion: -1, [disbursed],
            Context(command.Actor, command.DisbursedAt, command.CommandId), ct);
    }

    /// <summary>
    /// Pay one scheduled installment: rehydrate the loan (must be Active), derive the next installment from
    /// the schedule, COLLECT its amount (settlement debit on the borrower / credit to the lender), and append
    /// the <see cref="LoanInstallmentPaid"/> — paired with a closing <see cref="LoanSettled"/> when it is the
    /// final installment (the balance reaches zero). Triggered MANUALLY here, as a deposit coupon is.
    /// </summary>
    public async Task<long> PayInstallmentAsync(PayInstallmentCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // 1. Rehydrate the disbursed position (load-then-append on the live stream head).
        var hydrated = await runtime.LoadAsync(command.LoanId, ct);
        var position = hydrated.State;

        // Transition-legality gate (the state machine): paying an installment is legal only from Active.
        RejectIfIllegal(position.Lifecycle, LifecycleTransitions.Transition.PayInstallment, command.LoanId, "pay installment");

        // 2. Decide (pure): the final installment pairs with a settlement; an intermediate one does not.
        var paidOn = DateOnly.FromDateTime(command.PaidAt.UtcDateTime);
        var isFinal = position.InstallmentsPaid + 1 >= position.TermMonths;
        var events = isFinal
            ? CreditoPessoalDecider.DecideFinalInstallment(position, paidOn)
            : CreditoPessoalDecider.DecideInstallment(position, paidOn);

        // 3. Settle (ADR-PC-016): collect the installment (interest + capital) from the borrower's account.
        var paid = events.OfType<LoanInstallmentPaid>().Single();
        var installmentTotal = paid.Interest + paid.Capital;
        await settlement.SettleAsync(
            new SettlementInstruction(
                command.LoanId, SettlementDirection.Credit, installmentTotal,
                command.CollectionAccount, "installment"),
            ct);

        // 4. Append at the current head (optimistic concurrency). The command id makes the append idempotent.
        return await runtime.AppendAsync(
            command.LoanId, hydrated.Version, events,
            Context(command.Actor, command.PaidAt, command.CommandId), ct);
    }

    /// <summary>
    /// Repay a loan early (<i>reembolso antecipado</i>, fin-math §7.5): rehydrate it (must be Active),
    /// resolve the capped commission by the remaining-term band, COLLECT the repaid capital + commission, and
    /// append the <see cref="LoanRepaidEarly"/> — paired with a closing <see cref="LoanSettled"/> for a FULL
    /// repayment (the balance reaches zero). The capped-commission math lives in the pure decider; the cap is
    /// the PT consumer-credit statutory ceiling (0.50% &gt;1y / 0.25% ≤1y).
    /// </summary>
    public async Task<long> RepayEarlyAsync(RepayEarlyCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // 1. Rehydrate the disbursed position.
        var hydrated = await runtime.LoadAsync(command.LoanId, ct);
        var position = hydrated.State;

        // Transition-legality gate: repaying early is legal only from Active.
        RejectIfIllegal(position.Lifecycle, LifecycleTransitions.Transition.RepayEarly, command.LoanId, "repay early");

        // 2. Decide (pure): the remaining installments select the statutory cap band and bound the
        //    lost-interest ceiling. The repayment DATE is derived from the command instant (an input).
        var repaidOn = DateOnly.FromDateTime(command.RepaidAt.UtcDateTime);
        var remainingInstallments = position.TermMonths - position.InstallmentsPaid;
        var events = CreditoPessoalDecider.DecideEarlyRepayment(
            position, new Money(command.RepaymentAmountCents), repaidOn, remainingInstallments);

        // 3. Settle (ADR-PC-016): collect the repaid capital PLUS the capped commission from the borrower.
        var repaid = events.OfType<LoanRepaidEarly>().Single();
        var collected = repaid.CapitalRepaid + repaid.Commission;
        await settlement.SettleAsync(
            new SettlementInstruction(
                command.LoanId, SettlementDirection.Credit, collected,
                command.RepaymentAccount, "early_repayment"),
            ct);

        // 4. Append at the current head (optimistic concurrency). The command id makes the append idempotent.
        return await runtime.AppendAsync(
            command.LoanId, hydrated.Version, events,
            Context(command.Actor, command.RepaidAt, command.CommandId), ct);
    }

    /// <summary>
    /// Write off a defaulted loan (ADR-PC-030 §P1 item 4): rehydrate it (must be Active), and append the
    /// <see cref="LoanWrittenOff"/> recording the remaining outstanding capital as an unrecoverable loss.
    /// NO settlement leg — a write-off recognises a loss, it does not move money. The engine RECORDS the
    /// write-off; it does not run the collections procedure (that is upstream).
    /// </summary>
    public async Task<long> WriteOffAsync(WriteOffLoanCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var hydrated = await runtime.LoadAsync(command.LoanId, ct);
        var position = hydrated.State;

        RejectIfIllegal(position.Lifecycle, LifecycleTransitions.Transition.WriteOff, command.LoanId, "write off");

        var writtenOffOn = DateOnly.FromDateTime(command.WrittenOffAt.UtcDateTime);
        var events = CreditoPessoalDecider.DecideWriteOff(position, writtenOffOn, command.WriteOffReason);

        return await runtime.AppendAsync(
            command.LoanId, hydrated.Version, events,
            Context(command.Actor, command.WrittenOffAt, command.CommandId), ct);
    }

    /// <summary>
    /// Record the GDPR Article 17 erasure fact on a loan (ADR-PC-004 §P3): append
    /// <see cref="PersonalDataErasureRequested"/> so the loan folds to <c>Erased</c>. The host has ALREADY
    /// crypto-shredded the subject's key before calling here, so this layer stays PII-free and only writes
    /// the structural audit fact. The lifecycle gate makes erasing an already-Erased loan illegal (also the
    /// idempotency guard against a double-erase request).
    /// </summary>
    public async Task<long> ErasePersonalDataAsync(ErasePersonalDataCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var hydrated = await runtime.LoadAsync(command.LoanId, ct);
        var position = hydrated.State;

        RejectIfIllegal(position.Lifecycle, LifecycleTransitions.Transition.Erase, command.LoanId, "erase personal data");

        var erasedOn = DateOnly.FromDateTime(command.ErasedAt.UtcDateTime);
        var erased = new PersonalDataErasureRequested(
            command.LoanId, command.SubjectPseudonym, erasedOn, command.ErasureReason);

        return await runtime.AppendAsync(
            command.LoanId, hydrated.Version, [erased],
            Context(command.Actor, command.ErasedAt, command.CommandId), ct);
    }

    /// <summary>
    /// Consult the lifecycle state machine (<see cref="LifecycleTransitions"/>) and reject an illegal
    /// transition with the established <see cref="DomainRejectedException"/> pattern — the single place
    /// command-side lifecycle legality is enforced (mirrors the term-deposit family's RejectIfIllegal).
    /// </summary>
    private static void RejectIfIllegal(
        LoanLifecycle current, LifecycleTransitions.Transition transition, Guid loanId, string action)
    {
        if (!LifecycleTransitions.IsLegal(current, transition))
        {
            throw new DomainRejectedException(
                $"Loan {loanId} is {current}; cannot {action} (illegal lifecycle transition {transition}).");
        }
    }

    // commandId is the OPTIONAL command-ingress idempotency key (ADR-PC-029 slot 4).
    private AppendContext Context(string actor, DateTimeOffset validTime, Guid? commandId = null) =>
        new(Family.FamilyName, pack.VersionKey, Family.SchemaVersion, actor, validTime, CommandId: commandId);
}
