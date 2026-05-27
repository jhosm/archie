using Xunit;

namespace Babelstone.Money.Analyzers.Tests;

public class MathRoundOnDecimalAnalyzerTests
{
    private const string MoneyShell = """
        namespace Babelstone.FinancialTypes
        {
            public readonly struct Money
            {
                public long Cents { get; }
                private Money(long cents) { Cents = cents; }
                public static Money FromCents(decimal cents) =>
                    new Money((long)System.Math.Round(cents, 0, System.MidpointRounding.ToEven));
            }
        }
        """;

    [Fact]
    public async Task FromCents_is_the_allowed_rounding_site()
    {
        var ids = await AnalyzerHarness.DiagnosticIdsAsync(MoneyShell, new MathRoundOnDecimalAnalyzer());
        Assert.Empty(ids);
    }

    [Fact]
    public async Task Math_Round_on_decimal_elsewhere_is_flagged()
    {
        const string source = """
            namespace Accrual
            {
                public static class Calc
                {
                    public static decimal Bad(decimal d) => System.Math.Round(d, 2);
                }
            }
            """;

        var ids = await AnalyzerHarness.DiagnosticIdsAsync(source, new MathRoundOnDecimalAnalyzer());
        Assert.Equal(new[] { MoneyDiagnostics.MathRoundId }, ids);
    }

    [Fact]
    public async Task Math_Round_on_double_is_not_money_math_and_is_ignored()
    {
        const string source = """
            namespace Geometry
            {
                public static class Calc
                {
                    public static double Area(double r) => System.Math.Round(3.14159 * r * r, 3);
                }
            }
            """;

        var ids = await AnalyzerHarness.DiagnosticIdsAsync(source, new MathRoundOnDecimalAnalyzer());
        Assert.Empty(ids);
    }

    [Fact]
    public async Task A_FromCents_on_a_different_type_is_still_flagged()
    {
        // The exemption is anchored to Babelstone.FinancialTypes.Money.FromCents specifically —
        // a look-alike FromCents elsewhere does not earn the boundary exemption.
        const string source = """
            namespace Other
            {
                public static class NotMoney
                {
                    public static long FromCents(decimal cents) =>
                        (long)System.Math.Round(cents, 0, System.MidpointRounding.ToEven);
                }
            }
            """;

        var ids = await AnalyzerHarness.DiagnosticIdsAsync(source, new MathRoundOnDecimalAnalyzer());
        Assert.Equal(new[] { MoneyDiagnostics.MathRoundId }, ids);
    }
}
