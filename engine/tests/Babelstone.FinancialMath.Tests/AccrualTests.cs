using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.FinancialMath.Tests;

public class AccrualTests
{
    // €10,000 as cents — the fin-math running example principal.
    private static readonly Money TenThousand = new(1_000_000L);

    // --- Simple interest (fin-math §5.1; ADR-PC-010 §P1 accrual form). ---

    [Fact]
    public void SimpleInterest_reproduces_the_fin_math_5_1_worked_example()
    {
        // C = €10,000, TAN 6% (600 bps), Act/360 over 365 actual days.
        // 1,000,000 × 600 × 365 / (360 × 10000) = 60,833.33… → €608.33, so M = €10,608.33.
        var factor = DayCount.Between(new DateOnly(2023, 1, 1), new DateOnly(2024, 1, 1), DayCountConvention.Act360);
        Money interest = Accrual.SimpleInterest(TenThousand, 600, factor);

        Assert.Equal(60_833L, interest.Cents);
        Assert.Equal(1_060_833L, (TenThousand + interest).Cents); // €10,608.33 maturity
    }

    [Fact]
    public void SimpleInterest_is_zero_over_zero_days()
    {
        var factor = new DayCountFactor(0, 360);
        Assert.Equal(Money.Zero, Accrual.SimpleInterest(TenThousand, 600, factor));
    }

    [Fact]
    public void SimpleInterest_rejects_a_reversed_interval()
    {
        var reversed = DayCount.Between(new DateOnly(2024, 1, 1), new DateOnly(2023, 1, 1), DayCountConvention.Act360);
        Assert.True(reversed.Days < 0);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Accrual.SimpleInterest(TenThousand, 600, reversed));
        Assert.Equal("factor", ex.ParamName);
    }

    // --- Compound interest (fin-math §5.2). ---

    [Fact]
    public void CompoundMaturity_reproduces_the_fin_math_5_2_monthly_example()
    {
        // C = €10,000, TAN 6% (600 bps), monthly compounding (m = 12), one year (12 periods).
        // M = 10,000 × (1.005)^12 = €10,616.78; TAE = (1.005)^12 − 1 ≈ 6.17% (§5.4).
        Money maturity = Accrual.CompoundMaturity(TenThousand, 600, periodsPerYear: 12, totalPeriods: 12);
        Assert.Equal(1_061_678L, maturity.Cents);
        Assert.Equal(61_678L, Accrual.CompoundInterest(TenThousand, 600, 12, 12).Cents);
    }

    [Fact]
    public void CompoundMaturity_over_zero_periods_returns_principal()
    {
        Assert.Equal(TenThousand, Accrual.CompoundMaturity(TenThousand, 600, 12, 0));
    }

    [Fact]
    public void CompoundMaturity_annual_compounding_matches_the_m1_special_case()
    {
        // m = 1 reduces to M = C × (1 + TAN)^n (§5.2): €10,000 × 1.06^2 = €11,236.00.
        Money maturity = Accrual.CompoundMaturity(TenThousand, 600, periodsPerYear: 1, totalPeriods: 2);
        Assert.Equal(1_123_600L, maturity.Cents);
    }

    [Theory]
    [InlineData(0, 12)]   // periodsPerYear must be positive
    [InlineData(-1, 12)]
    public void CompoundMaturity_rejects_non_positive_frequency(int periodsPerYear, int totalPeriods)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Accrual.CompoundMaturity(TenThousand, 600, periodsPerYear, totalPeriods));
        Assert.Equal("periodsPerYear", ex.ParamName);
    }

    [Fact]
    public void CompoundMaturity_rejects_negative_period_count()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Accrual.CompoundMaturity(TenThousand, 600, 12, -1));
        Assert.Equal("totalPeriods", ex.ParamName);
    }

    // --- Sum-of-daily-balances (fin-math §8.2 / §8.3). ---

    [Fact]
    public void DailyBalanceInterest_reproduces_the_fin_math_8_3_current_account_example()
    {
        // Credit TAN 0.5% (50 bps), Act/365, January. Σ S(d) = 1000·9 + 1500·10 + 200·12
        // = 26,400 → J = (0.005/365) × 26,400 = €0.36.
        var intervals = new (Money, int)[]
        {
            (new Money(100_000L), 9),   // €1,000 for Jan 01–09
            (new Money(150_000L), 10),  // €1,500 for Jan 10–19
            (new Money(20_000L), 12),   // €200   for Jan 20–31
        };
        Money interest = Accrual.DailyBalanceInterest(intervals, rateBps: 50, basis: 365);
        Assert.Equal(36L, interest.Cents);
    }

    [Fact]
    public void DailyBalanceInterest_over_no_intervals_is_zero() =>
        Assert.Equal(Money.Zero, Accrual.DailyBalanceInterest(Array.Empty<(Money, int)>(), 50, 365));

    [Fact]
    public void DailyBalanceInterest_rejects_non_positive_basis()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Accrual.DailyBalanceInterest(Array.Empty<(Money, int)>(), 50, 0));
        Assert.Equal("basis", ex.ParamName);
    }

    [Fact]
    public void DailyBalanceInterest_admits_a_zero_day_interval()
    {
        // A balance held zero days contributes nothing but is not a reversed interval: the
        // guard rejects days < 0, never days == 0. Pins the < boundary so a <= slip (which
        // would throw on the zero-day leg) is caught (B.10 mutation triage). Only the 10-day
        // leg accrues: (0.005/365) × (100,000 × 10) = 13.70… → 14 cents.
        var intervals = new (Money, int)[] { (new Money(100_000L), 0), (new Money(100_000L), 10) };
        Assert.Equal(14L, Accrual.DailyBalanceInterest(intervals, rateBps: 50, basis: 365).Cents);
    }

    [Fact]
    public void DailyBalanceInterest_rejects_a_negative_interval()
    {
        var bad = new (Money, int)[] { (new Money(100_000L), -1) };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Accrual.DailyBalanceInterest(bad, 50, 365));
        Assert.Equal("intervals", ex.ParamName);
    }

    // --- Negative rates and balances are permitted BY DESIGN (negative-rate environments).
    //     Only the time dimension is guarded; the rate sign is not. These pin that contract
    //     so a maintainer can tell "intended" from "forgotten" (review I1). ---

    [Fact]
    public void SimpleInterest_with_a_negative_rate_emits_negative_interest()
    {
        // A negative TAN (−600 bps) over the §5.1 interval mirrors the worked example with a
        // flipped sign: 1,000,000 × −600 × 365 / (360 × 10000) = −60,833.33… → −€608.33.
        var factor = DayCount.Between(new DateOnly(2023, 1, 1), new DateOnly(2024, 1, 1), DayCountConvention.Act360);
        Assert.Equal(-60_833L, Accrual.SimpleInterest(TenThousand, -600, factor).Cents);
    }

    [Fact]
    public void CompoundMaturity_with_a_negative_rate_erodes_principal()
    {
        // TAN −6% monthly: M = 10,000 × (0.995)^12 = €9,416.23, so the deposit shrinks and
        // CompoundInterest is negative (−€583.77) — emitted, not rejected.
        Assert.Equal(941_623L, Accrual.CompoundMaturity(TenThousand, -600, 12, 12).Cents);
        Assert.Equal(-58_377L, Accrual.CompoundInterest(TenThousand, -600, 12, 12).Cents);
    }

    [Fact]
    public void DailyBalanceInterest_nets_signed_overdraft_intervals()
    {
        // A credit interval and an overdraft (negative-balance) interval in one period —
        // realistic for revolving accrual (§8.1). Σ S(d) = 100000·15 + (−50000)·15 = 750,000;
        // J = (0.005/365) × 750,000 = 10.27… → €0.10. Negative balances net, no error.
        var intervals = new (Money, int)[]
        {
            (new Money(100_000L), 15),   // +€1,000 for 15 days
            (new Money(-50_000L), 15),   // −€500 overdraft for 15 days
        };
        Assert.Equal(10L, Accrual.DailyBalanceInterest(intervals, rateBps: 50, basis: 365).Cents);
    }

    // --- §P2 single-boundary HALF_EVEN: drive each linear-formula family to an exact .5-cent
    //     tie and assert banker's rounding. A regression that pre-rounded an intermediate
    //     (violating round-once) would round these ties differently (review S2). The compound
    //     family crosses the same Money.FromCents boundary, exercised by the corpus fixtures. ---

    [Theory]
    [InlineData(1L, 0L)]   // 0.5 → 0 (even)
    [InlineData(3L, 2L)]   // 1.5 → 2 (even)
    [InlineData(5L, 2L)]   // 2.5 → 2 (even)
    [InlineData(7L, 4L)]   // 3.5 → 4 (even)
    public void SimpleInterest_rounds_half_to_even_at_the_cent_boundary(long principalCents, long expectedCents)
    {
        // factor 1/2 (half a basis) at 100% (10000 bps): interest = principal_cents / 2, so
        // odd principals land exactly on a .5-cent tie.
        var halfBasis = new DayCountFactor(1, 2);
        Assert.Equal(expectedCents, Accrual.SimpleInterest(new Money(principalCents), 10_000, halfBasis).Cents);
    }

    [Theory]
    [InlineData(3L, 2L)]   // Σ = 3, J = 3/2 = 1.5 → 2 (even)
    [InlineData(5L, 2L)]   // 2.5 → 2 (even)
    public void DailyBalanceInterest_rounds_half_to_even_at_the_cent_boundary(long balanceCents, long expectedCents)
    {
        // basis 2 at 100%: J = Σ(balance×days) / 2 over a single 1-day interval.
        var intervals = new (Money, int)[] { (new Money(balanceCents), 1) };
        Assert.Equal(expectedCents, Accrual.DailyBalanceInterest(intervals, 10_000, basis: 2).Cents);
    }

    // --- Long-horizon cent-exactness: the PR claimed PowDecimal stays cent-exact to 360
    //     periods. This pins it as a committed fitness function — and self-validates by
    //     recomputing the growth the naive O(n) way the exponentiation-by-squaring replaces,
    //     so a refactor of either path that drifts a cent fails here (review S1). ---

    [Fact]
    public void CompoundMaturity_is_cent_exact_over_a_30_year_monthly_horizon()
    {
        const int rateBps = 600, m = 12, periods = 360; // TAN 6%, monthly, 30 years
        Money maturity = Accrual.CompoundMaturity(TenThousand, rateBps, m, periods);

        // Independent naive computation: (1 + r)^360 by repeated decimal multiplication.
        decimal periodicRate = rateBps / (m * 10_000m);
        decimal naiveGrowth = 1m;
        for (int i = 0; i < periods; i++)
            naiveGrowth *= 1m + periodicRate;
        Money naiveMaturity = Money.FromCents((decimal)TenThousand.Cents * naiveGrowth);

        Assert.Equal(naiveMaturity.Cents, maturity.Cents); // squaring == naive, to the cent
        Assert.Equal(6_022_575L, maturity.Cents);          // €60,225.75 on €10,000 over 30y
    }

    // --- Overflow at the boundary is loud, not silent (review S6). ---

    [Fact]
    public void SimpleInterest_throws_OverflowException_when_the_decimal_product_overflows()
    {
        // (decimal)long.MaxValue × int.MaxValue × int.MaxValue ≈ 4.3e37 exceeds decimal's
        // ~7.9e28 range; the decimal multiply itself throws before any rounding.
        var huge = new DayCountFactor(int.MaxValue, 1);
        Assert.Throws<OverflowException>(
            () => Accrual.SimpleInterest(new Money(long.MaxValue), int.MaxValue, huge));
    }
}
