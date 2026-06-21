using Babelstone.FinancialTypes;

namespace Babelstone.Families.PersonalLoan;

/// <summary>The lifecycle states the personal_loan aggregate folds into. The transition LEGALITY
/// (which states may move to which) is the <see cref="LifecycleTransitions"/> state machine,
/// deliberately NOT enforced here — these handlers are pure folds that label state, not guards
/// (mirrors the term-deposit family's split).</summary>
public enum LoanLifecycle
{
    /// <summary>Seed state before any event has folded.</summary>
    Pending,

    /// <summary>Disbursed and amortizing — between <c>LoanDisbursed</c> and a terminal closing event.</summary>
    Active,

    /// <summary>Disbursement was rejected by a config/rule check — no loan was opened (terminal).</summary>
    Failed,

    /// <summary>Fully amortized and settled — the outstanding balance reached zero (terminal).</summary>
    Settled,

    /// <summary>Written off as an unrecoverable loss after default (terminal). The engine RECORDS the
    /// write-off; it does not run the collections procedure (ADR-PC-030 §P1).</summary>
    WrittenOff,

    /// <summary>GDPR Article 17 right-to-be-forgotten exercised — the subject's PII key was
    /// crypto-shredded (ADR-PC-004 §P3) and only non-personal structural fields remain queryable
    /// (terminal). Reachable from any non-Pending state: erasure is a regulatory obligation that can
    /// land on a live OR an already-closed loan (a settled/written-off loan still holds the subject's
    /// PII until erased).</summary>
    Erased,
}

/// <summary>
/// The loan-position projection: the personal_loan aggregate's folded state — the closed-end-asset
/// analogue of the term-deposit family's <c>DepositPosition</c>. Produced by folding the family's events
/// through the existing engine mechanism (<see cref="Babelstone.Engine.SimulationRuntime{TState}"/> for
/// the in-memory read, <see cref="Babelstone.Engine.AggregateRuntime{TState}"/> for the durable
/// read-through), so a read of the just-committed log always reflects the latest event.
/// </summary>
/// <remarks>
/// All monetary fields are <see cref="Money"/> (cents); no <c>decimal</c> state lives here (ADR-PC-010
/// §P1, BMNY002). The amortization MATH runs command-side (the decider builds the events off
/// <c>Babelstone.FinancialMath.Amortization</c>); this fold only records the carried, already-computed
/// facts — accruing the interest/capital tallies and tracking the amortizing
/// <see cref="OutstandingBalance"/>, so a cold rebuild reproduces the position byte-for-byte
/// (ADR-PC-010 §P5). Unlike the deposit position (which carries a collection — the principal timeline),
/// the loan position is all scalar/Money/enum fields, so the compiler-synthesised record equality is
/// correct as-is (no custom Equals needed for replay determinism).
/// </remarks>
/// <param name="LoanId">The loan stream id.</param>
/// <param name="Principal">The original disbursed capital (Money cents).</param>
/// <param name="TanBasisPoints">The annual nominal rate (TAN) in basis points, folded from disbursement.</param>
/// <param name="RateSheetVersionId">The pinned rate-sheet version the TAN was resolved from.</param>
/// <param name="TermMonths">The number of monthly installments the loan amortizes over.</param>
/// <param name="PeriodicRateBasisPoints">The periodic (monthly) rate the schedule amortizes at, in
/// basis points (<c>TAN_bps / 12</c>) — folded so any later recompute reads it off the loan.</param>
/// <param name="InstallmentAmount">The level (constant) installment, folded from disbursement.</param>
/// <param name="StartDate">The disbursement date (schedule anchor), folded from disbursement.</param>
/// <param name="FirstInstallmentDate">The first installment's due date, folded from disbursement.</param>
/// <param name="Purpose">The loan purpose category (structural, not PII).</param>
/// <param name="ProductCode">The catalogue product code (structural, not PII).</param>
/// <param name="DisbursementAccountRef">The opaque disbursement-account token (a reference, not an IBAN).</param>
/// <param name="EarlyRepaymentCommissionBps">The pinned early-repayment commission rate in basis points.</param>
/// <param name="OutstandingBalance">The capital still owed — starts at the principal and amortizes toward
/// zero as installments are paid and early repayments land (folded from each balance-changing event).</param>
/// <param name="InstallmentsPaid">How many scheduled installments have been paid (folded count).</param>
/// <param name="TotalInterestPaid">Running sum of interest paid to date (Money cents).</param>
/// <param name="TotalCapitalRepaid">Running sum of capital returned to date — installments + early
/// repayments (Money cents).</param>
/// <param name="TotalCommissionCharged">Running sum of early-repayment commission charged (Money cents).</param>
/// <param name="Lifecycle">The current lifecycle state.</param>
public sealed record LoanPosition(
    Guid LoanId,
    Money Principal,
    int TanBasisPoints,
    string RateSheetVersionId,
    int TermMonths,
    int PeriodicRateBasisPoints,
    Money InstallmentAmount,
    DateOnly StartDate,
    DateOnly FirstInstallmentDate,
    string Purpose,
    string ProductCode,
    string DisbursementAccountRef,
    int EarlyRepaymentCommissionBps,
    Money OutstandingBalance,
    int InstallmentsPaid,
    Money TotalInterestPaid,
    Money TotalCapitalRepaid,
    Money TotalCommissionCharged,
    LoanLifecycle Lifecycle)
{
    /// <summary>The seed state a fold starts from (before <c>LoanDisbursed</c>).</summary>
    public static LoanPosition Empty { get; } = new(
        LoanId: Guid.Empty,
        Principal: Money.Zero,
        TanBasisPoints: 0,
        RateSheetVersionId: string.Empty,
        TermMonths: 0,
        PeriodicRateBasisPoints: 0,
        InstallmentAmount: Money.Zero,
        StartDate: default,
        FirstInstallmentDate: default,
        Purpose: string.Empty,
        ProductCode: string.Empty,
        DisbursementAccountRef: string.Empty,
        EarlyRepaymentCommissionBps: 0,
        OutstandingBalance: Money.Zero,
        InstallmentsPaid: 0,
        TotalInterestPaid: Money.Zero,
        TotalCapitalRepaid: Money.Zero,
        TotalCommissionCharged: Money.Zero,
        Lifecycle: LoanLifecycle.Pending);
}
