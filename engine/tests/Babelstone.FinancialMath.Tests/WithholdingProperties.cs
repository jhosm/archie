using Babelstone.FinancialTypes;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Babelstone.FinancialMath.Tests;

/// <summary>
/// Property-based laws for <see cref="Withholding"/> (B.9; fin-math §5.4). The example-based
/// <see cref="WithholdingTests"/> pin that flow-by-flow withholding <i>does</i> diverge from the
/// rate-on-aggregate shortcut (3¢ on twelve realistic monthly flows); these assert the laws that
/// govern every case:
/// <list type="bullet">
///   <item><b>Conservation</b> — <c>Net + Tax == Gross</c> exactly, for any flow and any rate
///   (the residual-net design leaves no rounding gap, §5.4);</item>
///   <item><b>Containment</b> — for a non-negative flow, tax and net each stay within
///   <c>[0, Gross]</c>;</item>
///   <item><b>Bounded divergence</b> — the §5.4 heart: per-flow withholding and the aggregate
///   shortcut differ by at most a half-cent per flow, so they are neither interchangeable nor
///   free to run apart.</item>
/// </list>
/// </summary>
public class WithholdingProperties
{
    // A non-empty run of non-negative interest flows (cents). 1..60 flows ≤ €1M each keeps the
    // aggregate gross inside Int64 (60 × 1e8 = 6e9) so flows.Sum() cannot overflow.
    private static Gen<long[]> InterestFlows =>
        from n in Gen.Choose(1, 60)
        from flows in Gen.ArrayOf(from c in Gen.Choose(0, 100_000_000) select (long)c, n)
        select flows;

    [Property]
    public Property Net_plus_tax_equals_gross_for_any_flow_and_rate()
    {
        // Gross spans both signs: a negative-rate environment accrues negative interest, and
        // conservation must still hold leg-for-leg there (Accrual emits negative interest by design).
        var gen = from cents in Gen.Choose(-2_000_000_000, 2_000_000_000)
                  from rateBps in Gen.Choose(0, 10_000)
                  select ((long)cents, rateBps);
        return Prop.ForAll(gen.ToArbitrary(), t =>
        {
            var (cents, rateBps) = t;
            var r = Withholding.Withhold(new Money(cents), rateBps);
            return r.Gross.Cents == cents && (r.Net + r.Tax).Cents == cents;
        });
    }

    [Property]
    public Property Tax_and_net_stay_within_a_non_negative_gross()
    {
        var gen = from cents in Gen.Choose(0, 2_000_000_000)
                  from rateBps in Gen.Choose(0, 10_000)
                  select ((long)cents, rateBps);
        return Prop.ForAll(gen.ToArbitrary(), t =>
        {
            var (cents, rateBps) = t;
            var r = Withholding.Withhold(new Money(cents), rateBps);
            return r.Tax.Cents >= 0 && r.Tax.Cents <= cents
                && r.Net.Cents >= 0 && r.Net.Cents <= cents;
        });
    }

    [Property]
    public Property Flow_by_flow_withholding_diverges_from_the_aggregate_only_within_a_bounded_envelope()
    {
        var gen = from flows in InterestFlows
                  from rateBps in Gen.Choose(0, 10_000)
                  select (flows, rateBps);
        return Prop.ForAll(gen.ToArbitrary(), t =>
        {
            var (flows, rateBps) = t;

            Money perFlowTax = Money.Zero;
            foreach (long f in flows)
                perFlowTax += Withholding.Withhold(new Money(f), rateBps).Tax;

            Money aggregateTax = Withholding.Withhold(new Money(flows.Sum()), rateBps).Tax;

            // Σ round(fᵢ·r) − round(Σfᵢ·r): the n per-flow roundings and the one aggregate rounding
            // each carry at most a half-cent of error, so |drift| ≤ (n+1)/2 — exactly, in integers.
            long n = flows.Length;
            long drift = Math.Abs(perFlowTax.Cents - aggregateTax.Cents);
            return 2 * drift <= n + 1;
        });
    }
}
