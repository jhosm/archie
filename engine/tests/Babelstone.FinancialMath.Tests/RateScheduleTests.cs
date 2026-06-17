using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.FinancialMath.Tests;

/// <summary>
/// The resolved rate-VECTOR primitive (F.10): step-up (<i>crescente</i>) and amount-tiered
/// (<i>escalonada</i>) rate schedules folded over the simple-interest engine. Pure, Docker-free.
/// The load-bearing property is the EQUIVALENCE guarantee — a one-segment vector of any kind
/// accrues exactly what <see cref="Accrual.SimpleInterest"/> does, so the schedule is a faithful
/// generalisation of the flat path (no rounding gap introduced by the vector machinery).
/// </summary>
public class RateScheduleTests
{
    private static readonly Money TenThousand = new(1_000_000L);
    private static readonly DateOnly Start = new(2026, 1, 1);

    // --- equivalence: a one-segment vector == flat SimpleInterest -------------------------------

    [Fact]
    public void Flat_schedule_equals_SimpleInterest_over_the_same_interval()
    {
        var end = Start.AddDays(365);
        var schedule = RateSchedule.Flat(600);

        var viaSchedule = schedule.AccrueGross(TenThousand, Start, end, DayCountConvention.Act360);
        var viaSimple = Accrual.SimpleInterest(TenThousand, 600, DayCount.Between(Start, end, DayCountConvention.Act360));

        Assert.Equal(viaSimple, viaSchedule);
        Assert.Equal(60_833L, viaSchedule.Cents); // the §5.1 worked example
    }

    [Fact]
    public void Single_segment_stepUp_equals_flat_SimpleInterest()
    {
        var end = Start.AddDays(365);
        var schedule = RateSchedule.StepUp([new RateSegment(0, 600)]);

        Assert.Equal(
            Accrual.SimpleInterest(TenThousand, 600, DayCount.Between(Start, end, DayCountConvention.Act360)),
            schedule.AccrueGross(TenThousand, Start, end, DayCountConvention.Act360));
    }

    [Fact]
    public void Single_tranche_amountTiered_equals_flat_SimpleInterest()
    {
        var end = Start.AddDays(365);
        var schedule = RateSchedule.AmountTiered([new RateSegment(0, 600)]);

        Assert.Equal(
            Accrual.SimpleInterest(TenThousand, 600, DayCount.Between(Start, end, DayCountConvention.Act360)),
            schedule.AccrueGross(TenThousand, Start, end, DayCountConvention.Act360));
    }

    // --- step-up (crescente): rate rises across sub-periods of the term ------------------------

    [Fact]
    public void StepUp_sums_each_sub_period_at_its_own_rate()
    {
        // Two-step crescente over a 360-day term: first 180 days at 2%, next 180 days at 4%.
        // First leg : 1,000,000 × 200 × 180 / (360×10000) = 10,000.00
        // Second leg: 1,000,000 × 400 × 180 / (360×10000) = 20,000.00
        // Total gross = 30,000 cents (€300.00).
        var schedule = RateSchedule.StepUp([new RateSegment(0, 200), new RateSegment(180, 400)]);
        var end = Start.AddDays(360);

        var gross = schedule.AccrueGross(TenThousand, Start, end, DayCountConvention.Act360);
        Assert.Equal(30_000L, gross.Cents);
    }

    [Fact]
    public void StepUp_rounds_once_at_the_boundary_not_per_sub_period()
    {
        // Pick rates/days that round per-leg differently than once-at-the-end. Three 121-day legs
        // (363 days) at 333, 667, 999 bps on €10,000, Act/360. Computing each leg, rounding, then
        // summing would diverge from the one-boundary fold; the schedule must match the latter.
        var schedule = RateSchedule.StepUp(
        [
            new RateSegment(0, 333),
            new RateSegment(121, 667),
            new RateSegment(242, 999),
        ]);
        var end = Start.AddDays(363);

        decimal exact = 0m;
        exact += (decimal)TenThousand.Cents * 333 * 121 / (360m * 10_000);
        exact += (decimal)TenThousand.Cents * 667 * 121 / (360m * 10_000);
        exact += (decimal)TenThousand.Cents * 999 * 121 / (360m * 10_000);

        Assert.Equal(Money.FromCents(exact), schedule.AccrueGross(TenThousand, Start, end, DayCountConvention.Act360));
    }

    [Fact]
    public void StepUp_clips_the_last_segment_when_the_deposit_ends_mid_vector()
    {
        // A deposit broken at day 200 of a [0→2%, 180→4%] crescente accrues 180 days @ 2% + only
        // 20 days @ 4%, not the full second leg.
        var schedule = RateSchedule.StepUp([new RateSegment(0, 200), new RateSegment(180, 400)]);

        // First leg : 1,000,000 × 200 × 180 / (360×10000) = 10,000.00
        // Partial 2nd: 1,000,000 × 400 ×  20 / (360×10000) =  2,222.22 → rounds in the single sum.
        decimal exact = (decimal)TenThousand.Cents * 200 * 180 / (360m * 10_000)
                      + (decimal)TenThousand.Cents * 400 * 20 / (360m * 10_000);

        Assert.Equal(
            Money.FromCents(exact),
            schedule.AccrueGross(TenThousand, Start, Start.AddDays(200), DayCountConvention.Act360));
    }

    [Fact]
    public void StepUp_terminated_before_the_second_step_only_accrues_the_first_rate()
    {
        // Broken at day 90 — entirely within the first segment, so it accrues at 2% alone.
        var schedule = RateSchedule.StepUp([new RateSegment(0, 200), new RateSegment(180, 400)]);
        var flat = Accrual.SimpleInterest(
            TenThousand, 200, DayCount.Between(Start, Start.AddDays(90), DayCountConvention.Act360));

        Assert.Equal(flat, schedule.AccrueGross(TenThousand, Start, Start.AddDays(90), DayCountConvention.Act360));
    }

    // --- amount-tiered (escalonada): rate depends on the principal band ------------------------

    [Fact]
    public void AmountTiered_prices_each_principal_tranche_at_its_rate()
    {
        // Marginal tiering over a 360-day term, Act/360: first €5,000 at 2%, the rest at 4%.
        // Principal €10,000 → tranche 1 = 500,000c @ 2%, tranche 2 = 500,000c @ 4%.
        // Leg 1: 500,000 × 200 × 360 / (360×10000) = 10,000.00
        // Leg 2: 500,000 × 400 × 360 / (360×10000) = 20,000.00 → total 30,000c.
        var schedule = RateSchedule.AmountTiered([new RateSegment(0, 200), new RateSegment(500_000, 400)]);
        var end = Start.AddDays(360);

        Assert.Equal(30_000L, schedule.AccrueGross(TenThousand, Start, end, DayCountConvention.Act360).Cents);
    }

    [Fact]
    public void AmountTiered_principal_below_the_second_tranche_only_uses_the_first_rate()
    {
        // €3,000 principal never reaches the €5,000 boundary, so the whole principal accrues at 2%.
        var schedule = RateSchedule.AmountTiered([new RateSegment(0, 200), new RateSegment(500_000, 400)]);
        var threeThousand = new Money(300_000L);
        var end = Start.AddDays(360);

        Assert.Equal(
            Accrual.SimpleInterest(threeThousand, 200, DayCount.Between(Start, end, DayCountConvention.Act360)),
            schedule.AccrueGross(threeThousand, Start, end, DayCountConvention.Act360));
    }

    [Fact]
    public void AmountTiered_prices_a_coupon_WINDOW_on_the_windows_day_count_not_the_full_term()
    {
        // escalonada is principal-indexed, so a PERIODIC coupon window must tier the principal over
        // exactly the WINDOW's day count — not the whole term. This pins the AccrueGrossWindow
        // AmountTiered branch, which every full-term AccrueGross test would miss: a mutation that
        // fed the full term instead of the window would inflate the coupon and go uncaught.
        var schedule = RateSchedule.AmountTiered([new RateSegment(0, 200), new RateSegment(500_000, 400)]);
        var windowStart = Start.AddDays(31);
        var windowEnd = Start.AddDays(62); // a 31-day coupon window partway through the term

        // The tiered gross over JUST the window: each €5,000 tranche over 31 days, Act/360.
        var windowFactor = DayCount.Between(windowStart, windowEnd, DayCountConvention.Act360);
        decimal exact = (decimal)500_000L * 200 * windowFactor.Days / ((decimal)windowFactor.Basis * 10_000)
                      + (decimal)500_000L * 400 * windowFactor.Days / ((decimal)windowFactor.Basis * 10_000);

        var gross = schedule.AccrueGrossWindow(
            TenThousand, Start, windowStart, windowEnd, DayCountConvention.Act360);

        Assert.Equal(Money.FromCents(exact), gross);
        // And strictly less than the full-term tiered accrual — proving it priced the window, not the term.
        Assert.True(gross.Cents < schedule.AccrueGross(TenThousand, Start, Start.AddDays(360), DayCountConvention.Act360).Cents);
    }

    // --- step-up is defined only for actual-day conventions (v1 scope) --------------------------

    [Fact]
    public void StepUp_on_a_thirty360_day_count_fails_loud_rather_than_mis_attributing_days()
    {
        // Step-up boundaries are ELAPSED days; on 30/360 DayCount.Between returns adjusted days, so
        // a day-indexed boundary would mean the wrong calendar day. v1 rejects it fail-loud.
        var schedule = RateSchedule.StepUp([new RateSegment(0, 200), new RateSegment(180, 400)]);
        var end = Start.AddDays(360);

        var ex = Assert.Throws<ArgumentException>(() =>
            schedule.AccrueGross(TenThousand, Start, end, DayCountConvention.Thirty360European));
        Assert.Contains("actual-day", ex.Message);
    }

    [Fact]
    public void AmountTiered_on_a_thirty360_day_count_is_allowed_principal_indexed_not_day_indexed()
    {
        // Amount-tiered boundaries are principal cents, not days, so the 30/360 restriction that
        // applies to step-up does NOT apply here — it accrues without throwing.
        var schedule = RateSchedule.AmountTiered([new RateSegment(0, 200), new RateSegment(500_000, 400)]);
        var end = Start.AddDays(360);

        var gross = schedule.AccrueGross(TenThousand, Start, end, DayCountConvention.Thirty360European);
        Assert.True(gross.Cents > 0); // priced fine on a 30/360 convention
    }

    // --- well-formedness guards ----------------------------------------------------------------

    [Fact]
    public void StepUp_rejects_a_vector_whose_first_segment_is_not_zero()
    {
        var ex = Assert.Throws<ArgumentException>(() => RateSchedule.StepUp([new RateSegment(30, 200)]));
        Assert.Contains("must start at 0", ex.Message);
    }

    [Fact]
    public void StepUp_rejects_non_ascending_boundaries()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            RateSchedule.StepUp([new RateSegment(0, 200), new RateSegment(180, 400), new RateSegment(90, 600)]));
        Assert.Contains("strictly ascend", ex.Message);
    }

    [Fact]
    public void AccrueGross_rejects_a_reversed_interval()
    {
        var schedule = RateSchedule.StepUp([new RateSegment(0, 200), new RateSegment(180, 400)]);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            schedule.AccrueGross(TenThousand, Start, Start.AddDays(-5), DayCountConvention.Act360));
    }

    [Fact]
    public void AccrueGross_is_a_deterministic_pure_function()
    {
        var schedule = RateSchedule.StepUp([new RateSegment(0, 200), new RateSegment(180, 400)]);
        var end = Start.AddDays(365);

        Assert.Equal(
            schedule.AccrueGross(TenThousand, Start, end, DayCountConvention.Act360),
            schedule.AccrueGross(TenThousand, Start, end, DayCountConvention.Act360));
    }
}
