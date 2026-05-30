using Xunit;

namespace Babelstone.FinancialTypes.Tests;

public class MoneyTests
{
    public static IEnumerable<object[]> BoundaryCases() =>
        MoneyBoundaryFixtures.Cases.Select(c => new object[] { c.Name, c.InputCents, c.ExpectedCents });

    [Theory]
    [MemberData(nameof(BoundaryCases))]
    public void FromCents_rounds_half_even_once(string name, decimal inputCents, long expectedCents)
    {
        long actual = Money.FromCents(inputCents).Cents;
        Assert.True(expectedCents == actual, $"{name}: expected {expectedCents} cents, got {actual}");
    }

    [Fact]
    public void Zero_is_zero_cents() => Assert.Equal(0L, Money.Zero.Cents);

    [Theory]
    [InlineData(100L, 50L, 150L)]
    [InlineData(100L, -50L, 50L)]
    public void Addition_sums_cents(long a, long b, long expected) =>
        Assert.Equal(expected, (new Money(a) + new Money(b)).Cents);

    [Fact]
    public void Subtraction_subtracts_cents() =>
        Assert.Equal(50L, (new Money(100L) - new Money(50L)).Cents);

    [Fact]
    public void Negation_flips_sign() =>
        Assert.Equal(-100L, (-new Money(100L)).Cents);

    [Fact]
    public void Addition_overflow_throws() =>
        Assert.Throws<OverflowException>(() => new Money(long.MaxValue) + new Money(1L));

    [Fact]
    public void Subtraction_overflow_throws() =>
        // Underflow past long.MinValue: the checked operator must throw, never wrap.
        Assert.Throws<OverflowException>(() => new Money(long.MinValue) - new Money(1L));

    [Fact]
    public void Negation_overflow_throws() =>
        // −long.MinValue has no Int64 representation; checked negation must throw, never wrap.
        Assert.Throws<OverflowException>(() => -new Money(long.MinValue));

    [Fact]
    public void ToDecimal_projects_cents_to_euros()
    {
        // The read-only euro projection divides by 100; pin it so the division is not silently
        // a multiplication (a wrong report figure rather than a thrown error — B.10 triage).
        Assert.Equal(12.34m, new Money(1234L).ToDecimal());
        Assert.Equal(-12.34m, new Money(-1234L).ToDecimal());
        Assert.Equal(0.05m, new Money(5L).ToDecimal());
        Assert.Equal(0m, Money.Zero.ToDecimal());
    }

    [Fact]
    public void FromCents_throws_contextualised_when_rounded_cents_exceed_int64()
    {
        // 1e19 > long.MaxValue (~9.22e18). The boundary names the operand and the value
        // rather than surfacing a bare framework OverflowException (review I2).
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Money.FromCents(10_000_000_000_000_000_000m));
        Assert.Equal("cents", ex.ParamName);
    }

    [Fact]
    public void Equality_is_by_value()
    {
        Assert.Equal(new Money(100L), new Money(100L));
        Assert.NotEqual(new Money(100L), new Money(101L));
    }

    [Theory]
    [InlineData(0L, "0.00")]
    [InlineData(5L, "0.05")]
    [InlineData(1234L, "12.34")]
    [InlineData(-1234L, "-12.34")]
    public void ToString_formats_euros_invariant(long cents, string expected) =>
        Assert.Equal(expected, new Money(cents).ToString());
}
