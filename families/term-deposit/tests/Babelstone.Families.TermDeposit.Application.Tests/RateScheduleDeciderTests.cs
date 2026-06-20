using Babelstone.Engine;
using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// The rate-VECTOR fold through the term-deposit decider (F.10, bd k6r8.3): step-up (<i>crescente</i>)
/// and amount-tiered (<i>escalonada</i>) schedules resolved at constitution and folded over the B.3
/// accrual engine, plus the rate-reduction early-termination penalty (F.11, bd k6r8.4). Pure,
/// Docker-free. Two load-bearing properties: (1) passing a flat (one-segment) schedule is
/// byte-identical to passing none — so the vector is purely additive over the existing flat path;
/// (2) withholding stays FLOW-BY-FLOW on the real gross even when a rate reduction is the penalty
/// (fin-math §5.4) — the reduction is a haircut on the gross, never a re-scaling of the withholding.
/// </summary>
public sealed class RateScheduleDeciderTests
{
    private static readonly DateOnly Start = new(2026, 1, 1);
    private const long PrincipalCents = 1_000_000; // EUR 10,000.00
    private const int IrsBps = 2800;
    private const string Reason = "CUSTOMER_REQUEST";

    private static DepositPosition AtMaturityPosition(int tanBps, int termDays) => (DepositPosition.Empty with
    {
        DepositId = Guid.NewGuid(),
        Principal = new Money(PrincipalCents),
        TanBasisPoints = tanBps,
        TermDays = termDays,
        StartDate = Start,
        MaturityDate = Start.AddDays(termDays),
        InterestVariant = "AT_MATURITY",
        RemainingPrincipal = new Money(PrincipalCents),
        Lifecycle = DepositLifecycle.Active,
    }).AsFreshlyConstituted();

    // ---- F.10: a flat schedule is byte-identical to no schedule (additive over the flat path) ----

    [Fact]
    public void DecideMaturity_with_a_flat_schedule_equals_no_schedule()
    {
        var position = AtMaturityPosition(tanBps: 300, termDays: 365);

        var withoutSchedule = TermDepositDecider.DecideMaturity(position, DayCountConvention.Act360, IrsBps);
        var withFlat = TermDepositDecider.DecideMaturity(
            position, DayCountConvention.Act360, IrsBps, RateSchedule.Flat(300));

        Assert.Equal(withoutSchedule, withFlat); // record-equal events — the vector is purely additive
    }

    // ---- F.10: step-up (crescente) folds into the single AT_MATURITY flow (no new event) ----------

    [Fact]
    public void DecideMaturity_stepUp_folds_the_vector_into_the_single_maturity_flow()
    {
        // 360-day term, crescente: first 180 days @ 2%, next 180 days @ 4% on €10,000, Act/360.
        // gross = 10,000.00 + 20,000.00 = 30,000c (€300.00). Withhold ONE flow: tax = 30,000×28% =
        // 8,400; net = 21,600. Payout = principal + net.
        var position = AtMaturityPosition(tanBps: 0, termDays: 360); // TAN unused; the vector drives it
        var schedule = RateSchedule.StepUp([new RateSegment(0, 200), new RateSegment(180, 400)]);

        var events = TermDepositDecider.DecideMaturity(position, DayCountConvention.Act360, IrsBps, schedule);

        // Still exactly the three-event AT_MATURITY shape — no new event type for the vector.
        Assert.Equal(3, events.Count);
        var accrued = Assert.IsType<InterestAccrued>(events[0]);
        var withheld = Assert.IsType<WithholdingApplied>(events[1]);
        var matured = Assert.IsType<DepositMatured>(events[2]);

        Assert.Equal(new Money(30_000), accrued.GrossInterest); // the folded vector, ONE flow
        Assert.Equal(new Money(8_400), withheld.Tax);           // flow-by-flow on the single gross
        Assert.Equal(new Money(21_600), withheld.Net);
        Assert.Equal(new Money(PrincipalCents + 21_600), matured.TotalPayout);
    }

    [Fact]
    public void StepUp_earns_strictly_more_than_the_opening_rate_held_flat()
    {
        // A crescente that rises 2%→4% earns more than a flat 2% for the whole term (the point of
        // a step-up product) and less than a flat 4% — the vector is bracketed by its endpoints.
        var position = AtMaturityPosition(tanBps: 0, termDays: 360);
        var stepUp = RateSchedule.StepUp([new RateSegment(0, 200), new RateSegment(180, 400)]);
        var flatLow = RateSchedule.Flat(200);
        var flatHigh = RateSchedule.Flat(400);

        var stepped = ((InterestAccrued)TermDepositDecider.DecideMaturity(
            position, DayCountConvention.Act360, IrsBps, stepUp)[0]).GrossInterest;
        var low = ((InterestAccrued)TermDepositDecider.DecideMaturity(
            position, DayCountConvention.Act360, IrsBps, flatLow)[0]).GrossInterest;
        var high = ((InterestAccrued)TermDepositDecider.DecideMaturity(
            position, DayCountConvention.Act360, IrsBps, flatHigh)[0]).GrossInterest;

        Assert.True(stepped.Cents > low.Cents);
        Assert.True(stepped.Cents < high.Cents);
    }

    // ---- F.10: amount-tiered (escalonada) on the principal band ---------------------------------

    [Fact]
    public void DecideMaturity_amountTiered_prices_each_principal_tranche()
    {
        // Escalonada: first €5,000 @ 2%, the rest @ 4%, 360-day term, Act/360.
        // gross = 500,000×200×360/(360×10000) + 500,000×400×360/(360×10000) = 10,000 + 20,000 = 30,000c.
        var position = AtMaturityPosition(tanBps: 0, termDays: 360);
        var schedule = RateSchedule.AmountTiered([new RateSegment(0, 200), new RateSegment(500_000, 400)]);

        var accrued = (InterestAccrued)TermDepositDecider.DecideMaturity(
            position, DayCountConvention.Act360, IrsBps, schedule)[0];

        Assert.Equal(new Money(30_000), accrued.GrossInterest);
    }

    // ---- F.10: step-up on a PERIODIC coupon — a later coupon earns the higher step ---------------

    [Fact]
    public void DecideInterestPayment_stepUp_prices_the_coupon_window_at_the_rate_in_force()
    {
        // crescente: [0→3%, 180→6%]. A monthly coupon early in the term (days 0–31) accrues at 3%;
        // a coupon after the step (days 210–241) accrues at 6%. The later coupon earns strictly more.
        var position = AtMaturityPosition(tanBps: 0, termDays: 360) with
        {
            InterestVariant = "PERIODIC",
            PaymentPeriodMonths = 1,
        };
        var schedule = RateSchedule.StepUp([new RateSegment(0, 300), new RateSegment(180, 600)]);

        var earlyWindowStart = Start;                 // day 0
        var earlyWindowEnd = Start.AddDays(31);       // day 31 — wholly in the 3% step
        var lateWindowStart = Start.AddDays(210);     // day 210 — wholly in the 6% step
        var lateWindowEnd = Start.AddDays(241);       // day 241

        var early = (InterestPaid)TermDepositDecider.DecideInterestPayment(
            position, earlyWindowStart, earlyWindowEnd, DayCountConvention.Act360, IrsBps, schedule)[0];
        var late = (InterestPaid)TermDepositDecider.DecideInterestPayment(
            position, lateWindowStart, lateWindowEnd, DayCountConvention.Act360, IrsBps, schedule)[0];

        // Each window is priced exactly at the step in force across it: the early coupon at 3% over
        // its 31 days, the late one at 6% over its 31 days. (Asserting exact per-window grosses, not
        // a 2× ratio — the two windows round independently, so the ratio is off by a cent.)
        var expectedEarly = Accrual.SimpleInterest(
            new Money(PrincipalCents), 300, DayCount.Between(earlyWindowStart, earlyWindowEnd, DayCountConvention.Act360));
        var expectedLate = Accrual.SimpleInterest(
            new Money(PrincipalCents), 600, DayCount.Between(lateWindowStart, lateWindowEnd, DayCountConvention.Act360));
        Assert.Equal(expectedEarly, early.GrossInterest);
        Assert.Equal(expectedLate, late.GrossInterest);
        Assert.True(late.GrossInterest.Cents > early.GrossInterest.Cents); // the later coupon earns the higher step
    }

    [Fact]
    public void DecideInterestPayment_stepUp_coupon_window_STRADDLING_a_step_is_split_exactly_at_the_boundary()
    {
        // The subtlest cash-flow in F.10: a coupon window that crosses a step boundary. The schedule
        // is anchored at the deposit start (day 0), so a window over days 170–201 of a [0→3%, 180→6%]
        // crescente must accrue 10 days @ 3% (170–180) + 21 days @ 6% (180–201) — the split happens
        // at the elapsed-day boundary, NOT at the window's edges. Asserted against an INDEPENDENTLY
        // computed two-segment decimal rounded ONCE, so a re-anchoring or per-leg-rounding regression
        // through the decider path is caught here (not only at the RateSchedule unit level).
        var position = AtMaturityPosition(tanBps: 0, termDays: 360) with
        {
            InterestVariant = "PERIODIC",
            PaymentPeriodMonths = 1,
        };
        var schedule = RateSchedule.StepUp([new RateSegment(0, 300), new RateSegment(180, 600)]);

        var windowStart = Start.AddDays(170); // 10 days before the step
        var windowEnd = Start.AddDays(201);   // 21 days after the step — a 31-day window straddling day 180

        // Independent expected: 10 days @ 3% + 21 days @ 6% on €10,000, Act/360, summed in decimal
        // and crossed to cents exactly ONCE (the single-rounding-boundary discipline).
        decimal exact = (decimal)PrincipalCents * 300 * 10 / (360m * 10_000)
                      + (decimal)PrincipalCents * 600 * 21 / (360m * 10_000);
        var expected = Money.FromCents(exact);

        var paid = (InterestPaid)TermDepositDecider.DecideInterestPayment(
            position, windowStart, windowEnd, DayCountConvention.Act360, IrsBps, schedule)[0];

        Assert.Equal(expected, paid.GrossInterest);

        // Sanity: the straddle is strictly between a 31-day window held wholly at 3% and wholly at 6%.
        var allLow = Accrual.SimpleInterest(
            new Money(PrincipalCents), 300, DayCount.Between(windowStart, windowEnd, DayCountConvention.Act360));
        var allHigh = Accrual.SimpleInterest(
            new Money(PrincipalCents), 600, DayCount.Between(windowStart, windowEnd, DayCountConvention.Act360));
        Assert.True(paid.GrossInterest.Cents > allLow.Cents);
        Assert.True(paid.GrossInterest.Cents < allHigh.Cents);
    }

    [Fact]
    public void DecideInterestPayment_with_a_flat_schedule_equals_no_schedule()
    {
        var position = AtMaturityPosition(tanBps: 325, termDays: 360) with
        {
            InterestVariant = "PERIODIC",
            PaymentPeriodMonths = 1,
        };
        var windowStart = Start.AddDays(31);
        var windowEnd = Start.AddDays(62);

        var withoutSchedule = TermDepositDecider.DecideInterestPayment(
            position, windowStart, windowEnd, DayCountConvention.Act360, IrsBps);
        var withFlat = TermDepositDecider.DecideInterestPayment(
            position, windowStart, windowEnd, DayCountConvention.Act360, IrsBps, RateSchedule.Flat(325));

        Assert.Equal(withoutSchedule, withFlat);
    }

    // ---- F.10: a crescente broken mid-vector accrues only the steps it reached --------------------

    [Fact]
    public void DecideEarlyTermination_stepUp_accrues_only_the_elapsed_steps()
    {
        // crescente [0→2%, 180→4%], broken at day 200: 180 days @ 2% + 20 days @ 4% — not the full
        // second leg. Penalty 100% of accrued (flat policy). The accrued flow folds the clipped vector.
        var position = AtMaturityPosition(tanBps: 0, termDays: 360);
        var schedule = RateSchedule.StepUp([new RateSegment(0, 200), new RateSegment(180, 400)]);
        var policy = EarlyTerminationPolicy.Flat(penaltyBasisPoints: 10_000, PenaltyBasis.AccruedInterest);

        var events = TermDepositDecider.DecideEarlyTermination(
            position, Start.AddDays(200), policy, DayCountConvention.Act360, IrsBps, Reason, schedule);

        var accrued = (InterestAccrued)events[0];
        var expected = schedule.AccrueGross(
            new Money(PrincipalCents), Start, Start.AddDays(200), DayCountConvention.Act360);
        Assert.Equal(expected, accrued.GrossInterest);
    }
}
