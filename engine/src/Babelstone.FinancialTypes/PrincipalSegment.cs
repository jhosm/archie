namespace Babelstone.FinancialTypes;

/// <summary>
/// One segment of a deposit's PRINCIPAL over time (fin-math §8.1 step-function balance; F.12 partial
/// withdrawal). The principal of a term deposit is normally constant, but a partial early withdrawal
/// reduces it mid-term, making the balance a step function. This segment is
/// the principal in force from <see cref="From"/> (inclusive) until the next segment's date (or the
/// accrual window's end for the last).
/// </summary>
/// <remarks>
/// Lives in <c>Babelstone.FinancialTypes</c> (beside <see cref="Money"/>), the layer BOTH the family
/// core — which folds the timeline onto its deposit position — and <c>Babelstone.FinancialMath</c> —
/// which accrues over it (<c>RateSchedule.AccrueGrossWindowOverPrincipal</c>) — can reference, without
/// the family handlers gaining a dependency on the math engine. A pure value type, like <see cref="Money"/>:
/// no clock, no I/O. The timeline it composes is a deterministic fold of the deposit's events —
/// <c>DepositConstituted</c> seeds the opening <c>(start, principal)</c>, each
/// <c>DepositPartiallyWithdrawn</c> appends <c>(withdrawn-on, remaining-principal)</c> — so a cold
/// replay reproduces it byte-for-byte (ADR-PC-010 §P5).
/// </remarks>
/// <param name="From">The date this principal takes effect — the deposit start for the first segment,
/// a withdrawal date for each later one. A well-formed timeline ascends strictly by date.</param>
/// <param name="Principal">The principal on deposit from <see cref="From"/> until the next change.</param>
public readonly record struct PrincipalSegment(DateOnly From, Money Principal);
