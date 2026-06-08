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

    /// <summary>The auto-renewal policy discriminators (02 §2.4.4) the renewal branches on — the same
    /// tokens the CUE family schema enumerates and <c>DepositConstituted.AutoRenewalPolicy</c> carries.</summary>
    public const string RenewalNone = "NONE";
    public const string RenewalSameTermCurrentRate = "SAME_TERM_CURRENT_RATE";
    public const string RenewalSameTermSameRate = "SAME_TERM_SAME_RATE";

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

    // ---- early termination (02 §2.5) -----------------------------------------------------------

    /// <summary>
    /// Break a deposit before maturity and settle net of the configured penalty (02 §2.5). Pure: the
    /// position carries the principal/rate/start, the pack supplies the day-count and withholding rate,
    /// and the product's <see cref="EarlyTerminationPolicy"/> + the termination date are explicit inputs
    /// — no clock. The flow, in order:
    /// <list type="number">
    /// <item><b>Accrue</b> the gross interest earned over the elapsed period
    /// <c>[start, terminationDate]</c> on the resolved day-count (a <i>short</i> term, not the full one).</item>
    /// <item><b>Withhold</b> that ONE accrued flow (<see cref="Withholding.Withhold"/>) — flow-by-flow,
    /// never rate-scaled (fin-math §5.4). The realized net interest is this single flow's net.</item>
    /// <item><b>Select the band</b> first-match against the elapsed term and compute the penalty as the
    /// band's basis-point share of its <see cref="PenaltyBasis"/> (accrued interest, principal, or both).
    /// The penalty is computed on the GROSS accrued interest (the headline the band prices), never on the
    /// post-tax net — the share is rounded once at the Money boundary like every other amount.</item>
    /// <item><b>Settle</b> <c>principal + netAccrued − penalty</c>, then <b>floor</b>: the net settlement
    /// never falls below the policy floor (02 §2.5). When the floor binds, the EFFECTIVE penalty is
    /// reduced so the conservation <c>settlement = principal + netAccrued − penalty</c> still holds to the
    /// cent (the event records the effective penalty, not the pre-floor headline).</item>
    /// </list>
    /// Emits the SAME three-event shape AT_MATURITY uses for the interest flow — <see cref="InterestAccrued"/>
    /// + <see cref="WithholdingApplied"/> (so the withholding ledger and position fold the accrued flow exactly
    /// as any other) — then <see cref="DepositTerminatedEarly"/> carrying the principal, the resolved penalty,
    /// and the net settlement. One accumulation path per flow: the accrued interest folds via the Accrued+Withheld
    /// pair (it has no <see cref="InterestPaid"/>), and the terminated event itself carries no interest tally.
    /// </summary>
    /// <param name="position">The Active deposit being broken (principal, TAN, start date).</param>
    /// <param name="terminationDate">The as-of date the break is priced and accrued to — an INPUT, not a clock read.</param>
    /// <param name="policy">The product's early-termination policy (flat or banded, with optional floor).</param>
    /// <param name="dayCount">The pack-resolved day-count convention the elapsed accrual uses.</param>
    /// <param name="withholdingBasisPoints">The pack-resolved withholding rate, applied to the one accrued flow.</param>
    /// <param name="terminationReason">A stable, non-PII reason code recorded on the event (e.g. <c>CUSTOMER_REQUEST</c>).</param>
    public static IReadOnlyList<DomainEvent> DecideEarlyTermination(
        DepositPosition position, DateOnly terminationDate, EarlyTerminationPolicy policy,
        DayCountConvention dayCount, int withholdingBasisPoints, string terminationReason)
    {
        // 1. Accrue the elapsed-period gross interest (start → termination), one flow on the resolved
        //    day-count. SimpleInterest rejects a reversed interval, so a termination before the start
        //    date fails loud rather than emitting negative interest.
        var factor = DayCount.Between(position.StartDate, terminationDate, dayCount);
        var grossAccrued = Accrual.SimpleInterest(position.RemainingPrincipal, position.TanBasisPoints, factor);

        // 2. Withhold that ONE flow — flow-by-flow (fin-math §5.4), never rate-scaled.
        var withheld = Withholding.Withhold(grossAccrued, withholdingBasisPoints);

        // 3. Select the band first-match against the elapsed term, then compute the penalty on its basis.
        var elapsedDays = terminationDate.DayNumber - position.StartDate.DayNumber;
        var band = policy.ResolveBand(elapsedDays);
        var penalty = ComputePenalty(band, position.RemainingPrincipal, grossAccrued);

        // 4. Settle principal + net accrued − penalty, then floor. The floor caps the EFFECTIVE penalty
        //    (never the principal/net legs) so the conservation settlement = principal + net − penalty
        //    still holds to the cent and the event records the penalty actually charged.
        var preFloorSettlement = position.RemainingPrincipal + withheld.Net - penalty;
        var (settlement, effectivePenalty) = ApplyFloor(preFloorSettlement, penalty, policy.Floor,
            position.RemainingPrincipal, withheld.Net);

        return
        [
            new InterestAccrued(grossAccrued, terminationDate),
            new WithholdingApplied(withheld.Tax, withheld.Net),
            new DepositTerminatedEarly(
                position.DepositId, position.RemainingPrincipal, effectivePenalty, settlement,
                terminationDate, terminationReason),
        ];
    }

    /// <summary>
    /// The penalty for a band: its basis-point share of the chosen basis (02 §2.5). The share is a
    /// single decimal computation rounded once at the Money boundary (ADR-PC-010 §P1–§P2) — never a
    /// rate scaled mid-calculation. The basis is the GROSS accrued interest, the principal, or both.
    /// </summary>
    private static Money ComputePenalty(EarlyTerminationBand band, Money principal, Money grossAccrued)
    {
        var basisAmount = band.Basis switch
        {
            PenaltyBasis.AccruedInterest => grossAccrued,
            PenaltyBasis.Principal => principal,
            // BOTH: the share applies to (principal + gross accrued interest).
            _ => principal + grossAccrued,
        };

        // share = basis_cents × penalty_bps / 10000, rounded once HALF_EVEN at the cents boundary.
        return Money.FromCents((decimal)basisAmount.Cents * band.PenaltyBasisPoints / 10_000);
    }

    /// <summary>
    /// Enforce the policy floor (02 §2.5): the net settlement never falls below it. When the floor
    /// binds, the EFFECTIVE penalty is reduced to <c>principal + net − floor</c> so the conservation
    /// <c>settlement = principal + net − penalty</c> stays exact to the cent (the floor is realised by
    /// charging less penalty, never by inventing money on the principal/net legs). With no floor, the
    /// pre-floor settlement and headline penalty pass through unchanged.
    /// </summary>
    private static (Money Settlement, Money EffectivePenalty) ApplyFloor(
        Money preFloorSettlement, Money penalty, Money? floor, Money principal, Money net)
    {
        if (floor is { } floorValue && preFloorSettlement.Cents < floorValue.Cents)
        {
            // Settlement is lifted to the floor; the penalty actually charged is whatever brings the
            // (principal + net) payout down to the floor — i.e. the floor absorbs the excess penalty.
            var effectivePenalty = principal + net - floorValue;
            return (floorValue, effectivePenalty);
        }

        return (preFloorSettlement, penalty);
    }

    // ---- auto-renewal (02 §2.4.4) --------------------------------------------------------------

    /// <summary>
    /// The renewal TAN (in basis points) and the rate-sheet version it came from, resolved by policy
    /// (02 §2.4.4). Pure: the service does the I/O (re-resolves the sheet at the renewal moment) and
    /// hands BOTH the freshly-resolved rate AND the closing deposit's original rate in; this method
    /// only PICKS between them per the policy the closing position carries — no clock, no I/O.
    /// <list type="bullet">
    /// <item><b>SAME_TERM_CURRENT_RATE</b> — the bank's then-current standard rate: take the freshly
    /// re-resolved <paramref name="currentTanBasisPoints"/> / <paramref name="currentRateSheetVersionId"/>.</item>
    /// <item><b>SAME_TERM_SAME_RATE</b> — the original rate: carry the closing deposit's
    /// <see cref="DepositPosition.TanBasisPoints"/> and <see cref="DepositPosition.RateSheetVersionId"/>
    /// forward unchanged. NOTE (bd babelstone-k4yr follow-up): 02 §2.4.4 pack-RESTRICTS this policy, but
    /// no pack primitive expressing that restriction exists yet — the policy is implemented here, the
    /// missing restriction is recorded rather than silently inventing a new pack primitive.</item>
    /// </list>
    /// <c>NONE</c> never reaches here (the service rejects it before deciding — there is no renewal to price).
    /// </summary>
    public static (int TanBasisPoints, string RateSheetVersionId) ResolveRenewalRate(
        DepositPosition closing, int currentTanBasisPoints, string currentRateSheetVersionId) =>
        closing.AutoRenewalPolicy switch
        {
            RenewalSameTermSameRate => (closing.TanBasisPoints, closing.RateSheetVersionId),
            // SAME_TERM_CURRENT_RATE is the only other renewable policy (NONE is rejected upstream).
            _ => (currentTanBasisPoints, currentRateSheetVersionId),
        };

    /// <summary>
    /// Build the new instance's <see cref="DepositConstituted"/> for an auto-renewal (02 §2.4.4 step 2):
    /// a fresh deposit constituted from the rolled-over principal at the policy-resolved TAN/version, for
    /// the SAME term, interest variant, cadence, and renewal policy as the closing deposit. The new start
    /// date is the renewal date and the new maturity is <c>renewalDate + termDays</c> — derived here, not
    /// recomputed downstream. Pure: the rolled-over principal, resolved rate, and renewal date are explicit
    /// inputs (the service settles and threads the <c>causation_id</c> → closing <c>DepositMatured</c>).
    /// </summary>
    public static DepositConstituted DecideRenewalConstitution(
        DepositPosition closing, Guid newDepositId, Money rolloverPrincipal,
        int tanBasisPoints, string rateSheetVersionId, DateOnly renewalDate) =>
        new(
            DepositId: newDepositId,
            Principal: rolloverPrincipal,
            TanBasisPoints: tanBasisPoints,
            RateSheetVersionId: rateSheetVersionId,
            TermDays: closing.TermDays,
            StartDate: renewalDate,
            MaturityDate: renewalDate.AddDays(closing.TermDays),
            InterestVariant: closing.InterestVariant,
            AutoRenewalPolicy: closing.AutoRenewalPolicy,
            PaymentPeriodMonths: closing.PaymentPeriodMonths);

    /// <summary>
    /// Build the <see cref="DepositRenewed"/> link (02 §2.4.4 step 3) carrying the closing↔new deposit ids
    /// and the new instance's pinned facts, for direct old→new lookup. Pure: every field is read off the
    /// closing position and the already-decided new constitution — no clock, no I/O.
    /// </summary>
    public static DepositRenewed DecideRenewalLink(DepositPosition closing, DepositConstituted renewed) =>
        new(
            DepositId: closing.DepositId,
            NewDepositId: renewed.DepositId,
            RolloverPrincipal: renewed.Principal,
            NewRateSheetVersionId: renewed.RateSheetVersionId,
            NewTanBasisPoints: renewed.TanBasisPoints,
            NewTermDays: renewed.TermDays,
            RenewalDate: renewed.StartDate,
            NewMaturityDate: renewed.MaturityDate);
}
