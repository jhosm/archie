using Babelstone.FinancialMath;
using Xunit;

namespace Babelstone.FinancialMath.Tests;

/// <summary>
/// Pins <see cref="Rate"/> — the shared basis-point scale primitive — directly in the kernel's own
/// leg. <see cref="Rate.ScaledByBasisPoints"/> is the UN-ROUNDED numerator <c>cents × rateBps / 10000</c>
/// every accrual/amortization/effective-rate path and the loan decider fold through, so a slip here
/// is a cent slip everywhere. The B.10 mutation leg runs the kernel in test-project context (its own
/// Docker-free suite only), so a helper exercised only by SIBLING-family tests shows up as an
/// unpinned mutant — these tests close that gap by pinning the scale in the kernel itself.
/// </summary>
public class RateTests
{
    [Fact]
    public void BasisPointsPerUnit_is_the_100pct_equals_10000bps_scale()
        => Assert.Equal(10_000, Rate.BasisPointsPerUnit);

    [Theory]
    // value = cents × rateBps / 10000, exact: separates the * and / so neither arithmetic mutant
    // (× → ÷ or ÷ → ×) reproduces the expected number.
    [InlineData(1_000_000L, 600, 60_000)]   // €10,000 at 6%  → 60,000 (un-rounded cents-of-interest numerator)
    [InlineData(1_000_000L, 2800, 280_000)] // the 28% IRS withholding scale
    [InlineData(0L, 600, 0)]                // no capital → no interest, for any rate
    public void ScaledByBasisPoints_is_cents_times_bps_over_10000(long cents, int rateBps, long expected)
        => Assert.Equal(expected, Rate.ScaledByBasisPoints(cents, rateBps));

    [Fact]
    public void ScaledByBasisPoints_stays_un_rounded_below_the_cent()
        // 1005 cents at 50 bps = 1005 × 50 / 10000 = 5.025 — a sub-cent fraction the helper must NOT
        // round (it is the numerator a caller folds into a larger single-rounding expression, §P2). A
        // mutant that rounded here, or swapped the arithmetic, loses the exact 5.025.
        => Assert.Equal(5.025m, Rate.ScaledByBasisPoints(1_005L, 50));

    [Fact]
    public void ScaledByBasisPoints_carries_a_negative_rate_through()
        // Negative-rate environments: a negative bps yields a negative numerator, emitted not rejected
        // (consistent with Accrual/Tae). −600 bps on €10,000 → −60,000.
        => Assert.Equal(-60_000m, Rate.ScaledByBasisPoints(1_000_000L, -600));
}
