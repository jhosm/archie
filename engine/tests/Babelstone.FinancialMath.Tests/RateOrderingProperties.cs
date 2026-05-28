using Babelstone.FinancialTypes;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Babelstone.FinancialMath.Tests;

/// <summary>
/// Property-based laws for the effective-rate metrics (B.9; fin-math §6). The doc states the
/// ordering directly — "in normal conditions (positive rates, non-negative charges, m ≥ 1):
/// <c>TAEG ≥ TAE ≥ TAN</c>". These pin both links across the input space:
/// <list type="bullet">
///   <item><b>TAE ≥ TAN</b>, exactly and purely: <c>(1 + TAN/m)^m − 1 ≥ TAN</c> for any
///   non-negative rate and m ≥ 1 (equality only at m = 1), and TAE is non-decreasing in the
///   compounding frequency m;</item>
///   <item><b>TAEG ≥ TAE ≥ TAN</b> on a constructed loan: the charge-free TAEG recovers the
///   loan's TAE, and adding a mandatory charge (a fee netted at disbursement) never lowers it —
///   the §6.2 fee effect, the reason TAEG sits above TAE.</item>
/// </list>
/// </summary>
public class RateOrderingProperties
{
    [Property]
    public Property Tae_is_never_below_Tan()
    {
        var gen = from tanBps in Gen.Choose(0, 5_000) // 0%–50%
                  from m in Gen.Choose(1, 365)        // up to daily compounding
                  select (tanBps, m);
        return Prop.ForAll(gen.ToArbitrary(), t =>
        {
            var (tanBps, m) = t;
            return Rates.Tae(tanBps, m) >= tanBps / 10_000m;
        });
    }

    [Property]
    public Property Tae_does_not_shrink_as_compounding_frequency_rises()
    {
        var gen = from tanBps in Gen.Choose(0, 5_000)
                  from mLo in Gen.Choose(1, 200)
                  from step in Gen.Choose(0, 200)
                  select (tanBps, mLo, mLo + step);
        return Prop.ForAll(gen.ToArbitrary(), t =>
        {
            var (tanBps, mLo, mHi) = t;
            return Rates.Tae(tanBps, mLo) <= Rates.Tae(tanBps, mHi);
        });
    }

    [Property(MaxTest = 200)]
    public Property Taeg_is_at_least_Tae_is_at_least_Tan_on_a_constructed_loan()
    {
        // A 12-month interest-only loan at monthly rate i = TAN/12, borrower's perspective. The
        // repayment legs depend only on the contracted principal, so they are identical with or
        // without a fee — only the disbursement shrinks when a fee is netted, which is exactly what
        // pushes the effective cost up. One sign change → conventional → a single IRR ≈ i, whose
        // annualisation is the loan's TAE.
        //
        // Inputs sit in the solver's sweet spot: 1%–24% TAN, €1k–€100k, fee ≤ a fifth of principal —
        // keeping i ∈ ~[0.0008, 0.02]/period (near 0, where Newton converges) and the monthly
        // interest strictly positive (≥ 83¢), so the coupon vector is never degenerate.
        var gen = from tanBps in Gen.Choose(100, 2_400)
                  from principalEuros in Gen.Choose(1_000, 100_000)
                  from feeFraction in Gen.Choose(0, 20) // 0%–20% of principal, netted at disbursement
                  select (tanBps, principalEuros, feeFraction);

        return Prop.ForAll(gen.ToArbitrary(), t =>
        {
            var (tanBps, principalEuros, feeFraction) = t;
            const int m = 12;

            // The solver compares two numerically-found rates, so the TAEG links carry a tolerance:
            // convergence is |PV| < 1e-6 cents and the monthly interest is rounded to the cent, which
            // can nudge a recovered rate a hair below its analytic target. 1e-3 (0.1 pp) absorbs that
            // while staying far tighter than any real ordering violation (a sign/formula bug misses
            // by ≫ 1 pp). A method-local decimal is a boundary computation, not stored state (§P1).
            const decimal tol = 0.001m;

            long principal = principalEuros * 100L;
            Money interest = Money.FromCents((decimal)principal * tanBps / (m * 10_000m));
            long fee = principal * feeFraction / 100;

            decimal tan = tanBps / 10_000m;
            decimal tae = Rates.Tae(tanBps, m);
            decimal taegNoFee = Rates.Taeg(InterestOnlyLoan(principal, principal, interest, m), m);
            decimal taegWithFee = Rates.Taeg(InterestOnlyLoan(principal - fee, principal, interest, m), m);

            return tae >= tan                       // TAE ≥ TAN  (pure, exact)
                && taegNoFee >= tae - tol           // charge-free TAEG recovers the loan's TAE
                && taegWithFee >= taegNoFee - tol   // a mandatory charge never lowers the TAEG …
                && (feeFraction == 0 || taegWithFee > taegNoFee); // … and strictly raises it (§6.2)
        });
    }

    /// <summary>
    /// Interest-only loan, borrower's perspective: <paramref name="disbursement"/> received at t=0,
    /// a fixed <paramref name="interest"/> coupon each period, and the contracted
    /// <paramref name="principal"/> returned with the final coupon. The disbursement may be netted
    /// (principal − fee); the repayment legs always reflect the full contracted principal.
    /// </summary>
    private static IReadOnlyList<(Money Amount, int Period)> InterestOnlyLoan(
        long disbursement, long principal, Money interest, int periods)
    {
        var flows = new List<(Money, int)>(periods + 1) { (new Money(disbursement), 0) };
        for (int t = 1; t < periods; t++)
            flows.Add((-interest, t));
        flows.Add((-(new Money(principal) + interest), periods));
        return flows;
    }
}
