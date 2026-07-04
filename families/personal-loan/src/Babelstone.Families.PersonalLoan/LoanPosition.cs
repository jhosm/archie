using Babelstone.Engine;
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
/// <param name="WrittenOffAmount">The capital recognised as an UNRECOVERABLE LOSS when the loan was written
/// off (Money cents); <see cref="Money.Zero"/> for every loan that was not written off. This is what lets a
/// written-off loan be told apart from a fully-repaid one from the position ALONE: both carry a zero
/// <see cref="OutstandingBalance"/>, but only a written-off loan carries a non-zero loss here, so principal
/// reconciles (<c>TotalCapitalRepaid + WrittenOffAmount</c> closes the books) without reading the event log
/// (bd babelstone-5r9n.8). Folded from <c>LoanWrittenOff.OutstandingBalanceWrittenOff</c>; the engine RECORDS
/// the loss, it does not run collections (ADR-PC-030 §P1).</param>
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
    Money WrittenOffAmount,
    LoanLifecycle Lifecycle) : IErasable<LoanPosition>, IAccount
{
    /// <summary>
    /// GDPR Article 17 terminal transition (ADR-PC-004 §P3 / Amendment A4): label the loan
    /// <see cref="LoanLifecycle.Erased"/>. The engine's generic cross-cutting erasure fold
    /// (<c>PersonalDataErasureRequestedHandler&lt;LoanPosition&gt;</c>) calls this; the family owns only
    /// what "erased" means on its own lifecycle. Structural fields stay intact and queryable
    /// post-erasure (the PII lived behind the OpenBao key, never in this projection). Pure — no clock,
    /// no I/O (BENG001/002/003).
    /// </summary>
    public LoanPosition WithErased() => this with { Lifecycle = LoanLifecycle.Erased };

    /// <summary>
    /// The Account seam binding (ADR-PC-033 slot 1): the personal loan is a DEGENERATE account —
    /// one balance, no holds — so implementing <see cref="IAccount"/> RECLASSIFIES it; nothing
    /// about the fold changes. The <c>account_ref</c> is the loan's own stream id
    /// (<see cref="LoanId"/>): an opaque instance identifier the engine resolves internally, never
    /// PII (ADR-PC-004 §P2) — NOT <see cref="DisbursementAccountRef"/>, which is the COUNTERPARTY
    /// reference the disbursement moved money to, not this account. A computed read over the
    /// already-folded id — not a record positional parameter — so the compiler-synthesised record
    /// equality and replay determinism (ADR-PC-010 §P5) are untouched. Deliberately NOT
    /// <see cref="IHoldable"/>: a loan carries no holds, so its available balance trivially equals
    /// its accounting balance (the uniform-split degenerate case, ADR-PC-033 slot 1).
    /// </summary>
    public string AccountRef => LoanId.ToString();

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
        WrittenOffAmount: Money.Zero,
        Lifecycle: LoanLifecycle.Pending);
}
