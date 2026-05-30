using Babelstone.FinancialMath;
using Xunit;

namespace Babelstone.FinancialMath.Tests;

/// <summary>
/// Direct tests for the internal base-10 power primitive. The kernel exercises
/// <see cref="DecimalMath.Pow"/> indirectly through accrual and rate math, but its guards and
/// empty-product convention are contracts in their own right; pinning them here (rather than
/// only through callers) keeps the mutation suite honest about the primitive itself (B.10).
/// </summary>
public class DecimalMathTests
{
    [Theory]
    [InlineData(2, 10, 1024)]
    [InlineData(3, 0, 1)]      // x^0 = 1, the empty-product convention the compounding forms rely on
    [InlineData(0, 0, 1)]      // 0^0 = 1 too (documented)
    [InlineData(1, 100, 1)]
    public void Pow_raises_to_a_whole_exponent(int value, int exponent, int expected) =>
        Assert.Equal(expected, DecimalMath.Pow(value, exponent));

    [Fact]
    public void Pow_of_a_sub_unity_base_erodes() =>
        // (1 + a negative rate)^n < 1: the kernel raises eroding bases, so this path matters.
        Assert.Equal(0.81m, DecimalMath.Pow(0.9m, 2));

    [Fact]
    public void Pow_rejects_a_negative_exponent() =>
        // The primitive offers no negative power (callers divide by a positive one instead);
        // the guard is the contract, so it must throw rather than silently fall through.
        Assert.Throws<ArgumentOutOfRangeException>(() => DecimalMath.Pow(2m, -1));
}
