using Babelstone.FinancialMath;
using Xunit;

namespace Babelstone.FinancialMath.Tests;

public class RatesTests
{
    // --- TAE — effective annual rate (fin-math §5.4): (1 + TAN/m)^m − 1. ---

    [Fact]
    public void Tae_reproduces_the_fin_math_5_4_worked_example()
    {
        // TAN 6% (600 bps), monthly capitalization: (1 + 0.06/12)^12 − 1 ≈ 0.061678 (6.17%).
        // The 17-bps gap over the 6% TAN is the compounding effect the doc highlights.
        decimal tae = Rates.Tae(tanBps: 600, periodsPerYear: 12);

        // Assert via xUnit's precision overload (rounds inside the assertion) rather than
        // Math.Round on the decimal — BMNY001 reserves decimal rounding for Money.FromCents.
        Assert.Equal(0.0617m, tae, 4); // 6.17% to the doc's stated precision
    }

    [Fact]
    public void Tae_equals_tan_when_interest_is_not_compounded_intra_year()
    {
        // m = 1 (interest at maturity, no intra-period capitalization): TAE = TAN exactly,
        // (1 + 0.06)^1 − 1 = 0.06. The §5.4 degenerate case where the formula "doesn't matter".
        Assert.Equal(0.06m, Rates.Tae(tanBps: 600, periodsPerYear: 1));
    }

    [Fact]
    public void Tae_grows_with_compounding_frequency()
    {
        // The §5.4 claim "the gap grows with m": at a fixed TAN, more frequent compounding
        // yields a strictly higher effective rate. annual < monthly < daily.
        decimal annual = Rates.Tae(600, 1);
        decimal monthly = Rates.Tae(600, 12);
        decimal daily = Rates.Tae(600, 365);

        Assert.True(annual < monthly);
        Assert.True(monthly < daily);
    }

    [Fact]
    public void Tae_with_a_negative_rate_is_negative()
    {
        // Consistent with Accrual: a negative TAN annualizes to a negative effective rate,
        // emitted rather than rejected (negative-rate environments).
        Assert.True(Rates.Tae(tanBps: -600, periodsPerYear: 12) < 0m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-12)]
    public void Tae_rejects_a_non_positive_compounding_frequency(int periodsPerYear)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Rates.Tae(600, periodsPerYear));
        Assert.Equal("periodsPerYear", ex.ParamName);
    }

    // --- Annualize — the (1 + r)^m − 1 identity TAE and TAEG both fold through. ---

    [Fact]
    public void Annualize_of_a_monthly_periodic_rate_matches_the_irr_6_1_example()
    {
        // fin-math §6.1: a per-month IRR of 0.005 corresponds to TAE = (1.005)^12 − 1 ≈ 6.17%.
        // This is the path the TAEG takes — annualize a solved per-period rate.
        decimal annual = Rates.Annualize(periodicRate: 0.005m, periodsPerYear: 12);

        Assert.Equal(0.0617m, annual, 4);
        Assert.Equal(Rates.Tae(600, 12), annual); // same number, two doors in (§5.4 ≡ §6.1)
    }

    [Fact]
    public void Annualize_of_a_zero_rate_is_zero()
    {
        // (1 + 0)^m − 1 = 0 for any m: no growth annualizes to no effective rate.
        Assert.Equal(0m, Rates.Annualize(0m, 12));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Annualize_rejects_a_non_positive_compounding_frequency(int periodsPerYear)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Rates.Annualize(0.005m, periodsPerYear));
        Assert.Equal("periodsPerYear", ex.ParamName);
    }
}
