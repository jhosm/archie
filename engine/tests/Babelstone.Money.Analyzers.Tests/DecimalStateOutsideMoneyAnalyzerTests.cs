using Xunit;

namespace Babelstone.Money.Analyzers.Tests;

public class DecimalStateOutsideMoneyAnalyzerTests
{
    [Fact]
    public async Task Decimal_field_outside_money_is_flagged()
    {
        const string source = """
            namespace Ledger
            {
                public class Balance
                {
                    private decimal _amount;
                }
            }
            """;

        var ids = await AnalyzerHarness.DiagnosticIdsAsync(source, new DecimalStateOutsideMoneyAnalyzer());
        Assert.Equal(new[] { MoneyDiagnostics.DecimalStateId }, ids);
    }

    [Fact]
    public async Task Decimal_property_outside_money_is_flagged()
    {
        const string source = """
            namespace Ledger
            {
                public class Balance
                {
                    public decimal Amount { get; set; }
                }
            }
            """;

        var ids = await AnalyzerHarness.DiagnosticIdsAsync(source, new DecimalStateOutsideMoneyAnalyzer());
        Assert.Equal(new[] { MoneyDiagnostics.DecimalStateId }, ids);
    }

    [Fact]
    public async Task Nullable_decimal_property_outside_money_is_flagged()
    {
        const string source = """
            namespace Ledger
            {
                public class Balance
                {
                    public decimal? Amount { get; set; }
                }
            }
            """;

        var ids = await AnalyzerHarness.DiagnosticIdsAsync(source, new DecimalStateOutsideMoneyAnalyzer());
        Assert.Equal(new[] { MoneyDiagnostics.DecimalStateId }, ids);
    }

    [Fact]
    public async Task Decimal_local_and_parameter_are_boundary_compute_and_ignored()
    {
        const string source = """
            namespace Accrual
            {
                public static class Calc
                {
                    public static long ToCents(decimal rate)
                    {
                        decimal scaled = rate * 100m;
                        return (long)scaled;
                    }
                }
            }
            """;

        var ids = await AnalyzerHarness.DiagnosticIdsAsync(source, new DecimalStateOutsideMoneyAnalyzer());
        Assert.Empty(ids);
    }

    [Fact]
    public async Task Decimal_state_inside_financialtypes_namespace_is_allowed()
    {
        const string source = """
            namespace Babelstone.FinancialTypes
            {
                public class RateInput
                {
                    public decimal Percent { get; set; }
                }
            }
            """;

        var ids = await AnalyzerHarness.DiagnosticIdsAsync(source, new DecimalStateOutsideMoneyAnalyzer());
        Assert.Empty(ids);
    }

    [Fact]
    public async Task Decimal_state_in_financialtypes_subnamespace_is_allowed()
    {
        // The fixture corpus lives in Babelstone.FinancialTypes.Tests and holds decimal inputs;
        // the exemption is the whole Babelstone.FinancialTypes.* subtree, not just the root.
        const string source = """
            namespace Babelstone.FinancialTypes.Tests
            {
                public readonly record struct Case(string Name, decimal InputCents, long ExpectedCents);
            }
            """;

        var ids = await AnalyzerHarness.DiagnosticIdsAsync(source, new DecimalStateOutsideMoneyAnalyzer());
        Assert.Empty(ids);
    }

    [Fact]
    public async Task A_namespace_that_merely_starts_with_the_exempt_text_is_not_exempt()
    {
        // Babelstone.FinancialTypesExtra shares the prefix but is not the subtree (no dot
        // boundary), so the exemption must not apply.
        const string source = """
            namespace Babelstone.FinancialTypesExtra
            {
                public class Suspicious
                {
                    public decimal Amount { get; set; }
                }
            }
            """;

        var ids = await AnalyzerHarness.DiagnosticIdsAsync(source, new DecimalStateOutsideMoneyAnalyzer());
        Assert.Equal(new[] { MoneyDiagnostics.DecimalStateId }, ids);
    }
}
