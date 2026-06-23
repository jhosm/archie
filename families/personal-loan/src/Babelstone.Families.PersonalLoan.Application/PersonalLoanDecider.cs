using Babelstone.Engine;
using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;

namespace Babelstone.Families.PersonalLoan.Application;

/// <summary>
/// The pure decision core of the personal_loan decider (ADR-PC-021 §P3): given a command plus the
/// inputs the service resolved (the rate-sheet TAN, the pinned commission rate), it produces the events —
/// running the AMORTIZATION kernel (<see cref="Amortization"/>) command-side, never in a fold. No clock,
/// no I/O, no randomness: every time/value input is explicit, so this is unit-tested Docker-free. The
/// impure orchestration (resolve, settle, append) lives in <see cref="PersonalLoanConstitutionService"/>;
/// keeping the two apart is what lets the shared choreography lift into a generic pipeline (ADR-PC-021 §P5,
/// bd babelstone-osv6).
/// </summary>
/// <remarks>
/// <b>Closed-end-asset shape vs the deposit liability.</b> Where the term-deposit decider accrues interest
/// to a single maturity, a loan DISBURSES a lump sum at t=0 and AMORTIZES it over <c>n</c> monthly
/// installments on a French (constant-installment) schedule (fin-math §4.1). The headline installment and
/// each period's interest/capital split come from <see cref="Amortization"/>; this decider stamps the
/// already-computed facts onto the events so the folds stay pure. Origination stays UPSTREAM (ADR-PC-030 /
/// ADR-PC-024): the loan is already approved and priced — the decider never models solvency.
/// </remarks>
public static class PersonalLoanDecider
{
    /// <summary>The monthly installment cadence: PT personal loans amortize on a monthly grid
    /// (fin-math §2.2 — the proportional periodic rate is <c>TAN / 12</c>). v1 prices monthly only.</summary>
    public const int PeriodsPerYear = 12;

    /// <summary>The PT consumer-credit statutory early-repayment commission cap, in basis points, when MORE
    /// than one year of term remains: 0.50% of the capital repaid (research/personal-loan/02 §2; PT DL
    /// 133/2009 art. 19). The pack/config could tighten this, but the statute is the ceiling.</summary>
    public const int StatutoryCapBpsOverOneYear = 50;

    /// <summary>The PT consumer-credit statutory early-repayment commission cap, in basis points, when ONE
    /// YEAR OR LESS of term remains: 0.25% of the capital repaid (research/personal-loan/02 §2).</summary>
    public const int StatutoryCapBpsUnderOneYear = 25;

    /// <summary>The closed, engine-owned commercial-eligibility verdict-key taxonomy (ADR-PC-024 §1, §6) —
    /// the SAME tokens the CUE family schema's precondition key enumerates and a product's
    /// <c>required_preconditions</c> picks from. For a loan the gate is origination-shaped: the upstream
    /// solvency/CRC checks that ADR-PC-024/ADR-PC-030 keep UPSTREAM, recorded here as opaque verdicts only.</summary>
    public const string PreconditionSolvencyAssessed = "solvency_assessed";
    public const string PreconditionCrcConsulted = "crc_consulted";

    /// <summary>The <see cref="LoanDisbursementFailed.FailureReason"/> code a precondition refusal records
    /// (ADR-PC-024 §5). Stable, machine-readable, non-PII — like every failure reason.</summary>
    public const string EligibilityNotMetReason = "ELIGIBILITY_NOT_MET";

    /// <summary>
    /// The PERIODIC (monthly) rate the schedule amortizes at, in basis points: <c>TAN_bps / 12</c> (the PT
    /// proportional-rate convention, fin-math §2.2). Integer-divided — the rate grid is the bank's pricing
    /// convention; any sub-bp remainder is the bank's rounding, not the engine's. Pure.
    /// </summary>
    public static int PeriodicRateBasisPoints(int tanBasisPoints) => tanBasisPoints / PeriodsPerYear;

    /// <summary>
    /// Build <see cref="LoanDisbursed"/> from the command, stamping the resolved TAN + the rate-sheet
    /// version it came from (ADR-PC-008 §P3), the derived periodic rate, and the LEVEL installment the
    /// French schedule yields (<see cref="Amortization.LevelInstallment"/>, fin-math §4.1). The first
    /// installment falls one cadence after disbursement. Pure: every input is explicit; the amortization
    /// math runs here, never in a fold.
    /// </summary>
    public static LoanDisbursed DecideDisbursement(
        DisburseLoanCommand command, int tanBasisPoints, string rateSheetVersionId)
    {
        var principal = new Money(command.PrincipalCents);
        var periodicRateBps = PeriodicRateBasisPoints(tanBasisPoints);
        var installment = Amortization.LevelInstallment(principal, periodicRateBps, command.TermMonths);

        return new LoanDisbursed(
            LoanId: command.LoanId,
            Principal: principal,
            TanBasisPoints: tanBasisPoints,
            RateSheetVersionId: rateSheetVersionId,
            TermMonths: command.TermMonths,
            PeriodicRateBasisPoints: periodicRateBps,
            InstallmentAmount: installment,
            StartDate: command.StartDate,
            FirstInstallmentDate: command.StartDate.AddMonths(1),
            Purpose: command.Purpose,
            ProductCode: command.ProductId,
            DisbursementAccountRef: command.DisbursementAccountRef,
            EarlyRepaymentCommissionBps: command.EarlyRepaymentCommissionBps);
    }

    /// <summary>
    /// Decide commercial eligibility (ADR-PC-024 §5): refuse the disbursement when a precondition the
    /// product REQUIRES is absent from the command's verdicts or evaluated <c>Satisfied == false</c>. Pure
    /// function of <paramref name="requiredPreconditions"/> and <paramref name="verdicts"/> — NO upstream
    /// call, no in-engine evaluation, no clock (ADR-PC-024 §3–§4). Identical refusal semantics to the
    /// term-deposit family: the engine never re-evaluates a verdict; it only checks each required key is
    /// present and satisfied. <c>null</c> ⇒ disburse proceeds; otherwise the refusal event.
    /// </summary>
    public static LoanDisbursementFailed? CheckPreconditions(
        Guid loanId,
        IReadOnlyCollection<string> requiredPreconditions,
        IReadOnlyDictionary<string, PreconditionVerdict>? verdicts)
    {
        // Ungated products never reach a refusal — fast path, no allocation.
        if (requiredPreconditions.Count == 0)
        {
            return null;
        }

        // A required key fails when it is absent OR not satisfied. Order deterministically so the detail
        // string (and thus the recorded event) is identical on replay — purity extends to the message.
        var unmet = requiredPreconditions
            .Where(key => verdicts is null
                || !verdicts.TryGetValue(key, out var verdict)
                || !verdict.Satisfied)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        if (unmet.Length == 0)
        {
            return null;
        }

        // Detail names the unmet KEYS only — never the verdict's evidence_ref or any customer fact
        // (ADR-PC-024 §1, §5). The full resolved verdicts are recorded for AUDIT LINEAGE.
        return new LoanDisbursementFailed(
            loanId,
            EligibilityNotMetReason,
            $"Required commercial-eligibility precondition(s) absent or not satisfied: {string.Join(", ", unmet)}.",
            RecordedVerdicts(verdicts));
    }

    /// <summary>
    /// Decide one scheduled installment (fin-math §3–§4.1): rebuild the French schedule from the loan's
    /// PINNED facts (principal, periodic rate, term) and take the row for the NEXT installment (the count
    /// already paid + 1). The row's interest/capital split and post-installment balance are stamped onto
    /// <see cref="LoanInstallmentPaid"/>. Rebuilding the WHOLE schedule and indexing the next row keeps the
    /// math anchored at the original principal (so the integer-cent conservation and the balancing final
    /// row hold), rather than incrementally re-deriving a balance that could drift. Pure: no clock, no I/O.
    /// </summary>
    /// <param name="position">The Active loan being amortized (carries the pinned schedule facts).</param>
    /// <param name="paidOn">The installment's paid date — an INPUT, not a clock read.</param>
    /// <exception cref="DomainRejectedException">If the loan has no installment left to pay (all paid).</exception>
    public static IReadOnlyList<DomainEvent> DecideInstallment(LoanPosition position, DateOnly paidOn)
    {
        var nextInstallmentNumber = position.InstallmentsPaid + 1;
        if (nextInstallmentNumber > position.TermMonths)
        {
            throw new DomainRejectedException(
                $"Loan {position.LoanId} has no installment left to pay (all {position.TermMonths} paid).");
        }

        // Rebuild the full schedule from the pinned facts and take the row for the next installment. The
        // schedule conserves to the cent and absorbs rounding in the final (balancing) row (fin-math §4.1).
        var schedule = Amortization.Schedule(
            position.Principal, position.PeriodicRateBasisPoints, position.TermMonths);
        var row = schedule[nextInstallmentNumber - 1]; // 0-based list, 1-based installment number

        return
        [
            new LoanInstallmentPaid(
                position.LoanId, row.Period, row.Interest, row.Capital, row.ClosingBalance, paidOn),
        ];
    }

    /// <summary>
    /// Decide an early repayment (fin-math §7.5): repay
    /// <paramref name="repaymentAmount"/> of the outstanding capital plus the LEGALLY-CAPPED commission.
    /// The commission is <c>min(charged_bps, statutory_cap_bps) × capitalRepaid</c>, further capped at the
    /// interest the borrower would still have paid (the §7.5 ceiling), via
    /// <see cref="Amortization.EarlyRepaymentCommission"/>. The statutory cap is selected by the REMAINING
    /// term (0.50% &gt;1y / 0.25% ≤1y). A FULL repayment (== outstanding balance) drives the balance to
    /// zero and is CLOSED by a paired <see cref="LoanSettled"/>; a PARTIAL one reduces the balance and the
    /// loan stays Active. Pure: every input explicit.
    /// </summary>
    /// <param name="position">The Active loan being repaid (carries the outstanding balance + pinned rate).</param>
    /// <param name="repaymentAmount">The capital to repay early.</param>
    /// <param name="repaidOn">The repayment's as-of date — an INPUT, not a clock read.</param>
    /// <param name="remainingInstallments">The number of scheduled installments still due — selects the
    /// statutory cap band (&gt;12 ⇒ 0.50%, ≤12 ⇒ 0.25%) and bounds the lost-interest ceiling.</param>
    /// <exception cref="DomainRejectedException">If the repayment is non-positive or exceeds the balance.</exception>
    public static IReadOnlyList<DomainEvent> DecideEarlyRepayment(
        LoanPosition position, Money repaymentAmount, DateOnly repaidOn, int remainingInstallments)
    {
        if (repaymentAmount.Cents <= 0)
        {
            throw new DomainRejectedException(
                $"Early repayment on loan {position.LoanId} must be positive (got {repaymentAmount.Cents}c).");
        }

        if (repaymentAmount.Cents > position.OutstandingBalance.Cents)
        {
            throw new DomainRejectedException(
                $"Early repayment {repaymentAmount.Cents}c on loan {position.LoanId} exceeds the outstanding " +
                $"balance {position.OutstandingBalance.Cents}c — a loan cannot repay more capital than it owes.");
        }

        var balanceAfter = position.OutstandingBalance - repaymentAmount;

        // The statutory cap band by remaining term: >1 year (more than 12 monthly installments left) caps at
        // 0.50%; ≤1 year caps at 0.25% (research/personal-loan/02 §2).
        var statutoryCapBps = remainingInstallments > PeriodsPerYear
            ? StatutoryCapBpsOverOneYear
            : StatutoryCapBpsUnderOneYear;

        // The §7.5 lost-interest ceiling: the commission may never exceed the interest the borrower would
        // still have paid over the remaining term. Approximate it as the interest the remaining schedule
        // would have accrued on the repaid capital — an upper bound that keeps the cap conservative.
        var lostInterestCeiling = LostInterestCeiling(position, repaymentAmount, remainingInstallments);

        var commission = Amortization.EarlyRepaymentCommission(
            repaymentAmount, position.EarlyRepaymentCommissionBps, statutoryCapBps, lostInterestCeiling);

        var events = new List<DomainEvent>
        {
            new LoanRepaidEarly(position.LoanId, repaymentAmount, commission, balanceAfter, repaidOn),
        };

        // A FULL repayment settles the loan (balance == 0): pair the repayment with a closing LoanSettled.
        if (balanceAfter.Cents == 0)
        {
            var totalCapital = position.TotalCapitalRepaid + repaymentAmount;
            events.Add(new LoanSettled(totalCapital, position.TotalInterestPaid, repaidOn));
        }

        return events;
    }

    /// <summary>
    /// Decide a final scheduled-installment settlement: when the LAST installment is paid, the loan is fully
    /// amortized (balance reaches zero), so the installment is paired with a closing <see cref="LoanSettled"/>.
    /// This is the scheduled-completion analogue of a full early repayment. Pure.
    /// </summary>
    public static IReadOnlyList<DomainEvent> DecideFinalInstallment(LoanPosition position, DateOnly paidOn)
    {
        var installmentEvents = DecideInstallment(position, paidOn);
        var paid = (LoanInstallmentPaid)installmentEvents[^1];

        var events = new List<DomainEvent>(installmentEvents);
        if (paid.OutstandingBalance.Cents == 0)
        {
            var totalCapital = position.TotalCapitalRepaid + paid.Capital;
            var totalInterest = position.TotalInterestPaid + paid.Interest;
            events.Add(new LoanSettled(totalCapital, totalInterest, paidOn));
        }

        return events;
    }

    /// <summary>
    /// Decide a write-off (ADR-PC-030 §P1 item 4): the engine RECORDS the remaining outstanding capital as
    /// an unrecoverable loss after default. It does NOT run the collections procedure — that is upstream;
    /// the engine records resulting state only. Pure.
    /// </summary>
    public static IReadOnlyList<DomainEvent> DecideWriteOff(
        LoanPosition position, DateOnly writtenOffOn, string writeOffReason) =>
    [
        new LoanWrittenOff(position.LoanId, position.OutstandingBalance, writtenOffOn, writeOffReason),
    ];

    /// <summary>
    /// An UPPER BOUND on the interest the borrower would still pay over the remaining term, used as the
    /// §7.5 lost-interest ceiling for the early-repayment commission. Computed as the interest the remaining
    /// schedule accrues on the repaid capital — a conservative ceiling that never under-bounds the real
    /// lost interest, so the commission is only ever clamped DOWN when it is genuinely excessive. Pure.
    /// </summary>
    private static Money LostInterestCeiling(
        LoanPosition position, Money repaymentAmount, int remainingInstallments)
    {
        if (remainingInstallments <= 0)
        {
            return Money.Zero;
        }

        // The interest one remaining period accrues on the repaid capital, times the periods remaining —
        // an upper bound (the real schedule's balance shrinks each period, so this over-states, never
        // under-states). Computed in ONE full-precision decimal expression and crossed to Money exactly
        // once (ADR-PC-010 §P1–§P2): the per-period interest (cents × periodic_bps / 10000) is NOT rounded
        // before being multiplied by remainingInstallments — rounding the per-period leg first and then
        // combining is the "round each step then combine" shape §P2 forbids. The per-period numerator is
        // the shared, UN-ROUNDED kernel helper Rate.ScaledByBasisPoints (bd babelstone-5r9n.6) — the same
        // `cents × bps / 10000` form the kernel uses internally — so the single rounding lands here, after
        // the multiply by the period count, and the 10,000 scale is no longer re-declared in this family.
        decimal ceiling = Rate.ScaledByBasisPoints(repaymentAmount.Cents, position.PeriodicRateBasisPoints)
            * remainingInstallments;
        return Money.FromCents(ceiling);
    }

    /// <summary>
    /// Map the command's resolved verdicts into the structural <see cref="RecordedPreconditionVerdict"/>
    /// lineage recorded on the refusal event (ADR-PC-024 §1 "for audit lineage only"). Ordered by key
    /// (Ordinal) so the recorded list is REPLAY-IDENTICAL regardless of the command map's iteration order.
    /// Pure: no clock, no I/O; the <c>evaluated_at</c> is upstream-supplied data carried through.
    /// </summary>
    private static IReadOnlyList<RecordedPreconditionVerdict> RecordedVerdicts(
        IReadOnlyDictionary<string, PreconditionVerdict>? verdicts) =>
        verdicts is null || verdicts.Count == 0
            ? Array.Empty<RecordedPreconditionVerdict>()
            : verdicts
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new RecordedPreconditionVerdict(
                    kv.Key, kv.Value.Satisfied, kv.Value.EvidenceRef, kv.Value.EvaluatedAt))
                .ToArray();
}
