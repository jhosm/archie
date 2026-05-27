using Babelstone.FinancialMath;
using Xunit;

namespace Babelstone.FinancialMath.Tests;

// Inside the namespace so it outranks the like-named Babelstone.Money namespace.
using Money = global::Babelstone.Money.Money;

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
    public void DailyBalanceInterest_rejects_a_negative_interval()
    {
        var bad = new (Money, int)[] { (new Money(100_000L), -1) };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Accrual.DailyBalanceInterest(bad, 50, 365));
        Assert.Equal("intervals", ex.ParamName);
    }
}
