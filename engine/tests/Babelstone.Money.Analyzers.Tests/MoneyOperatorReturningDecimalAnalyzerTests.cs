using Xunit;

namespace Babelstone.Money.Analyzers.Tests;

public class MoneyOperatorReturningDecimalAnalyzerTests
{
    private const string MoneyHeader = "namespace Babelstone.Money { public readonly partial struct Money { public long Cents { get; init; } ";

    [Fact]
    public async Task Binary_operator_returning_decimal_from_money_is_flagged()
    {
        const string source = MoneyHeader + """
                public static decimal operator *(Money a, decimal factor) => a.Cents * factor;
            } }
            """;

        var ids = await AnalyzerHarness.DiagnosticIdsAsync(source, new MoneyOperatorReturningDecimalAnalyzer());
        Assert.Equal(new[] { MoneyDiagnostics.MoneyOperatorId }, ids);
    }

    [Fact]
    public async Task Conversion_operator_to_decimal_from_money_is_flagged()
    {
        const string source = MoneyHeader + """
                public static explicit operator decimal(Money m) => m.Cents;
            } }
            """;

        var ids = await AnalyzerHarness.DiagnosticIdsAsync(source, new MoneyOperatorReturningDecimalAnalyzer());
        Assert.Equal(new[] { MoneyDiagnostics.MoneyOperatorId }, ids);
    }

    [Fact]
    public async Task Operator_returning_money_is_allowed()
    {
        const string source = MoneyHeader + """
                public static Money operator +(Money a, Money b) => new Money { Cents = a.Cents + b.Cents };
            } }
            """;

        var ids = await AnalyzerHarness.DiagnosticIdsAsync(source, new MoneyOperatorReturningDecimalAnalyzer());
        Assert.Empty(ids);
    }

    [Fact]
    public async Task A_plain_method_returning_decimal_is_not_an_operator_and_is_ignored()
    {
        // ToDecimal() is the sanctioned, explicit read-only projection — a method, not an operator.
        const string source = MoneyHeader + """
                public decimal ToDecimal() => Cents / 100m;
            } }
            """;

        var ids = await AnalyzerHarness.DiagnosticIdsAsync(source, new MoneyOperatorReturningDecimalAnalyzer());
        Assert.Empty(ids);
    }
}
