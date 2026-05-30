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
public sealed record DepositConstituted(
    Guid DepositId,
    Money Principal,
    int TanBasisPoints,
    string RateSheetVersionId,
    int TermDays,
    DateOnly StartDate,
    DateOnly MaturityDate,
    string InterestVariant,
    string AutoRenewalPolicy) : DomainEvent;

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
