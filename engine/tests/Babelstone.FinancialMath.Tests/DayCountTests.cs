using Babelstone.FinancialMath;
using Xunit;

namespace Babelstone.FinancialMath.Tests;

public class DayCountTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    // --- Basis: each convention pins its days-in-year denominator (fin-math §2.2). ---

    [Theory]
    [InlineData(DayCountConvention.Act360, 360)]
    [InlineData(DayCountConvention.Act365, 365)]
    [InlineData(DayCountConvention.Thirty360European, 360)]
    public void Basis_is_360_or_365_per_convention(DayCountConvention convention, int expectedBasis) =>
        Assert.Equal(expectedBasis, DayCount.Between(D(2025, 1, 1), D(2025, 6, 1), convention).Basis);

    // --- Actual conventions count calendar days exactly, including leap days. ---

    [Theory]
    // fin-math §5.1 worked example: a deposit spanning a full 365-day (non-leap) year.
    [InlineData(2023, 1, 1, 2024, 1, 1, 365)]
    // Leap year: 2024 contains Feb 29, so the same span is one day longer.
    [InlineData(2024, 1, 1, 2025, 1, 1, 366)]
    // fin-math §8.4 example: January as a 31-day period.
    [InlineData(2025, 1, 1, 2025, 2, 1, 31)]
    // February counted actually: 28 days in a non-leap year.
    [InlineData(2025, 2, 1, 2025, 3, 1, 28)]
    // The leap day itself is counted.
    [InlineData(2024, 2, 1, 2024, 3, 1, 29)]
    public void Actual_conventions_count_calendar_days(
        int y1, int m1, int d1, int y2, int m2, int d2, int expectedDays)
    {
        Assert.Equal(expectedDays, DayCount.Between(D(y1, m1, d1), D(y2, m2, d2), DayCountConvention.Act360).Days);
        Assert.Equal(expectedDays, DayCount.Between(D(y1, m1, d1), D(y2, m2, d2), DayCountConvention.Act365).Days);
    }

    [Fact]
    public void Act360_over_a_full_year_yields_factor_above_one()
    {
        // The PT quirk (fin-math §5.1): 365 actual days over a 360-day basis.
        var f = DayCount.Between(D(2023, 1, 1), D(2024, 1, 1), DayCountConvention.Act360);
        Assert.Equal(365, f.Days);
        Assert.Equal(360, f.Basis);
        Assert.True(f.Days > f.Basis);
    }

    // --- 30E/360 (European): months are 30 days, both day-figures capped at 30. ---

    [Theory]
    // A clean one-month span is exactly 30 days.
    [InlineData(2025, 1, 15, 2025, 2, 15, 30)]
    // A clean calendar year is exactly 360 days.
    [InlineData(2025, 1, 1, 2026, 1, 1, 360)]
    // 31st caps to 30: Jan 31 → Mar 31 = 30·2 + (30 − 30) = 60.
    [InlineData(2025, 1, 31, 2025, 3, 31, 60)]
    // End-of-Feb is NOT extended (the European variant's distinguishing trait):
    // Jan 31 → Feb 28 = 30·1 + (28 − 30) = 28.
    [InlineData(2025, 1, 31, 2025, 2, 28, 28)]
    // Jan 30 → Feb 28 = 30·1 + (28 − 30) = 28.
    [InlineData(2025, 1, 30, 2025, 2, 28, 28)]
    // Spanning a leap-year Feb makes no difference under 30/360.
    [InlineData(2024, 1, 31, 2024, 2, 29, 29)]
    public void Thirty360European_caps_day_of_month_at_30(
        int y1, int m1, int d1, int y2, int m2, int d2, int expectedDays) =>
        Assert.Equal(
            expectedDays,
            DayCount.Between(D(y1, m1, d1), D(y2, m2, d2), DayCountConvention.Thirty360European).Days);

    // --- Total and additive: zero, sign, and interval composition (all conventions). ---

    [Theory]
    [InlineData(DayCountConvention.Act360)]
    [InlineData(DayCountConvention.Act365)]
    [InlineData(DayCountConvention.Thirty360European)]
    public void Equal_dates_yield_zero_days(DayCountConvention convention) =>
        Assert.Equal(0, DayCount.Between(D(2025, 3, 14), D(2025, 3, 14), convention).Days);

    [Theory]
    [InlineData(DayCountConvention.Act360)]
    [InlineData(DayCountConvention.Act365)]
    [InlineData(DayCountConvention.Thirty360European)]
    public void Reversed_interval_negates_day_count(DayCountConvention convention)
    {
        var start = D(2025, 1, 10);
        var end = D(2025, 7, 20);
        int forward = DayCount.Between(start, end, convention).Days;
        int backward = DayCount.Between(end, start, convention).Days;
        Assert.Equal(forward, -backward);
    }

    [Theory]
    [InlineData(DayCountConvention.Act360)]
    [InlineData(DayCountConvention.Act365)]
    [InlineData(DayCountConvention.Thirty360European)]
    public void Adjacent_intervals_sum_to_the_whole(DayCountConvention convention)
    {
        var start = D(2025, 1, 1);
        var mid = D(2025, 4, 15);
        var end = D(2025, 11, 30);
        int whole = DayCount.Between(start, end, convention).Days;
        int first = DayCount.Between(start, mid, convention).Days;
        int second = DayCount.Between(mid, end, convention).Days;
        Assert.Equal(whole, first + second);
    }

    [Fact]
    public void Unknown_convention_throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => DayCount.Between(D(2025, 1, 1), D(2025, 2, 1), (DayCountConvention)999));
        Assert.Equal("convention", ex.ParamName);
    }
}
