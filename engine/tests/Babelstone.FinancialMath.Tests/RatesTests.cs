using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.FinancialMath.Tests;

public class RatesTests
{
    // A Price-system credit (fin-math §4.1/§6): net disbursement at t=0, then n equal
    // installments at t=1..n. The borrower-side sign convention of §6.2 — money received is
    // positive, money paid is negative — so the vector has exactly one sign change (one IRR).
    private static List<(Money Amount, int Period)> PriceCredit(long netDisbursementCents, long installmentCents, int n)
    {
        var flows = new List<(Money Amount, int Period)> { (new Money(netDisbursementCents), 0) };
        for (int t = 1; t <= n; t++)
            flows.Add((new Money(-installmentCents), t));
        return flows;
    }

    // --- TAE — effective annual rate (fin-math §5.4): (1 + TAN/m)^m − 1. ---

    [Fact]
    public void Tae_reproduces_the_fin_math_5_4_worked_example()
    {
        // TAN 6% (600 bps), monthly capitalization: (1 + 0.06/12)^12 − 1 ≈ 0.061678 (6.17%).
        // The 17-bps gap over the 6% TAN is the compounding effect the doc highlights.
        decimal tae = Rates.Tae(tanBps: 600, periodsPerYear: 12);

        // Assert via xUnit's precision overload (rounds inside the assertion) rather than
        // Math.Round on the decimal — BMNY001 reserves decimal rounding for Money.FromCents.
        Assert.Equal(0.0617m, tae, 4); // 6.17% to the doc's stated precision
    }

    [Fact]
    public void Tae_equals_tan_when_interest_is_not_compounded_intra_year()
    {
        // m = 1 (interest at maturity, no intra-period capitalization): TAE = TAN exactly,
        // (1 + 0.06)^1 − 1 = 0.06. The §5.4 degenerate case where the formula "doesn't matter".
        Assert.Equal(0.06m, Rates.Tae(tanBps: 600, periodsPerYear: 1));
    }

    [Fact]
    public void Tae_grows_with_compounding_frequency()
    {
        // The §5.4 claim "the gap grows with m": at a fixed TAN, more frequent compounding
        // yields a strictly higher effective rate. annual < monthly < daily.
        decimal annual = Rates.Tae(600, 1);
        decimal monthly = Rates.Tae(600, 12);
        decimal daily = Rates.Tae(600, 365);

        Assert.True(annual < monthly);
        Assert.True(monthly < daily);
    }

    [Fact]
    public void Tae_with_a_negative_rate_is_negative()
    {
        // Consistent with Accrual: a negative TAN annualizes to a negative effective rate,
        // emitted rather than rejected (negative-rate environments).
        Assert.True(Rates.Tae(tanBps: -600, periodsPerYear: 12) < 0m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-12)]
    public void Tae_rejects_a_non_positive_compounding_frequency(int periodsPerYear)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Rates.Tae(600, periodsPerYear));
        Assert.Equal("periodsPerYear", ex.ParamName);
    }

    // --- Annualize — the (1 + r)^m − 1 identity TAE and TAEG both fold through. ---

    [Fact]
    public void Annualize_of_a_monthly_periodic_rate_matches_the_irr_6_1_example()
    {
        // fin-math §6.1: a per-month IRR of 0.005 corresponds to TAE = (1.005)^12 − 1 ≈ 6.17%.
        // This is the path the TAEG takes — annualize a solved per-period rate.
        decimal annual = Rates.Annualize(periodicRate: 0.005m, periodsPerYear: 12);

        Assert.Equal(0.0617m, annual, 4);
        Assert.Equal(Rates.Tae(600, 12), annual); // same number, two doors in (§5.4 ≡ §6.1)
    }

    [Fact]
    public void Annualize_of_a_zero_rate_is_zero()
    {
        // (1 + 0)^m − 1 = 0 for any m: no growth annualizes to no effective rate.
        Assert.Equal(0m, Rates.Annualize(0m, 12));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Annualize_rejects_a_non_positive_compounding_frequency(int periodsPerYear)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Rates.Annualize(0.005m, periodsPerYear));
        Assert.Equal("periodsPerYear", ex.ParamName);
    }

    // --- IRR — 0 = Σ CF(t)/(1+i)^t (fin-math §6.1). ---

    [Fact]
    public void Irr_of_a_single_period_deposit_is_the_exact_growth_rate()
    {
        // CF(0) = −€10,000, CF(1) = +€10,608.33 (the §5.1/§5.2 maturity). The root is rational:
        // 1,060,833 / 1,000,000 = 1.060833, so IRR = 0.060833 exactly — PV is 0 there.
        var deposit = new List<(Money Amount, int Period)> { (new Money(-1_000_000L), 0), (new Money(1_060_833L), 1) };
        Assert.Equal(0.060833m, Rates.InternalRateOfReturn(deposit), 6);
    }

    [Fact]
    public void Irr_reproduces_the_fin_math_6_1_price_credit_example()
    {
        // €10,000 received, 12 × €860.66 installments → per-month IRR = 0.5% (TAN 6%), and the
        // per-period IRR coincides with the nominal periodic rate when there are no charges.
        decimal irr = Rates.InternalRateOfReturn(PriceCredit(1_000_000L, 86_066L, 12));

        Assert.Equal(0.0050m, irr, 4);
        Assert.Equal(0.0617m, Rates.Annualize(irr, 12), 4); // TAE ≈ 6.17%, the §6.1 ✓
    }

    [Fact]
    public void Irr_falls_back_to_bisection_when_newton_leaves_the_domain()
    {
        // A deliberately bad guess (1000%/period) makes the first Newton step overshoot below
        // −1 (out of domain); the solver hands off to bisection and still finds the same root.
        var deposit = new List<(Money Amount, int Period)> { (new Money(-1_000_000L), 0), (new Money(1_060_833L), 1) };
        decimal viaBisection = Rates.InternalRateOfReturn(deposit, guess: 10m);

        Assert.Equal(0.060833m, viaBisection, 6);
        // Newton and bisection each converge to within ε of the root, not to identical bits, so
        // they agree to tolerance — not exactly. Both land on 0.060833 to the cent's worth of rate.
        Assert.Equal(Rates.InternalRateOfReturn(deposit), viaBisection, 6);
    }

    [Fact]
    public void Irr_converges_cleanly_over_a_30_year_monthly_horizon()
    {
        // The range-edge case: a 360-period (30-year monthly) mortgage must converge without
        // overflow or silent drift — exercising the documented decimal-range envelope, where
        // (1+i)^t over a long horizon is the binding constraint. €200,000 at 0.25%/month
        // (TAN 3%), installment €843.25 → IRR ≈ 0.25%/month, annualizing to TAEG ≈ 3.04%.
        var mortgage = PriceCredit(20_000_000L, 84_325L, 360);
        decimal irr = Rates.InternalRateOfReturn(mortgage);

        Assert.Equal(0.0025m, irr, 4);
        Assert.Equal(0.0304m, Rates.Annualize(irr, 12), 4);
    }

    // --- TAEG — annualised IRR of the full charge-bearing vector (fin-math §6.2). ---

    [Fact]
    public void Taeg_reproduces_the_fin_math_6_2_origination_fee_example()
    {
        // €200 origination fee netted at disbursement: borrower receives €9,800, installments
        // unchanged at €860.66. The exact vector's IRR is ≈0.008167/month → TAEG ≈ 10.25% (§6.2,
        // corrected in this same change). Annualize the *unrounded* i*: pre-rounding it to 0.00818
        // and re-compounding (1.00818)^12 − 1 inflates the figure to ≈10.27% — round once, at the
        // end. The fin-math doc originally carried the pre-rounded 10.27%; the solver caught it.
        var withFee = PriceCredit(980_000L, 86_066L, 12);

        Assert.Equal(0.1025m, Rates.Taeg(withFee, periodsPerYear: 12), 4);
    }

    [Fact]
    public void Taeg_is_the_annualised_irr_of_the_same_vector()
    {
        // TAEG is defined as the annualised IRR (§6.2); the two entry points must agree.
        var credit = PriceCredit(980_000L, 86_066L, 12);
        Assert.Equal(Rates.Annualize(Rates.InternalRateOfReturn(credit), 12), Rates.Taeg(credit, 12));
    }

    [Fact]
    public void A_mandatory_fee_raises_the_taeg_above_the_no_fee_case()
    {
        // The §6.2 headline: a small upfront fee on a short credit dwarfs the nominal rate —
        // the €200 fee adds ~4 pp (6.17% → 10.25%). Charges only ever push the TAEG up.
        decimal noFee = Rates.Taeg(PriceCredit(1_000_000L, 86_066L, 12), 12);
        decimal withFee = Rates.Taeg(PriceCredit(980_000L, 86_066L, 12), 12);
        Assert.True(withFee > noFee);
    }

    // --- Guards: the vector must admit a single IRR; solver knobs must be sane. ---

    [Fact]
    public void Irr_rejects_fewer_than_two_cash_flows()
    {
        var single = new List<(Money Amount, int Period)> { (new Money(-1_000_000L), 0) };
        Assert.Throws<ArgumentException>(() => Rates.InternalRateOfReturn(single));
    }

    [Fact]
    public void Irr_rejects_a_single_signed_vector_that_has_no_root()
    {
        // All outflows: PV is one-signed for every i, so there is no IRR to find.
        var allOutflows = new List<(Money Amount, int Period)> { (new Money(-1_000_000L), 0), (new Money(-86_066L), 1) };
        Assert.Throws<ArgumentException>(() => Rates.InternalRateOfReturn(allOutflows));
    }

    [Fact]
    public void Irr_rejects_a_negative_period_index()
    {
        var bad = new List<(Money Amount, int Period)> { (new Money(-1_000_000L), 0), (new Money(1_060_833L), -1) };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Rates.InternalRateOfReturn(bad));
        Assert.Equal("cashFlows", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Irr_rejects_a_non_positive_epsilon(decimal epsilon)
    {
        var deposit = new List<(Money Amount, int Period)> { (new Money(-1_000_000L), 0), (new Money(1_060_833L), 1) };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Rates.InternalRateOfReturn(deposit, epsilon: epsilon));
        Assert.Equal("epsilon", ex.ParamName);
    }

    [Fact]
    public void Irr_rejects_a_guess_at_or_below_minus_one()
    {
        var deposit = new List<(Money Amount, int Period)> { (new Money(-1_000_000L), 0), (new Money(1_060_833L), 1) };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Rates.InternalRateOfReturn(deposit, guess: -1m));
        Assert.Equal("guess", ex.ParamName);
    }

    // --- Solver numeric core, pinned directly (B.10 mutation triage). ---
    //
    // The IRR contract is robust by construction: Newton-Raphson with a bisection fallback.
    // That redundancy makes a wrong PV or derivative *unobservable* through the public API —
    // a corrupted Newton step just hands off to bisection, and the root of −PV equals the
    // root of PV, so a sign-flipped PV still finds the same rate. The black-box IRR/TAEG
    // tests above therefore cannot tell a correct PV core from several broken ones. These two
    // pin the PV and its derivative as *values* at a known rate, so the core the regulated
    // TAEG (§6.2) rests on is proven, not merely backstopped.
    // A 3-flow vector spanning t = 0, 1, 2. The t = 2 term is load-bearing: at t = 1 the
    // derivative's `cents * t` is numerically identical to `cents / t`, so a two-flow vector
    // cannot tell them apart — only t ≥ 2 separates the period weighting from its mutants.
    private static readonly (Money Amount, int Period)[] PinningVector =
        { (new Money(-1_000_000L), 0), (new Money(500_000L), 1), (new Money(700_000L), 2) };

    [Fact]
    public void PresentValue_is_pinned_at_a_known_rate()
    {
        // i = 1 (100%/period), chosen so (1+i)^t ≠ 1 — separating "/pow" from "*pow":
        // −1,000,000/1 + 500,000/2 + 700,000/4 = −575,000 cents.
        Assert.Equal(-575_000m, Rates.PresentValue(PinningVector, 1m));
    }

    [Fact]
    public void PresentValueAndDerivative_pins_both_legs_at_a_known_rate()
    {
        var (f, df) = Rates.PresentValueAndDerivative(PinningVector, 1m);
        Assert.Equal(-575_000m, f);   // Σ CF·(1+i)^−t — same value as PresentValue
        // −Σ CF·t·(1+i)^−(t+1) = −[500,000·1/2² + 700,000·2/2³] = −(125,000 + 175,000) = −300,000.
        Assert.Equal(-300_000m, df);
    }
}
