using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.FinancialMath.Tests;

public class WithholdingTests
{
    private const int PtIrsBps = 2800; // PT IRS withholding = 28% (fin-math §5.4)

    [Fact]
    public void Withhold_applies_the_pt_28_percent_rate_to_an_interest_flow()
    {
        // Gross €608.33 (the §5.1 accrual): tax = 60,833 × 0.28 = 17,033.24 → €170.33;
        // net = 60,833 − 17,033 = €438.00.
        var result = Withholding.Withhold(new Money(60_833L), PtIrsBps);
        Assert.Equal(60_833L, result.Gross.Cents);
        Assert.Equal(17_033L, result.Tax.Cents);
        Assert.Equal(43_800L, result.Net.Cents);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(60_833L)]
    [InlineData(999_999L)]
    [InlineData(123_457L)]
    public void Withhold_conserves_cents_net_plus_tax_equals_gross(long grossCents)
    {
        var result = Withholding.Withhold(new Money(grossCents), PtIrsBps);
        Assert.Equal(grossCents, (result.Net + result.Tax).Cents);
    }

    [Fact]
    public void Withhold_at_zero_rate_takes_nothing()
    {
        var result = Withholding.Withhold(new Money(60_833L), 0);
        Assert.Equal(Money.Zero, result.Tax);
        Assert.Equal(60_833L, result.Net.Cents);
    }

    [Fact]
    public void Withhold_at_full_rate_takes_everything()
    {
        var result = Withholding.Withhold(new Money(60_833L), 10_000);
        Assert.Equal(60_833L, result.Tax.Cents);
        Assert.Equal(Money.Zero, result.Net);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void Withhold_rejects_an_out_of_range_rate(int rateBps)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Withholding.Withhold(new Money(60_833L), rateBps));
        Assert.Equal("withholdingRateBps", ex.ParamName);
    }

    [Fact]
    public void Withhold_flow_by_flow_differs_from_withholding_an_aggregated_flow()
    {
        // The §5.4 rule made concrete: withholding each tiny flow rounds it to zero
        // (1 × 0.28 = 0.28 → 0), so per-flow tax is 0 and the depositor keeps all 3 cents —
        // whereas withholding one aggregated 3-cent flow takes 1 cent (3 × 0.28 = 0.84 → 1).
        // Computing flow-by-flow is therefore NOT the same as scaling/aggregating; the
        // primitive's Money-in signature is what forbids the rate-scaling shortcut.
        Money perFlowTax = Money.Zero;
        Money perFlowNet = Money.Zero;
        foreach (var _ in Enumerable.Range(0, 3))
        {
            var r = Withholding.Withhold(new Money(1L), PtIrsBps);
            perFlowTax += r.Tax;
            perFlowNet += r.Net;
        }

        var aggregated = Withholding.Withhold(new Money(3L), PtIrsBps);

        Assert.Equal(0L, perFlowTax.Cents);   // three sub-cent withholdings vanish
        Assert.Equal(3L, perFlowNet.Cents);
        Assert.Equal(1L, aggregated.Tax.Cents); // the aggregate keeps the sub-cent mass
        Assert.NotEqual(perFlowTax.Cents, aggregated.Tax.Cents);
    }

    [Fact]
    public void Withhold_flow_by_flow_drifts_from_aggregate_on_realistic_monthly_flows()
    {
        // The §5.4 economic point beyond the sub-cent degenerate case: twelve monthly interest
        // flows of €8.33 each. Per flow: 833 × 0.28 = 233.24 → 233¢ tax, so 12 × 233 = 2,796¢.
        // Aggregated: 9,996 × 0.28 = 2,798.88 → 2,799¢. Flow-by-flow withholds 3¢ less — the
        // depositor's realized net is higher than the rate-on-aggregate shortcut implies.
        Money perFlowTax = Money.Zero;
        Money perFlowNet = Money.Zero;
        foreach (var _ in Enumerable.Range(0, 12))
        {
            var r = Withholding.Withhold(new Money(833L), PtIrsBps);
            perFlowTax += r.Tax;
            perFlowNet += r.Net;
        }

        var aggregated = Withholding.Withhold(new Money(12 * 833L), PtIrsBps);

        Assert.Equal(2_796L, perFlowTax.Cents);
        Assert.Equal(7_200L, perFlowNet.Cents);
        Assert.Equal(2_799L, aggregated.Tax.Cents);
        Assert.Equal(3L, aggregated.Tax.Cents - perFlowTax.Cents); // 3¢ of accumulated drift
    }

    [Theory]
    [InlineData(1L, 0L, 1L)]   // tax 0.5 → 0 (even); net 1
    [InlineData(3L, 2L, 1L)]   // tax 1.5 → 2 (even); net 1
    [InlineData(5L, 2L, 3L)]   // tax 2.5 → 2 (even); net 3
    [InlineData(7L, 4L, 3L)]   // tax 3.5 → 4 (even); net 3
    public void Withhold_rounds_tax_half_to_even_at_the_cent_boundary(long grossCents, long expectedTax, long expectedNet)
    {
        // 50% rate puts tax = gross/2, so odd gross lands exactly on a .5-cent tie. Net stays
        // the residual gross − tax, so Net + Tax == Gross holds even across the tie.
        var result = Withholding.Withhold(new Money(grossCents), 5_000);
        Assert.Equal(expectedTax, result.Tax.Cents);
        Assert.Equal(expectedNet, result.Net.Cents);
        Assert.Equal(grossCents, (result.Net + result.Tax).Cents);
    }
}
