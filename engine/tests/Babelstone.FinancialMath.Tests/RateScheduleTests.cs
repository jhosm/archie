using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.FinancialMath.Tests;

/// <summary>
/// The resolved rate-VECTOR primitive (F.10): step-up and amount-tiered
/// rate schedules folded over the simple-interest engine. Pure, Docker-free.
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

    // --- step-up: rate rises across sub-periods of the term -----------------------------------

    [Fact]
    public void StepUp_sums_each_sub_period_at_its_own_rate()
    {
        // Two-step step-up over a 360-day term: first 180 days at 2%, next 180 days at 4%.
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
        // A deposit broken at day 200 of a [0→2%, 180→4%] step-up accrues 180 days @ 2% + only
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

    // --- amount-tiered: rate depends on the principal band ------------------------------------

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
        // Amount-tiered is principal-indexed, so a PERIODIC coupon window must tier the principal over
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

    // --- principal timeline (F.12 partial withdrawal): accrue over a step-function principal --------
    //
    // The load-bearing property mirrors the rate-vector one above: a SINGLE-segment timeline accrues
    // exactly what the single-principal AccrueGrossWindow does (a no-withdrawal deposit is unchanged),
    // and a multi-segment timeline prices each sub-period on the principal ACTUALLY held — exact, not a
    // conservative whole-window re-base — while still crossing to Money once.

    private static readonly Money FortyThousand = new(4_000_000L);
    private static readonly Money ThirtyThousand = new(3_000_000L);

    [Fact]
    public void Single_segment_principalTimeline_equals_AccrueGross()
    {
        var end = Start.AddDays(365);
        var schedule = RateSchedule.Flat(600);
        IReadOnlyList<PrincipalSegment> timeline = [new PrincipalSegment(Start, FortyThousand)];

        Assert.Equal(
            schedule.AccrueGross(FortyThousand, Start, end, DayCountConvention.Act360),
            schedule.AccrueGrossWindowOverPrincipal(timeline, Start, Start, end, DayCountConvention.Act360));
    }

    [Theory]
    [InlineData(600)]   // step-up one-segment and amount-tiered one-segment also reduce to the flat path
    public void Single_segment_principalTimeline_matches_every_schedule_kind(int bps)
    {
        var end = Start.AddDays(200);
        IReadOnlyList<PrincipalSegment> timeline = [new PrincipalSegment(Start, FortyThousand)];

        foreach (var schedule in new[]
                 {
                     RateSchedule.Flat(bps),
                     RateSchedule.StepUp([new RateSegment(0, bps)]),
                     RateSchedule.AmountTiered([new RateSegment(0, bps)]),
                 })
        {
            Assert.Equal(
                schedule.AccrueGross(FortyThousand, Start, end, DayCountConvention.Act360),
                schedule.AccrueGrossWindowOverPrincipal(timeline, Start, Start, end, DayCountConvention.Act360));
        }
    }

    [Fact]
    public void PrincipalTimeline_prices_each_segment_on_the_principal_actually_held()
    {
        // €40,000 held for 120 days, then €30,000 (a €10,000 withdrawal on day 120) for 245 more —
        // a 365-day Act/360 term at a flat 6%. Exact piecewise:
        //   leg 1: 4,000,000 × 600 × 120 / (360×10000) =  80,000 cents
        //   leg 2: 3,000,000 × 600 × 245 / (360×10000) = 122,500 cents  → total 202,500 cents (€2,025.00)
        var schedule = RateSchedule.Flat(600);
        var withdrawalDay = Start.AddDays(120);
        var maturity = Start.AddDays(365);
        IReadOnlyList<PrincipalSegment> timeline =
        [
            new PrincipalSegment(Start, FortyThousand),
            new PrincipalSegment(withdrawalDay, ThirtyThousand),
        ];

        var gross = schedule.AccrueGrossWindowOverPrincipal(timeline, Start, Start, maturity, DayCountConvention.Act360);

        Assert.Equal(202_500L, gross.Cents);
        // It sits strictly between the two wrong answers it replaces: accruing the WHOLE term on the
        // original €40,000 (243,333, the over-payment this fix removes) and on the reduced €30,000
        // (182,500, the conservative under-payment a naive whole-window re-base would give).
        Assert.True(gross.Cents < schedule.AccrueGross(FortyThousand, Start, maturity, DayCountConvention.Act360).Cents);
        Assert.True(gross.Cents > schedule.AccrueGross(ThirtyThousand, Start, maturity, DayCountConvention.Act360).Cents);
    }

    [Fact]
    public void PrincipalTimeline_equals_the_DailyBalanceInterest_primitive()
    {
        // The flat principal-timeline path is exactly Accrual.DailyBalanceInterest over the same
        // (balance, days) step function — ties the new method to the already-tested §8.2 primitive.
        var schedule = RateSchedule.Flat(600);
        var withdrawalDay = Start.AddDays(120);
        var maturity = Start.AddDays(365);
        IReadOnlyList<PrincipalSegment> timeline =
        [
            new PrincipalSegment(Start, FortyThousand),
            new PrincipalSegment(withdrawalDay, ThirtyThousand),
        ];

        var viaTimeline = schedule.AccrueGrossWindowOverPrincipal(timeline, Start, Start, maturity, DayCountConvention.Act360);
        var viaDailyBalance = Accrual.DailyBalanceInterest(
            [(FortyThousand, 120), (ThirtyThousand, 245)], 600, 360);

        Assert.Equal(viaDailyBalance, viaTimeline);
    }

    [Fact]
    public void PrincipalTimeline_rounds_once_across_segments_not_per_segment()
    {
        // Principals/days chosen so each leg has a fractional-cent numerator; the once-at-end cross to
        // Money must equal FromCents(sum of the raw decimals), never the sum of per-leg FromCents.
        var schedule = RateSchedule.Flat(575);
        var d1 = Start.AddDays(101);
        var maturity = Start.AddDays(242);
        var p1 = new Money(4_000_001L);
        var p2 = new Money(2_500_003L);
        IReadOnlyList<PrincipalSegment> timeline = [new PrincipalSegment(Start, p1), new PrincipalSegment(d1, p2)];

        decimal raw1 = (decimal)p1.Cents * 575 * 101 / (360m * 10_000);
        decimal raw2 = (decimal)p2.Cents * 575 * 141 / (360m * 10_000);
        Assert.Equal(
            Money.FromCents(raw1 + raw2),
            schedule.AccrueGrossWindowOverPrincipal(timeline, Start, Start, maturity, DayCountConvention.Act360));
    }

    [Fact]
    public void PrincipalTimeline_skips_a_change_that_lands_after_the_window()
    {
        // A coupon window [Start, Start+30) accrues only on the principal in force during it; a
        // withdrawal on day 120 does not touch an earlier coupon.
        var schedule = RateSchedule.Flat(600);
        var couponEnd = Start.AddDays(30);
        IReadOnlyList<PrincipalSegment> timeline =
        [
            new PrincipalSegment(Start, FortyThousand),
            new PrincipalSegment(Start.AddDays(120), ThirtyThousand),
        ];

        Assert.Equal(
            schedule.AccrueGross(FortyThousand, Start, couponEnd, DayCountConvention.Act360),
            schedule.AccrueGrossWindowOverPrincipal(timeline, Start, Start, couponEnd, DayCountConvention.Act360));
    }

    [Fact]
    public void PrincipalTimeline_over_a_coupon_window_uses_the_post_withdrawal_principal()
    {
        // A coupon window [Start+150, Start+180) that opens AFTER a day-120 withdrawal accrues on the
        // reduced €30,000 — the principal in force across that whole window.
        var schedule = RateSchedule.Flat(600);
        var winStart = Start.AddDays(150);
        var winEnd = Start.AddDays(180);
        IReadOnlyList<PrincipalSegment> timeline =
        [
            new PrincipalSegment(Start, FortyThousand),
            new PrincipalSegment(Start.AddDays(120), ThirtyThousand),
        ];

        Assert.Equal(
            schedule.AccrueGross(ThirtyThousand, winStart, winEnd, DayCountConvention.Act360),
            schedule.AccrueGrossWindowOverPrincipal(timeline, Start, winStart, winEnd, DayCountConvention.Act360));
    }

    [Fact]
    public void PrincipalTimeline_composes_a_stepUp_rate_with_a_multi_segment_principal()
    {
        // The cross-term: a time-varying RATE (step-up) AND a time-varying PRINCIPAL (a withdrawal).
        // Step-up 2% for the first 180 days, then 4%. Principal €40,000 for 120 days, then €30,000 (a
        // day-120 withdrawal) for 120 more. Each principal segment re-anchors at the deposit start, so
        // the rate in force across its sub-window is attributed correctly:
        //   €40,000 over [0,120) — all at 2%:               4,000,000×200×120/(360×10000) = 26,666.67c
        //   €30,000 over [120,180) at 2% + [180,240) at 4%: 3,000,000×200×60/… (10,000) + 3,000,000×400×60/… (20,000) = 30,000.00c
        //   summed un-rounded → 56,666.67 → 56,667c.
        var schedule = RateSchedule.StepUp([new RateSegment(0, 200), new RateSegment(180, 400)]);
        var withdrawalDay = Start.AddDays(120);
        var windowEnd = Start.AddDays(240);
        IReadOnlyList<PrincipalSegment> timeline =
        [
            new PrincipalSegment(Start, FortyThousand),
            new PrincipalSegment(withdrawalDay, ThirtyThousand),
        ];

        var gross = schedule.AccrueGrossWindowOverPrincipal(timeline, Start, Start, windowEnd, DayCountConvention.Act360);
        Assert.Equal(56_667L, gross.Cents);
    }

    [Fact]
    public void PrincipalTimeline_rejects_an_empty_timeline()
    {
        var schedule = RateSchedule.Flat(600);
        Assert.Throws<ArgumentException>(() =>
            schedule.AccrueGrossWindowOverPrincipal([], Start, Start, Start.AddDays(365), DayCountConvention.Act360));
    }
}
