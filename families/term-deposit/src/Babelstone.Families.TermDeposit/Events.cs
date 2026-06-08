using Babelstone.Engine;
using Babelstone.FinancialTypes;

namespace Babelstone.Families.TermDeposit;

// The four AT_MATURITY term-deposit events (E.1, archie-uqlm). Each carries the
// already-COMPUTED facts as Money — the financial-math kernel runs on the command/decider
// side (E.3) that builds these, never inside a handler fold (handlers stay pure, BENG001/2/3).
// Events are STRUCTURAL: no depositor PII (name/NIF) travels here (ADR-PC-004 §P2). The
// pack/schema/family pins (pt.2026.1 / term_deposit@2026.1) ride on the EventEnvelope via
// AppendContext, not on the event records.

/// <summary>The deposit is opened: principal, the rate-sheet-resolved TAN, and the
/// AT_MATURITY schedule are fixed at constitution.</summary>
/// <param name="TanBasisPoints">Annual nominal rate (TAN) in basis points, resolved from the
/// rate sheet at constitution (ADR-PC-008 §P3) — never inline config.</param>
/// <param name="RateSheetVersionId">The rate-sheet version the TAN was resolved from (pinned).</param>
/// <param name="PaymentPeriodMonths">The coupon cadence in months for the PERIODIC variant —
/// 1 (monthly) or 3 (quarterly), the only cadences v1 prices (02 §2.1). Carried so the engine
/// can derive each coupon window from this event alone. It is 0 for AT_MATURITY and ADVANCE,
/// which have no coupons. Optional/additive (defaulted) so pre-F.1 AT_MATURITY streams that
/// never carried it still replay (forward-only schema evolution).</param>
public sealed record DepositConstituted(
    Guid DepositId,
    Money Principal,
    int TanBasisPoints,
    string RateSheetVersionId,
    int TermDays,
    DateOnly StartDate,
    DateOnly MaturityDate,
    string InterestVariant,
    string AutoRenewalPolicy,
    int PaymentPeriodMonths = 0) : DomainEvent;

/// <summary>Interest accrued for the period. For AT_MATURITY this is the single flow at
/// maturity: <c>GrossInterest = Accrual.SimpleInterest(principal, tan, DayCount.Between(start, maturity, Act360))</c>.</summary>
public sealed record InterestAccrued(Money GrossInterest, DateOnly AsOf) : DomainEvent;

/// <summary>Withholding tax applied flow-by-flow to the gross interest:
/// <c>Withholding.Withhold(gross, 2800) → (Tax, Net)</c>, with <c>Net = Gross − Tax</c> conserved to the cent.</summary>
public sealed record WithholdingApplied(Money Tax, Money Net) : DomainEvent;

/// <summary>The deposit matures and pays out: <c>TotalPayout = Principal + NetInterest</c>.</summary>
public sealed record DepositMatured(
    Money PrincipalReturned,
    Money NetInterestPaid,
    Money TotalPayout,
    DateOnly MaturedOn) : DomainEvent;

// The seven remaining term-deposit events (F.2, babelstone-5czr) — the full lifecycle
// beyond the AT_MATURITY happy path. Same discipline as the four above: each carries
// already-COMPUTED facts (Money cents-native, ADR-PC-010 §P1), is STRUCTURAL only
// (computed facts + opaque references; NO depositor/heir PII — name/NIF/IBAN — in
// cleartext OR ciphertext, ADR-PC-004 §P2), and is folded by a PURE handler. The
// lifecycle state machine and transition legality are F.3 (babelstone-29v8), NOT here;
// the bitemporal projection/correction is D.1/D.2 / F.6, NOT here.

/// <summary>Constitution was rejected by a config/rule check, so no deposit exists. Carries
/// failure CODES only: <paramref name="FailureReason"/> is the machine code and
/// <paramref name="FailureDetail"/> describes the offending config or rule — NEVER anything
/// about the customer (ADR-PC-004 §P2).</summary>
/// <param name="FailureReason">Stable failure code (e.g. <c>RATE_SHEET_NOT_FOUND</c>).</param>
/// <param name="FailureDetail">Human-readable detail about the config/rule that failed — never PII.</param>
public sealed record DepositConstitutionFailed(
    Guid DepositId,
    string FailureReason,
    string FailureDetail) : DomainEvent;

/// <summary>Interest is paid out (the periodic/coupon variant, vs the single AT_MATURITY flow):
/// <c>NetInterest = GrossInterest − WithholdingTax</c> conserved to the cent.</summary>
public sealed record InterestPaid(
    Guid DepositId,
    Money GrossInterest,
    Money WithholdingTax,
    Money NetInterest,
    DateOnly PaidOn) : DomainEvent;

/// <summary>The deposit auto-renews into a new term: a fresh deposit (<paramref name="NewDepositId"/>)
/// is constituted from the rolled-over principal at the new rate-sheet-resolved TAN. The new
/// TAN/schedule are pinned facts (ADR-PC-008 §P3), resolved by the decider — never inline config.</summary>
public sealed record DepositRenewed(
    Guid DepositId,
    Guid NewDepositId,
    Money RolloverPrincipal,
    string NewRateSheetVersionId,
    int NewTanBasisPoints,
    int NewTermDays,
    DateOnly RenewalDate,
    DateOnly NewMaturityDate) : DomainEvent;

/// <summary>The deposit is broken before maturity. The depositor's payout is the principal still on
/// deposit PLUS the net interest accrued over the elapsed period, less the penalty haircut:
/// <c>NetSettlementAmount = PrincipalReturned + NetAccruedInterest − PenaltyAmount</c> (the F.4 decider
/// settles the accrued NET interest back too — only the penalty is forfeited, not the accrued interest).
/// <paramref name="PenaltyAmount"/> is the EFFECTIVE penalty actually charged and is non-negative. The
/// gross accrued interest, withholding, and net payout are emitted as the paired
/// <see cref="InterestAccrued"/>/<see cref="WithholdingApplied"/> flows (02 §2.5).</summary>
public sealed record DepositTerminatedEarly(
    Guid DepositId,
    Money PrincipalReturned,
    Money PenaltyAmount,
    Money NetSettlementAmount,
    DateOnly TerminatedOn,
    string TerminationReason) : DomainEvent;

/// <summary>A partial withdrawal reduces the deposit's principal:
/// <c>RemainingPrincipal</c> is the principal left after taking <paramref name="WithdrawnAmount"/> out.</summary>
public sealed record DepositPartiallyWithdrawn(
    Guid DepositId,
    Money WithdrawnAmount,
    Money RemainingPrincipal,
    DateOnly WithdrawnOn) : DomainEvent;

/// <summary>A correction to a previously-recorded fact. Carries opaque REFERENCES only
/// (<paramref name="PreviousValueRef"/> / <paramref name="CorrectedValueRef"/> point at the
/// resolvable values) — no PII travels here (ADR-PC-004 §P2). <paramref name="EffectiveFrom"/>
/// is the valid-time that feeds the D.1 §P2 bitemporal supersession; the real read-model
/// correction is D.1/D.2, NOT this fold.</summary>
public sealed record DepositCorrected(
    Guid DepositId,
    string CorrectionId,
    string CorrectedField,
    string PreviousValueRef,
    string CorrectedValueRef,
    DateOnly EffectiveFrom,
    string CorrectionReason) : DomainEvent;

/// <summary>The deposit balance is transferred to a deceased holder's heirs (succession).</summary>
/// <remarks>
/// Carries NO heir PII — no name, NIF, or IBAN — only the opaque <paramref name="HeirCaseRef"/>
/// (the succession case reference). The engine resolves heir identity internally from that
/// reference (ADR-PC-004 §P2); no identity ever rides on this structural event, in cleartext
/// or ciphertext.
/// </remarks>
/// <param name="HeirCaseRef">Opaque reference to the succession case — NOT an heir identity.</param>
public sealed record DepositTransferredToHeirs(
    Guid DepositId,
    string HeirCaseRef,
    Money TransferredBalance,
    DateOnly TransferDate) : DomainEvent;
