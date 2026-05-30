using Babelstone.Engine;
using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;

namespace Babelstone.Families.TermDeposit.Application;

/// <summary>
/// The pure decision core of the term-deposit decider (ADR-PC-021 §P3): given a command plus
/// the inputs the service resolved (the rate-sheet TAN, the pack day-count and withholding),
/// it produces the events — running the financial-math kernel command-side, never in a fold.
/// No clock, no I/O, no randomness: every time/value input is explicit, so this is unit-tested
/// Docker-free. The impure orchestration (resolve, settle, append) lives in
/// <see cref="TermDepositConstitutionService"/>; keeping the two apart is what lets the shared
/// choreography lift into a generic pipeline later (ADR-PC-021 §P5, bd babelstone-osv6).
/// </summary>
public static class TermDepositDecider
{
    /// <summary>
    /// Build <see cref="DepositConstituted"/> from the command, stamping the resolved TAN and
    /// the rate-sheet version it came from (ADR-PC-008 §P3). The maturity date is derived from
    /// the start date and term — an explicit field on the event, not recomputed downstream.
    /// </summary>
    public static DepositConstituted DecideConstitution(
        ConstituteDepositCommand command, int tanBasisPoints, string rateSheetVersionId) =>
        new(
            DepositId: command.DepositId,
            Principal: new Money(command.PrincipalCents),
            TanBasisPoints: tanBasisPoints,
            RateSheetVersionId: rateSheetVersionId,
            TermDays: command.TermDays,
            StartDate: command.StartDate,
            MaturityDate: command.StartDate.AddDays(command.TermDays),
            InterestVariant: command.InterestVariant,
            AutoRenewalPolicy: command.AutoRenewalPolicy);

    /// <summary>
    /// The AT_MATURITY single flow: accrue gross interest over the term on the resolved
    /// day-count, withhold tax flow-by-flow, and pay out principal + net. The gross-then-net
    /// order matches the family fold and the kernel cross-check (fin-math §5.1/§5.4); net is
    /// the conserved residual <c>gross − tax</c>. Pure: the position carries every input
    /// (principal, TAN, start, maturity), the pack supplies the convention and rate.
    /// </summary>
    public static IReadOnlyList<DomainEvent> DecideMaturity(
        DepositPosition position, DayCountConvention dayCount, int withholdingBasisPoints)
    {
        var factor = DayCount.Between(position.StartDate, position.MaturityDate, dayCount);
        var gross = Accrual.SimpleInterest(position.Principal, position.TanBasisPoints, factor);
        var withheld = Withholding.Withhold(gross, withholdingBasisPoints);
        var payout = position.Principal + withheld.Net;

        return
        [
            new InterestAccrued(gross, position.MaturityDate),
            new WithholdingApplied(withheld.Tax, withheld.Net),
            new DepositMatured(position.Principal, withheld.Net, payout, position.MaturityDate),
        ];
    }
}
