using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.FinancialMath.Tests;

/// <summary>
/// B.10 mutation backstop for <see cref="RateSchedule"/>'s GUARD and BOUNDARY logic — the edges the
/// equivalence-property tests in <see cref="RateScheduleTests"/> leave unpinned: the construction
/// invariants (non-empty, strictly-ascending boundaries), the reversed-interval guard on each of the
/// three accrual branches (flat, step-up, amount-tiered), the amount-tiered tranche bounds
/// (skip a tranche above the principal; clip the top in-range tranche to it), and the F.12
/// multi-principal window clamp (each principal segment accrues only over its overlap with the
/// accrual window). Pure, Docker-free; all monetary expectations are exact integer cents.
/// </summary>
public class RateScheduleBoundaryTests
{
    private static readonly Money Principal = new(100_000L);          // 1000.00
    private static readonly DateOnly Start = new(2026, 1, 1);
    private const DayCountConvention Act360 = DayCountConvention.Act360;

    // --- construction invariants (Validated) -----------------------------------------------------

    [Fact]
    public void A_schedule_needs_at_least_one_segment()
    {
        Assert.Throws<ArgumentException>(() => RateSchedule.StepUp([]));
        Assert.Throws<ArgumentException>(() => RateSchedule.AmountTiered([]));
    }

    [Fact]
    public void Segment_boundaries_must_strictly_ascend()
    {
        // Two segments sharing the boundary 0 are NOT strictly ascending (kills <= → <): a
        // non-ascending vector mis-resolves which segment covers a point.
        Assert.Throws<ArgumentException>(
            () => RateSchedule.StepUp([new RateSegment(0, 600), new RateSegment(0, 700)]));
    }

    // --- reversed-interval guard on every accrual branch -----------------------------------------

    [Fact]
    public void Flat_accrual_rejects_a_reversed_interval()
    {
        var schedule = RateSchedule.Flat(600);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => schedule.AccrueGross(Principal, Start, Start.AddDays(-1), Act360));
    }

    [Fact]
    public void StepUp_accrual_rejects_a_reversed_window()
    {
        var schedule = RateSchedule.StepUp([new RateSegment(0, 600)]);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => schedule.AccrueGross(Principal, Start, Start.AddDays(-1), Act360));
    }

    [Fact]
    public void AmountTiered_accrual_rejects_a_reversed_interval()
    {
        var schedule = RateSchedule.AmountTiered([new RateSegment(0, 600)]);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => schedule.AccrueGross(Principal, Start, Start.AddDays(-1), Act360));
    }

    // --- zero-day-window accrual (start == end) on every branch: 0, never a throw ----------------
    // A zero-length interval accrues nothing and must NOT trip the reversed-interval guard:
    // factor.Days == 0 is not < 0, so accrual returns Money.Zero rather than throwing. These pin the
    // `factor.Days < 0` boundary on each of the three accrual branches (flat → SimpleInterestRaw,
    // step-up → AccrueStepUpWindowRaw, amount-tiered → AccrueAmountTieredRaw), killing the `< 0` → `<= 0`
    // mutant that would otherwise wrongly throw on a legitimate zero-day window. (The strictly-negative
    // case is already pinned by the reversed-interval tests above; this is the OTHER side of the guard.)

    [Fact]
    public void Flat_accrual_of_a_zero_day_window_is_zero_and_does_not_throw()
    {
        var schedule = RateSchedule.Flat(600);
        Assert.Equal(Money.Zero, schedule.AccrueGross(Principal, Start, Start, Act360));
    }

    [Fact]
    public void StepUp_accrual_of_a_zero_day_window_is_zero_and_does_not_throw()
    {
        var schedule = RateSchedule.StepUp([new RateSegment(0, 600)]);
        Assert.Equal(Money.Zero, schedule.AccrueGross(Principal, Start, Start, Act360));
    }

    [Fact]
    public void AmountTiered_accrual_of_a_zero_day_window_is_zero_and_does_not_throw()
    {
        var schedule = RateSchedule.AmountTiered([new RateSegment(0, 600)]);
        Assert.Equal(Money.Zero, schedule.AccrueGross(Principal, Start, Start, Act360));
    }

    // --- amount-tiered tranche bounds ------------------------------------------------------------

    [Fact]
    public void AmountTiered_skips_a_tranche_whose_boundary_is_at_or_above_the_principal()
    {
        // A 5000.00 boundary the 1000.00 principal never reaches → that tranche is skipped, and the
        // whole principal accrues at the first tranche's rate (kills `trancheFrom >= principal` break).
        var schedule = RateSchedule.AmountTiered([new RateSegment(0, 600), new RateSegment(200_000, 800)]);
        var end = Start.AddDays(365);

        Assert.Equal(new Money(6_083L), schedule.AccrueGross(Principal, Start, end, Act360));
    }

    [Fact]
    public void AmountTiered_skips_a_tranche_whose_boundary_is_exactly_the_principal()
    {
        // A boundary sitting EXACTLY at the 1000.00 principal opens a zero-width top tranche that
        // contributes nothing, so the whole principal still accrues at the first tranche's rate — the
        // same 6083 as the strictly-above case. Pins the marginal-tiering edge right at the principal.
        var schedule = RateSchedule.AmountTiered([new RateSegment(0, 600), new RateSegment(100_000, 800)]);
        var end = Start.AddDays(365);

        Assert.Equal(new Money(6_083L), schedule.AccrueGross(Principal, Start, end, Act360));
    }

    [Fact]
    public void AmountTiered_clips_the_top_in_range_tranche_to_the_principal()
    {
        // Boundaries 0 / 500.00 / 2000.00 on a 1000.00 principal: tranche [0,500) @600, tranche
        // [500, 2000) clipped to [500,1000) @800, tranche from 2000.00 skipped. 50000@600 + 50000@800.
        var schedule = RateSchedule.AmountTiered(
            [new RateSegment(0, 600), new RateSegment(50_000, 800), new RateSegment(200_000, 1_000)]);
        var end = Start.AddDays(365);

        Assert.Equal(new Money(7_097L), schedule.AccrueGross(Principal, Start, end, Act360));
    }

    // --- F.12 multi-principal window clamp -------------------------------------------------------

    [Fact]
    public void Multi_principal_timeline_accrues_each_segment_over_its_window_overlap()
    {
        // A partial withdrawal at day 100 steps the principal 1000.00 → 500.00. Over the window
        // [day0, day200] the first 100 days price 1000.00 @600 and the next 100 days price 500.00 @600.
        // 100000·600·100/(360·10000) + 50000·600·100/(360·10000) = 1666.66… + 833.33… = 2500 exactly.
        var schedule = RateSchedule.Flat(600);
        var timeline = new[]
        {
            new PrincipalSegment(Start, new Money(100_000L)),
            new PrincipalSegment(Start.AddDays(100), new Money(50_000L)),
        };

        var accrued = schedule.AccrueGrossWindowOverPrincipal(
            timeline, anchorStart: Start, windowStart: Start, windowEnd: Start.AddDays(200), Act360);

        Assert.Equal(new Money(2_500L), accrued);
    }

    [Fact]
    public void Multi_principal_timeline_rejects_an_empty_timeline()
    {
        var schedule = RateSchedule.Flat(600);
        Assert.Throws<ArgumentException>(
            () => schedule.AccrueGrossWindowOverPrincipal([], Start, Start, Start.AddDays(200), Act360));
    }
}
