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
