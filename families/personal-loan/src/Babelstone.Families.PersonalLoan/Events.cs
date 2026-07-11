using Babelstone.Engine;
using Babelstone.FinancialTypes;

namespace Babelstone.Families.PersonalLoan;

// The personal_loan (closed-end personal loan) events — the 2nd family on the ADR-PC-030
// roadmap (the closed-end ASSET, mirroring the term-deposit liability). Same discipline as
// term_deposit: each event carries already-COMPUTED facts as Money (cents-native, ADR-PC-010
// §P1) — the amortization kernel (Babelstone.FinancialMath.Amortization) runs command-side in
// the decider that BUILDS these, never inside a handler fold (folds stay pure, BENG001/2/3).
// Events are STRUCTURAL: no borrower PII (name/NIF/IBAN) travels here, in cleartext OR ciphertext
// (ADR-PC-004 §P2). The pack/schema/family pins ride on the EventEnvelope via AppendContext, not
// on the event records. ORIGINATION stays UPSTREAM (ADR-PC-030 §P1 / ADR-PC-024): the engine
// receives an ALREADY-APPROVED, ALREADY-PRICED loan and never models solvency/CRC/KYC/scoring.

/// <summary>
/// A commercial-eligibility verdict recorded on a constitution event for AUDIT LINEAGE only
/// (ADR-PC-024 §1): the opaque <c>{ satisfied, evidence_ref, evaluated_at }</c> triple an upstream
/// authority resolved for one predicate, which the saga gathered and the decider stamps onto a
/// disbursement / refusal. STRUCTURAL, not PII: <see cref="EvidenceRef"/> is a resolvable reference,
/// never identity data (ADR-PC-004 §P2, ADR-PC-024 §1). The engine never re-evaluates a verdict; this
/// record is a lineage artefact, not a decision input on replay (the refusal is re-derived from the
/// COMMAND's verdicts, ADR-PC-024 §4). Mirrors the term-deposit type one-for-one — the same
/// precondition contract governs both families.
/// </summary>
/// <param name="Key">The engine-owned closed verdict key, e.g. <c>solvency_assessed</c> (ADR-PC-024 §1).</param>
/// <param name="Satisfied">Whether the upstream authority found the predicate satisfied.</param>
/// <param name="EvidenceRef">An opaque reference to the upstream evidence — NOT identity data.</param>
/// <param name="EvaluatedAt">When the upstream authority took the verdict (audit lineage / freshness).</param>
public sealed record RecordedPreconditionVerdict(
    string Key,
    bool Satisfied,
    string EvidenceRef,
    DateTimeOffset EvaluatedAt);

/// <summary>
/// The loan is disbursed: the lump-sum principal is paid out to the borrower and the closed-end
/// French-system amortization schedule is fixed at constitution
/// (fin-math §4.1). This is the loan's first event — the closed-end-asset analogue of the term
/// deposit's <c>DepositConstituted</c>, but where a deposit accrues to a single maturity, a loan
/// pays out at t=0 (a lump-sum DISBURSEMENT) and amortizes over <see cref="TermMonths"/> installments.
/// </summary>
/// <param name="LoanId">The loan stream id (Guid).</param>
/// <param name="Principal">The disbursed capital (lump sum), as Money cents.</param>
/// <param name="TanBasisPoints">Annual nominal rate (TAN) in basis points, resolved from the rate
/// sheet at constitution (ADR-PC-008 §P3) — never inline config. The borrower was already priced
/// upstream; the engine stamps the resolved rate it priced against for replay lineage.</param>
/// <param name="RateSheetVersionId">The pinned rate-sheet version the TAN was resolved from.</param>
/// <param name="TermMonths">The number of monthly installments <c>n</c> the loan amortizes over.</param>
/// <param name="PeriodicRateBasisPoints">The PERIODIC (monthly) rate the schedule amortizes at,
/// in basis points — <c>TAN_bps / 12</c> (the PT proportional-rate convention, fin-math §2.2),
/// resolved and stamped by the decider so the schedule is reproducible from this event alone.</param>
/// <param name="InstallmentAmount">The LEVEL (constant) installment the borrower pays each period
/// (<c>P = C × r / (1 − (1 + r)^−n)</c>, fin-math §4.1), computed command-side and stamped here.</param>
/// <param name="StartDate">The disbursement date — the schedule's anchor.</param>
/// <param name="FirstInstallmentDate">The due date of the first installment (StartDate + one cadence).</param>
/// <param name="Purpose">The loan PURPOSE category (e.g. <c>general</c>,
/// <c>education</c>, <c>health</c>) — a STRUCTURAL pricing/regulatory dimension that selects the legal
/// TAEG ceiling bucket (research/personal-loan/02 §2). NOT PII (ADR-PC-004 §P2).</param>
/// <param name="ProductCode">The catalogue product code the rate sheet priced — STRUCTURAL, NOT PII.</param>
/// <param name="DisbursementAccountRef">An OPAQUE disbursement-account TOKEN — a REFERENCE the engine
/// resolves internally, NEVER an IBAN/cleartext identifier (ADR-PC-004 §P2). The account the lump sum
/// was credited to.</param>
/// <param name="EarlyRepaymentCommissionBps">The early-repayment commission the product charges, in
/// basis points (PINNED at constitution like the rate — ADR-PC-009; the gates a live loan is subject
/// to are fixed for its life). The capped early-repayment commission a later early
/// repayment charges resolves from this pinned value (fin-math §7.5). 0 ⇒ no commission.</param>
/// <param name="Movements">The disbursement's money movement(s) recorded APPEND-FIRST on this event
/// (ADR-PC-032 slot 5 / feature-design money-movement-settlement §7). A disbursement records ONE
/// <see cref="MovementOrigin.Originated"/> <see cref="Movement"/>: a <see cref="SettlementDirection.Credit"/>
/// against the borrower's <see cref="DisbursementAccountRef"/> (the lump sum ENTERS that account — correcting
/// the prior eager <c>Debit</c>-on-the-borrower wrinkle, feature-design §134), operation
/// <see cref="MovementOperation.Disburse"/>. The engine no longer settles eagerly on the append path; this
/// recorded movement is what the substrate-owned settlement saga auto-starts off (its
/// <c>ce_movementorigin</c> / <c>ce_movementdirections</c> headers promoted by the generic engine-spine seam,
/// bd babelstone-t7o3.20) to effect the cash leg, gated. Optional/additive (defaulted empty) so pre-Movement
/// streams still replay (forward-only schema evolution, ADR-IC-002): an empty carrier declares no settlement
/// headers and starts no saga.</param>
public sealed record LoanDisbursed(
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
    int EarlyRepaymentCommissionBps = 0,
    IReadOnlyList<Movement>? Movements = null) : DomainEvent, IMovementBearing
{
    // Disbursement is a snapshot lifecycle boundary (ADR-PC-003 §P2 / event-store §8.1): the instance's
    // state is interpretable on its own here, so a snapshot is taken regardless of the per-N count.
    public override bool IsLifecycleBoundary => true;

    /// <summary>
    /// Promote the disbursement Movement's origin/direction to the <c>ce_movementorigin</c> /
    /// <c>ce_movementdirections</c> CloudEvents extension headers the substrate-owned settlement saga
    /// auto-starts on (ADR-PC-032; ADR-IC-018), AND stamp the settlement COUNTERPARTY as
    /// <c>ce_settlementtarget = engine-ca</c> (ADR-PC-043 slot 1) so the disbursement CREDIT lands on the
    /// engine-OWNED current account rather than the legacy demand core. Routed through the counterparty-aware
    /// GENERIC engine-spine seam
    /// (<see cref="MovementHeaders.ForOriginatedMovements(System.Collections.Generic.IReadOnlyList{Babelstone.Engine.Movement}, Babelstone.Engine.SettlementTarget)"/>) — it
    /// names no family, so every Movement-bearing event gets the headers the same way; the substrate's router
    /// keys the counterparty selection on the promoted header ALONE, never <see cref="Movement.AccountRef"/>
    /// (the payload-blind boundary, ADR-IC-018). Null/empty movements (pre-Movement streams) declare no
    /// settlement header (the base-default behaviour), starting no saga.
    /// </summary>
    public override IReadOnlyDictionary<string, string>? IntegrationHeaders =>
        MovementHeaders.ForOriginatedMovements(Movements ?? [], SettlementTarget.EngineCa);

    /// <summary>
    /// The <see cref="IMovementBearing"/> READ view (ADR-PC-032 §A1) the spine's account-keyed movement
    /// ledger folds: the disbursement's movements as a NON-null list. Maps the nullable
    /// <see cref="Movements"/> carrier — kept nullable+defaulted so pre-Movement streams still replay
    /// (forward-only schema evolution, ADR-IC-002) — onto the non-null seam member by coalescing
    /// <see langword="null"/> to empty, so the generic projector folds it with no per-event null guard.
    /// </summary>
    IReadOnlyList<Movement> IMovementBearing.Movements => Movements ?? [];
}

/// <summary>
/// Constitution (disbursement) was rejected by a config/rule check, so no loan exists. Carries failure
/// CODES only — never anything about the borrower (ADR-PC-004 §P2). The closed-end-asset analogue of the
/// term deposit's <c>DepositConstitutionFailed</c>; the SAME precondition refusal contract (ADR-PC-024 §5).
/// </summary>
/// <param name="LoanId">The (would-be) loan stream id.</param>
/// <param name="FailureReason">Stable failure code (e.g. <c>RATE_SHEET_NOT_FOUND</c>, <c>ELIGIBILITY_NOT_MET</c>).</param>
/// <param name="FailureDetail">Human-readable detail about the config/rule that failed — never PII.</param>
/// <param name="Preconditions">For an <c>ELIGIBILITY_NOT_MET</c> refusal (ADR-PC-024 §5), the
/// commercial-eligibility verdicts the saga resolved upstream — recorded for AUDIT LINEAGE only
/// (ADR-PC-024 §1), each an opaque triple, STRUCTURAL not PII. Optional/additive (defaulted empty) so
/// non-eligibility failures carry none and still replay (forward-only).</param>
public sealed record LoanDisbursementFailed(
    Guid LoanId,
    string FailureReason,
    string FailureDetail,
    IReadOnlyList<RecordedPreconditionVerdict>? Preconditions = null) : DomainEvent;

/// <summary>
/// One scheduled installment is paid: the period's interest + capital split is recorded
/// (<c>P(t) = J(t) + A(t)</c>, fin-math §3). The interest leg <see cref="Interest"/> accrues on the
/// opening balance (<c>J(t) = S(t-1) × r</c>); the capital leg <see cref="Capital"/> reduces the
/// outstanding balance (<c>S(t) = S(t-1) − A(t)</c>). The amortization kernel computed both
/// command-side; the fold only RECORDS them (it never recomputes accrual or re-derives the split).
/// The closed-end analogue of the deposit's coupon/accrual flows — but here interest and capital are
/// returned TO the lender, and the balance amortizes toward zero.
/// </summary>
/// <param name="LoanId">The loan stream id.</param>
/// <param name="InstallmentNumber">The 1-based installment index (1 … n).</param>
/// <param name="Interest">The period's interest leg, exactly as the schedule computed it (Money cents).</param>
/// <param name="Capital">The period's capital-amortized leg (Money cents).</param>
/// <param name="OutstandingBalance">The capital still owed AFTER this installment (Money cents).</param>
/// <param name="PaidOn">The installment's paid date — carried by the source event (never a clock).</param>
/// <param name="Movements">The installment's money movement(s) recorded APPEND-FIRST on this event
/// (ADR-PC-032 slot 5 / feature-design money-movement-settlement §7; bd babelstone-t7o3.16). An installment
/// collection records ONE <see cref="MovementOrigin.Originated"/> <see cref="Movement"/>: a
/// <see cref="SettlementDirection.Debit"/> against the borrower's named COLLECTION account (interest + capital
/// LEAVES that account — direction pinned to the named account, feature-design §2), operation
/// <see cref="MovementOperation.CollectInstallment"/>. The engine no longer settles eagerly on this path; the
/// substrate-owned settlement saga auto-starts off the promoted <c>ce_movementorigin</c> /
/// <c>ce_movementdirections</c> headers (the engine-spine seam, bd babelstone-t7o3.20) to effect the cash leg,
/// gated. Optional/additive (defaulted empty) so pre-Movement streams still replay (forward-only schema
/// evolution, ADR-IC-002): an empty carrier declares no settlement headers and starts no saga.</param>
public sealed record LoanInstallmentPaid(
    Guid LoanId,
    int InstallmentNumber,
    Money Interest,
    Money Capital,
    Money OutstandingBalance,
    DateOnly PaidOn,
    IReadOnlyList<Movement>? Movements = null) : DomainEvent, IMovementBearing
{
    /// <summary>
    /// Promote the installment-collection Movement's origin/direction to the <c>ce_movementorigin</c> /
    /// <c>ce_movementdirections</c> CloudEvents extension headers the substrate-owned settlement saga
    /// auto-starts on (ADR-PC-032; ADR-IC-018), AND stamp the settlement COUNTERPARTY as
    /// <c>ce_settlementtarget = engine-ca</c> (ADR-PC-043 slot 1) so the installment DEBIT settles against the
    /// engine-OWNED current account rather than the legacy demand core. Routed through the counterparty-aware
    /// GENERIC engine-spine seam
    /// (<see cref="MovementHeaders.ForOriginatedMovements(System.Collections.Generic.IReadOnlyList{Babelstone.Engine.Movement}, Babelstone.Engine.SettlementTarget)"/>) — it names no family;
    /// the substrate's router keys the counterparty on the promoted header ALONE, never
    /// <see cref="Movement.AccountRef"/> (ADR-IC-018). Null/empty movements (pre-Movement streams) declare
    /// no settlement header, starting no saga.
    /// </summary>
    public override IReadOnlyDictionary<string, string>? IntegrationHeaders =>
        MovementHeaders.ForOriginatedMovements(Movements ?? [], SettlementTarget.EngineCa);

    /// <summary>
    /// The <see cref="IMovementBearing"/> READ view (ADR-PC-032 §A1) the spine's account-keyed movement
    /// ledger folds: this installment's movements as a NON-null list (the nullable <see cref="Movements"/>
    /// carrier coalesced to empty so pre-Movement streams still replay, ADR-IC-002). Same seam mapping as
    /// the sibling Movement-bearing loan events — the spine folds them all uniformly, naming no family.
    /// </summary>
    IReadOnlyList<Movement> IMovementBearing.Movements => Movements ?? [];
}

/// <summary>
/// The borrower repays the loan early (fin-math §7.5): a partial or full
/// prepayment of the outstanding capital, plus the LEGALLY-CAPPED early-repayment commission. The
/// commission is computed command-side as <c>min(charged, statutory_cap) × capitalRepaid</c>, further
/// capped at the interest the borrower would still have paid (the §7.5 ceiling) — the PT consumer-credit
/// caps are 0.50% (&gt;1y remaining) / 0.25% (≤1y remaining) of the capital repaid
/// (research/personal-loan/02 §2). A FULL repayment drives the balance to zero (the loan then settles);
/// a PARTIAL one reduces the balance and the loan stays Active. This is the closed-end-asset analogue of
/// the deposit's early-termination — but capped by statute, not a free penalty band.
/// </summary>
/// <param name="LoanId">The loan stream id.</param>
/// <param name="CapitalRepaid">The capital repaid early (the whole outstanding balance for a full
/// repayment, or the partial amount), Money cents.</param>
/// <param name="Commission">The EFFECTIVE early-repayment commission actually charged — already capped
/// to the statutory ceiling and the lost-interest ceiling. Non-negative (Money cents).</param>
/// <param name="OutstandingBalanceAfter">The capital still owed after the repayment — <c>Money.Zero</c>
/// for a full repayment (the loan then settles), reduced for a partial one (Money cents).</param>
/// <param name="RepaidOn">The repayment's as-of date — an input, never a clock read.</param>
/// <param name="Movements">The early-repayment's money movement(s) recorded APPEND-FIRST on this event
/// (ADR-PC-032 slot 5 / feature-design money-movement-settlement §7; bd babelstone-t7o3.16). An early
/// repayment records ONE <see cref="MovementOrigin.Originated"/> <see cref="Movement"/>: a
/// <see cref="SettlementDirection.Debit"/> against the borrower's named REPAYMENT account (capital repaid +
/// capped commission LEAVES that account — direction pinned to the named account, feature-design §2), operation
/// <see cref="MovementOperation.RepayEarly"/>. The engine no longer settles eagerly on this path; the
/// substrate-owned settlement saga auto-starts off the promoted <c>ce_movementorigin</c> /
/// <c>ce_movementdirections</c> headers (the engine-spine seam, bd babelstone-t7o3.20) to effect the cash leg,
/// gated. Optional/additive (defaulted empty) so pre-Movement streams still replay (forward-only schema
/// evolution, ADR-IC-002): an empty carrier declares no settlement headers and starts no saga.</param>
public sealed record LoanRepaidEarly(
    Guid LoanId,
    Money CapitalRepaid,
    Money Commission,
    Money OutstandingBalanceAfter,
    DateOnly RepaidOn,
    IReadOnlyList<Movement>? Movements = null) : DomainEvent, IMovementBearing
{
    /// <summary>
    /// Promote the early-repayment Movement's origin/direction to the <c>ce_movementorigin</c> /
    /// <c>ce_movementdirections</c> CloudEvents extension headers the substrate-owned settlement saga
    /// auto-starts on (ADR-PC-032; ADR-IC-018), AND stamp the settlement COUNTERPARTY as
    /// <c>ce_settlementtarget = engine-ca</c> (ADR-PC-043 slot 1) so the early-repayment DEBIT settles against
    /// the engine-OWNED current account rather than the legacy demand core. Routed through the
    /// counterparty-aware GENERIC engine-spine seam
    /// (<see cref="MovementHeaders.ForOriginatedMovements(System.Collections.Generic.IReadOnlyList{Babelstone.Engine.Movement}, Babelstone.Engine.SettlementTarget)"/>) — it names no family;
    /// the substrate's router keys the counterparty on the promoted header ALONE, never
    /// <see cref="Movement.AccountRef"/> (ADR-IC-018). Null/empty movements (pre-Movement streams) declare
    /// no settlement header, starting no saga.
    /// </summary>
    public override IReadOnlyDictionary<string, string>? IntegrationHeaders =>
        MovementHeaders.ForOriginatedMovements(Movements ?? [], SettlementTarget.EngineCa);

    /// <summary>
    /// The <see cref="IMovementBearing"/> READ view (ADR-PC-032 §A1) the spine's account-keyed movement
    /// ledger folds: this early repayment's movements as a NON-null list (the nullable
    /// <see cref="Movements"/> carrier coalesced to empty so pre-Movement streams still replay,
    /// ADR-IC-002). Same seam mapping as the sibling Movement-bearing loan events — folded uniformly.
    /// </summary>
    IReadOnlyList<Movement> IMovementBearing.Movements => Movements ?? [];

    // Early repayment is a snapshot lifecycle boundary (ADR-PC-003 §P2 / event-store §8.1) — a
    // balance-changing point where the instance's state is interpretable on its own.
    public override bool IsLifecycleBoundary => true;
}

/// <summary>
/// The loan is fully amortized and settled (terminal): the outstanding balance has reached zero, either
/// through the final scheduled installment or a full early repayment. The closed-end-asset analogue of
/// the deposit's <c>DepositMatured</c> — but a loan settles when its capital is fully returned, not at a
/// fixed maturity date. Keyed by the loan stream id (no <c>LoanId</c> field — like <c>DepositMatured</c>).
/// </summary>
/// <param name="TotalCapitalRepaid">The total capital returned over the loan's life (= the original
/// principal), Money cents.</param>
/// <param name="TotalInterestPaid">The total interest paid over the loan's life, Money cents.</param>
/// <param name="SettledOn">The settlement date — an input, never a clock read.</param>
public sealed record LoanSettled(
    Money TotalCapitalRepaid,
    Money TotalInterestPaid,
    DateOnly SettledOn) : DomainEvent
{
    // Settlement is a closing snapshot lifecycle boundary (ADR-PC-003 §P2 / event-store §8.1).
    public override bool IsLifecycleBoundary => true;
}

/// <summary>
/// The loan is written off as a loss (terminal): the lender records the remaining outstanding capital as
/// unrecoverable after default. The engine RECORDS the write-off as a structural audit fact — it does NOT
/// run the collections procedure (PARI/PERSI enforcement is upstream, ADR-PC-030 §P1 item 4; the engine
/// records resulting state only). No borrower PII rides here; <see cref="WriteOffReason"/> is a stable
/// machine code (ADR-PC-004 §P2).
/// </summary>
/// <param name="LoanId">The loan stream id.</param>
/// <param name="OutstandingBalanceWrittenOff">The unrecovered capital recorded as a loss, Money cents.</param>
/// <param name="WrittenOffOn">The write-off date — an input, never a clock read.</param>
/// <param name="WriteOffReason">A stable, non-PII reason code (e.g. <c>DEFAULT_UNRECOVERABLE</c>).</param>
public sealed record LoanWrittenOff(
    Guid LoanId,
    Money OutstandingBalanceWrittenOff,
    DateOnly WrittenOffOn,
    string WriteOffReason) : DomainEvent
{
    // Write-off is a closing snapshot lifecycle boundary (ADR-PC-003 §P2 / event-store §8.1).
    public override bool IsLifecycleBoundary => true;
}

/// <summary>The approved disbursement could not be delivered to the borrower's account, so the loan is
/// held DISBURSEMENT-PENDING at source rather than disgorged (ADR-PC-043 slot 5).
/// In plain English: the loan was approved but the money had nowhere to land — the borrower's account is
/// closed, dormant-past-revival, or does not exist — so instead of losing the disbursement the source
/// KEEPS the funds and marks itself disbursement-pending. The lifecycle-driver's re-attempt rule
/// (<c>DisbursementPendingRetryRule</c>) watches this flag and re-fires the disbursement once a live
/// destination exists (the re-attempt lands exactly once, keyed on the same economic intent). A
/// NON-terminal, reversible marker: <see cref="LoanLifecycle.Pending"/> →
/// <see cref="LoanLifecycle.DisbursementPending"/>, resolved by <see cref="LoanDisbursementLanded"/>.
/// The closed-end-asset analogue of the deposit's <c>DepositPayoutPending</c>.</summary>
/// <remarks>STORE-ONLY (ADR-IC-017 — an internal source-side hold marker, not a bus event; the
/// undeliverable credit's attributed IOU is the engine's cross-cutting <c>operations.CreditUnapplied</c>).
/// STRUCTURAL only, no PII (ADR-PC-004): opaque refs, a machine reason code, and an input date.</remarks>
/// <param name="LoanId">The loan stream held disbursement-pending — never PII (ADR-PC-004).</param>
/// <param name="DisbursementAccountRef">The opaque borrower account the disbursement could not reach — a
/// reference the engine resolves internally, never PII (ADR-PC-004).</param>
/// <param name="Reason">The stable machine reason the disbursement was undeliverable (e.g.
/// <c>BENEFICIARY_ACCOUNT_CLOSED</c>) — never free-text PII.</param>
/// <param name="PendingSince">The economic date the disbursement became pending — an input date, never a
/// clock read in a fold (ADR-PC-023).</param>
public sealed record LoanDisbursementHeld(
    Guid LoanId,
    string DisbursementAccountRef,
    string Reason,
    DateOnly PendingSince) : DomainEvent;

/// <summary>The held disbursement was delivered once a live destination existed, so the loan leaves
/// disbursement-pending and becomes active/amortizing (ADR-PC-043 slot 5). In plain
/// English: the account that could not receive the disbursement became receivable again, the re-attempt
/// landed the credit, and the loan is finally live. The resolve leg of the reversible marker:
/// <see cref="LoanLifecycle.DisbursementPending"/> → <see cref="LoanLifecycle.Active"/>.</summary>
/// <remarks>STORE-ONLY (ADR-IC-017). STRUCTURAL only, no PII (ADR-PC-004).</remarks>
/// <param name="LoanId">The loan stream whose disbursement landed — never PII (ADR-PC-004).</param>
/// <param name="DisbursementAccountRef">The opaque account the disbursement finally landed on — never PII.</param>
/// <param name="LandedOn">The economic date the disbursement landed — an input date, never a clock read.</param>
public sealed record LoanDisbursementLanded(
    Guid LoanId,
    string DisbursementAccountRef,
    DateOnly LandedOn) : DomainEvent
{
    // Landing the held disbursement is a snapshot lifecycle boundary (ADR-PC-003 §P2) — the loan becomes
    // live/amortizing, interpretable on its own.
    public override bool IsLifecycleBoundary => true;
}
