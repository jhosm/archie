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
    /// <summary>The interest-variant discriminators (02 §2.1) the decider branches on — the same
    /// tokens the CUE family schema enumerates and the command/event carry.</summary>
    public const string AtMaturity = "AT_MATURITY";
    public const string Periodic = "PERIODIC";
    public const string Advance = "ADVANCE";

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
            AutoRenewalPolicy: command.AutoRenewalPolicy,
            PaymentPeriodMonths: command.PaymentPeriodMonths);

    /// <summary>
    /// Mature a deposit, branching on its interest variant (02 §2.1). Pure: the position carries
    /// every input, the pack supplies the convention and rate.
    /// <list type="bullet">
    /// <item><b>AT_MATURITY</b> — the single flow: accrue gross interest over the full term, withhold
    /// once, pay out principal + net (<c>CF(maturity) = +C + J</c>).</item>
    /// <item><b>PERIODIC</b> — the FINAL coupon: accrue interest for the last coupon window only
    /// (last-paid-through → maturity), withhold that one flow, and pay out principal + that final net
    /// (<c>CF(n) = +C + J_n</c>). The intermediate coupons were each paid by
    /// <see cref="DecideInterestPayment"/>; maturity must NOT re-accrue the whole term.</item>
    /// <item><b>ADVANCE</b> — principal ONLY: interest for the full term was paid at t=0 by
    /// <see cref="DecideAdvance"/>, so maturity emits a zero-interest <see cref="DepositMatured"/>
    /// returning the principal alone (<c>CF(n) = +C</c>). No re-accrual.</item>
    /// </list>
    /// Withholding is always flow-by-flow (one <see cref="Withholding.Withhold"/> per flow), never
    /// rate-scaled and never applied to an aggregate (fin-math §5.4).
    /// </summary>
    public static IReadOnlyList<DomainEvent> DecideMaturity(
        DepositPosition position, DayCountConvention dayCount, int withholdingBasisPoints) =>
        position.InterestVariant switch
        {
            Advance => MatureAdvance(position),
            Periodic => MatureFinalCoupon(position, dayCount, withholdingBasisPoints),
            // AT_MATURITY is the default — the single-flow full-term accrual.
            _ => MatureSingleFlow(position, position.StartDate, position.MaturityDate, dayCount, withholdingBasisPoints),
        };

    /// <summary>
    /// Pay one intermediate PERIODIC coupon (02 §2.1 <c>CF(k) = +J_k</c>, k = 1..n-1): accrue the
    /// coupon window's interest on the resolved day-count, withhold that ONE flow, and emit
    /// <see cref="InterestPaid"/>. Principal is untouched (periodic deposits do NOT compound the
    /// balance — coupons are paid OUT to the current account). Withholding is per-coupon, so the
    /// realized net is the sum of each coupon's net, NEVER <c>gross_total × (1 − rate)</c> on the
    /// aggregate (fin-math §5.4 — the rate-scaling shortcut is exact only for a single flow).
    /// Pure: the window dates and pack rate/convention are explicit inputs.
    /// </summary>
    /// <param name="periodStart">The coupon window's inclusive start (the previous coupon's end,
    /// or the deposit start for the first coupon).</param>
    /// <param name="periodEnd">The coupon window's exclusive end (the coupon's due/paid date).</param>
    /// <remarks>
    /// Emits ONLY <see cref="InterestPaid"/> — a self-contained coupon event that carries gross, tax,
    /// AND net, and whose <see cref="InterestPaidHandler"/> folds all three running tallies
    /// (AccruedGrossInterest, WithholdingToDate, NetInterest). It deliberately does NOT also emit the
    /// AT_MATURITY <see cref="InterestAccrued"/> + <see cref="WithholdingApplied"/> pair: those
    /// handlers accumulate the SAME tallies, so emitting both alongside InterestPaid would
    /// double-count every coupon. The AT_MATURITY single flow uses the Accrued+Withheld pair (it has
    /// no InterestPaid); the coupon flow uses InterestPaid. One accumulation path per flow.
    /// </remarks>
    public static IReadOnlyList<DomainEvent> DecideInterestPayment(
        DepositPosition position, DateOnly periodStart, DateOnly periodEnd,
        DayCountConvention dayCount, int withholdingBasisPoints)
    {
        var factor = DayCount.Between(periodStart, periodEnd, dayCount);
        var gross = Accrual.SimpleInterest(position.Principal, position.TanBasisPoints, factor);
        var withheld = Withholding.Withhold(gross, withholdingBasisPoints);

        return [new InterestPaid(position.DepositId, withheld.Gross, withheld.Tax, withheld.Net, periodEnd)];
    }

    /// <summary>
    /// Pay ADVANCE interest up front at constitution (02 §2.1 <c>CF(0) = -C + J</c>): the FULL-term
    /// nominal interest is the same <see cref="Accrual.SimpleInterest"/> over (start → maturity) as
    /// AT_MATURITY — there is NO present-value discounting; ADVANCE is a pure timing/presentation
    /// difference (fin-math §5.3). Withhold once at t=0 and emit <see cref="InterestPaid"/> dated the
    /// start. The principal alone returns at maturity (<see cref="MatureAdvance"/>). Pure: the start,
    /// maturity, and pack rate/convention are explicit inputs.
    /// </summary>
    /// <remarks>
    /// Like <see cref="DecideInterestPayment"/>, emits ONLY <see cref="InterestPaid"/> (dated the
    /// start) — the self-contained payout event that folds gross/tax/net once. It does NOT also emit
    /// <see cref="InterestAccrued"/> + <see cref="WithholdingApplied"/>, which would double-count the
    /// same tallies.
    /// </remarks>
    public static IReadOnlyList<DomainEvent> DecideAdvance(
        DepositPosition position, DayCountConvention dayCount, int withholdingBasisPoints)
    {
        var factor = DayCount.Between(position.StartDate, position.MaturityDate, dayCount);
        var gross = Accrual.SimpleInterest(position.Principal, position.TanBasisPoints, factor);
        var withheld = Withholding.Withhold(gross, withholdingBasisPoints);

        return [new InterestPaid(position.DepositId, withheld.Gross, withheld.Tax, withheld.Net, position.StartDate)];
    }

    // ---- variant-specific maturity flows -------------------------------------------------------

    /// <summary>AT_MATURITY: one accrual over the whole term, one withholding, payout = principal + net.</summary>
    private static IReadOnlyList<DomainEvent> MatureSingleFlow(
        DepositPosition position, DateOnly start, DateOnly end,
        DayCountConvention dayCount, int withholdingBasisPoints)
    {
        var factor = DayCount.Between(start, end, dayCount);
        var gross = Accrual.SimpleInterest(position.Principal, position.TanBasisPoints, factor);
        var withheld = Withholding.Withhold(gross, withholdingBasisPoints);
        var payout = position.Principal + withheld.Net;

        return
        [
            new InterestAccrued(gross, end),
            new WithholdingApplied(withheld.Tax, withheld.Net),
            new DepositMatured(position.Principal, withheld.Net, payout, end),
        ];
    }

    /// <summary>
    /// PERIODIC maturity: accrue ONLY the final coupon window (last-paid-through → maturity), withhold
    /// that one flow, and pay out principal + that final net. The last coupon is paid together with
    /// the principal at maturity (02 §2.1). Re-accruing the whole term here would double-count the
    /// coupons already paid — the bug this branch exists to avoid.
    /// </summary>
    private static IReadOnlyList<DomainEvent> MatureFinalCoupon(
        DepositPosition position, DayCountConvention dayCount, int withholdingBasisPoints)
    {
        var lastPaidThrough = CouponBoundary(position, position.CouponsPaid);
        var factor = DayCount.Between(lastPaidThrough, position.MaturityDate, dayCount);
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

    /// <summary>ADVANCE maturity: principal only — interest was paid at t=0. Zero-interest payout.</summary>
    private static IReadOnlyList<DomainEvent> MatureAdvance(DepositPosition position) =>
    [
        new DepositMatured(position.Principal, Money.Zero, position.Principal, position.MaturityDate),
    ];

    /// <summary>
    /// The coupon boundary date <paramref name="couponIndex"/> months-cadences after the start, capped
    /// at the maturity date. With cadence <c>p</c> months, boundary <c>k</c> is
    /// <c>start.AddMonths(k × p)</c>; a boundary at or past maturity collapses onto the maturity date
    /// so the final (possibly short/stub) coupon runs exactly to maturity. Pure date arithmetic —
    /// no clock. The service uses this to derive the next coupon window from <c>CouponsPaid</c>.
    /// </summary>
    public static DateOnly CouponBoundary(DepositPosition position, int couponIndex)
    {
        var boundary = position.StartDate.AddMonths(couponIndex * position.PaymentPeriodMonths);
        return boundary >= position.MaturityDate ? position.MaturityDate : boundary;
    }
}
