using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;

namespace Babelstone.FinancialMath.Tests;

/// <summary>Which tier a <see cref="GoldenCase"/> belongs to. The two tiers are computed
/// from <i>independent</i> sources so they don't share a blind spot (B.6 + B.8).</summary>
public enum CorpusTier
{
    /// <summary>Expected value transcribed from the fin-math spec's own worked examples
    /// (B.6). Catches regressions; shares the spec's conventions and any spec error.</summary>
    DocSeeded,

    /// <summary>Expected value derived by a method <i>independent</i> of the spec doc — a
    /// regulator's published figure, a spreadsheet IRR (B.8). Catches the
    /// formula-transcription / convention-misread errors a doc-seeded fixture cannot.</summary>
    IndependentAnchor,
}

/// <summary>
/// One golden-corpus case: a kernel computation pinned to an expected value with recorded
/// provenance. Replayed by <c>GoldenCorpusTests</c> (the fitness function B.9 property tests
/// and B.10 mutation testing build on).
/// </summary>
/// <remarks>
/// The expected value is a <see cref="string"/>, not a <see cref="decimal"/>: this corpus lives
/// in <c>Babelstone.FinancialMath.Tests</c>, outside the <c>Babelstone.FinancialTypes.*</c>
/// subtree that BMNY002 exempts, so a stored <c>decimal</c> field/property would fail the build.
/// The replay parses it to a <c>decimal</c> local (locals are untouched by the ban) and compares
/// to <see cref="Compute"/>'s result at <see cref="Precision"/> decimal places.
/// </remarks>
/// <param name="Id">Human-readable case name, prefixed with its fin-math section.</param>
/// <param name="Tier">Doc-seeded (B.6) vs independent external anchor (B.8).</param>
/// <param name="Provenance">Where the expected value comes from — the audit trail B.8 requires.</param>
/// <param name="ExpectedText">Expected value, invariant-culture decimal text.</param>
/// <param name="Precision">Decimal places the comparison rounds to (0 for exact-cent money).</param>
/// <param name="Compute">Calls the kernel and returns the actual value (Money exposes .Cents).</param>
public sealed record GoldenCase(
    string Id,
    CorpusTier Tier,
    string Provenance,
    string ExpectedText,
    int Precision,
    Func<decimal> Compute);

/// <summary>
/// The sealed FINANCIAL_MATH golden corpus — the doc-seeded worked examples of fin-math §5–§6,
/// each tagged with its provenance and replayed against the live kernel. The independent
/// external-anchor tier (B.8) appends to <see cref="Cases"/>; it does not replace the doc-seeded
/// tier. Parallels <c>MoneyBoundaryFixtures</c> (the §P2 boundary corpus) one layer up: that pins
/// the rounding boundary, this pins the formulas that feed it.
/// </summary>
public static class FinancialMathGoldenCorpus
{
    private static readonly Money TenThousand = new(1_000_000L); // €10,000 — the running example

    // The §4.1/§6 Price credit: net disbursement at t=0, then n equal installments at t=1..n.
    private static List<(Money Amount, int Period)> PriceCredit(long netDisbursementCents, long installmentCents, int n)
    {
        var flows = new List<(Money Amount, int Period)> { (new Money(netDisbursementCents), 0) };
        for (int t = 1; t <= n; t++)
            flows.Add((new Money(-installmentCents), t));
        return flows;
    }

    public static readonly IReadOnlyList<GoldenCase> Cases = new[]
    {
        // --- §5.1 Simple interest — €10,000 at TAN 6%, Act/360 over 365 actual days → €608.33.
        new GoldenCase(
            "§5.1 simple interest €10k 6% Act/360 365d", CorpusTier.DocSeeded,
            "fin-math §5.1 worked example → €608.33", "60833", 0,
            () => (decimal)Accrual.SimpleInterest(
                TenThousand, 600,
                DayCount.Between(new DateOnly(2023, 1, 1), new DateOnly(2024, 1, 1), DayCountConvention.Act360)).Cents),

        // --- §5.2 Compound maturity — €10,000 at TAN 6%, monthly, 12 periods: 10000×(1.005)^12.
        new GoldenCase(
            "§5.2 compound maturity €10k 6% monthly 12p", CorpusTier.DocSeeded,
            "fin-math §5.2 M = C×(1+TAN/m)^(m·n) → €10,616.78", "1061678", 0,
            () => (decimal)Accrual.CompoundMaturity(TenThousand, 600, 12, 12).Cents),

        // --- §5.4 TAE — TAN 6% monthly: (1+0.06/12)^12 − 1 ≈ 6.1678%.
        new GoldenCase(
            "§5.4 TAE 6% monthly", CorpusTier.DocSeeded,
            "fin-math §5.4 worked example → ≈6.17%", "0.061678", 6,
            () => Rates.Tae(600, 12)),

        // --- §5.4 Withholding — 28% IRS on the €608.33 gross flow: tax €170.33, net €438.00.
        new GoldenCase(
            "§5.4 withholding tax 28% on €608.33", CorpusTier.DocSeeded,
            "fin-math §5.4 flow-by-flow withholding → tax €170.33", "17033", 0,
            () => (decimal)Withholding.Withhold(new Money(60_833L), 2800).Tax.Cents),
        new GoldenCase(
            "§5.4 withholding net 28% on €608.33", CorpusTier.DocSeeded,
            "fin-math §5.4 flow-by-flow withholding → net €438.00", "43800", 0,
            () => (decimal)Withholding.Withhold(new Money(60_833L), 2800).Net.Cents),

        // --- §6.1 IRR — Price credit €10,000 / 12 × €860.66 → 0.5%/month (no charges ⇒ IRR = TAN/m).
        new GoldenCase(
            "§6.1 IRR price credit no charges", CorpusTier.DocSeeded,
            "fin-math §6.1 worked example → 0.5%/month", "0.005", 4,
            () => Rates.InternalRateOfReturn(PriceCredit(1_000_000L, 86_066L, 12))),

        // --- §6.1 IRR — single-period deposit: exact growth rate 0.060833 (PV = 0 there).
        new GoldenCase(
            "§6.1 IRR single-period deposit", CorpusTier.DocSeeded,
            "fin-math §5.3/§6.1 → 1,060,833/1,000,000 − 1", "0.060833", 6,
            () => Rates.InternalRateOfReturn(
                new List<(Money Amount, int Period)> { (new Money(-1_000_000L), 0), (new Money(1_060_833L), 1) })),

        // --- §6.2 TAEG — €200 fee netted: exact vector solves to ≈10.25% (the doc's pre-rounded
        //     10.27% is corrected in §6.2; this pins the precise value).
        new GoldenCase(
            "§6.2 TAEG with €200 origination fee", CorpusTier.DocSeeded,
            "fin-math §6.2 (corrected) → ≈10.25%", "0.1025", 4,
            () => Rates.Taeg(PriceCredit(980_000L, 86_066L, 12), 12)),

        // ===================================================================================
        // Independent external-anchor tier (B.8). Expected values are computed by a method
        // INDEPENDENT of the fin-math doc — closed-form algebra or a spreadsheet formula on
        // inputs the doc never worked out — so the two tiers cannot share a transcription or
        // convention-misread blind spot (it was an independent recompute that caught the §6.2
        // doc error). Conventions are pinned explicitly (Act/360, HALF_EVEN, proportional rate
        // TAN/m, flow-by-flow withholding) so an external number never disagrees for a non-bug
        // reason. Each Provenance records the exact independent computation — the audit trail.
        //
        // DEFERRED — regulator-published cross-check: DL 133/2009 Anexo / Banco de Portugal
        // worked examples (feed the published CF vector to the solver, assert it reproduces the
        // published TAEG) need the authentic regulatory figures to keep provenance auditable;
        // fabricating one would defeat the tier's purpose. Tracked as a follow-up. Also deferred
        // with §4/§7: PMT/IPMT amortization-schedule cross-checks (out of v1 calculator scope).
        // ===================================================================================

        // --- Simple interest, independent inputs: €25,000 at 3.5%, Act/360 over a 90-day
        //     quarter (2024-01-01 → 2024-03-31, a leap year: 31+29+30 = 90 days).
        new GoldenCase(
            "simple interest €25k 3.5% Act/360 90d", CorpusTier.IndependentAnchor,
            "spreadsheet =25000*0.035*90/360 = €218.75; Act/360, proportional rate", "21875", 0,
            () => (decimal)Accrual.SimpleInterest(
                new Money(2_500_000L), 350,
                DayCount.Between(new DateOnly(2024, 1, 1), new DateOnly(2024, 3, 31), DayCountConvention.Act360)).Cents),

        // --- Compound maturity, independent inputs: €5,000 at 4%, quarterly (m=4), 2 years.
        new GoldenCase(
            "compound maturity €5k 4% quarterly 8p", CorpusTier.IndependentAnchor,
            "spreadsheet =5000*(1+0.04/4)^8 = 5000*(1.01)^8 = €5,414.28", "541428", 0,
            () => (decimal)Accrual.CompoundMaturity(new Money(500_000L), 400, 4, 8).Cents),

        // --- TAE, independent inputs: 4% quarterly → (1.01)^4 − 1 = 0.04060401.
        new GoldenCase(
            "TAE 4% quarterly", CorpusTier.IndependentAnchor,
            "spreadsheet =(1+0.04/4)^4-1 = (1.01)^4-1 = 0.040604", "0.040604", 6,
            () => Rates.Tae(400, 4)),

        // --- Withholding, independent gross: 28% IRS on €1,234.56 → tax €345.68 (HALF_EVEN on
        //     34,567.68¢), net €888.88. Pins the round-once tax leg and the conserving net leg.
        new GoldenCase(
            "withholding tax 28% on €1,234.56", CorpusTier.IndependentAnchor,
            "spreadsheet =1234.56*0.28 = 345.6768 → €345.68 (HALF_EVEN)", "34568", 0,
            () => (decimal)Withholding.Withhold(new Money(123_456L), 2800).Tax.Cents),
        new GoldenCase(
            "withholding net 28% on €1,234.56", CorpusTier.IndependentAnchor,
            "spreadsheet =1234.56-345.68 = €888.88 (net = gross − tax)", "88888", 0,
            () => (decimal)Withholding.Withhold(new Money(123_456L), 2800).Net.Cents),

        // --- IRR, 1-period ratio: −€1,000 then +€1,100 → 1100/1000 − 1 = 0.10 exactly.
        new GoldenCase(
            "IRR 1-period €1,000 → €1,100", CorpusTier.IndependentAnchor,
            "ratio =1100/1000 - 1 = 0.10", "0.10", 6,
            () => Rates.InternalRateOfReturn(
                new List<(Money Amount, int Period)> { (new Money(-100_000L), 0), (new Money(110_000L), 1) })),

        // --- IRR, 2-period closed form: −€1,000, +€500, +€600. With x = 1/(1+i) this is the
        //     quadratic 6x² + 5x − 10 = 0 → x = (−5+√265)/12 → i ≈ 0.06394. Validates the
        //     iterative Newton-Raphson solver against an ALGEBRAIC root (n=2 has a closed form;
        //     n≥5 does not — which is why IRR is numerical), the strongest independence there is.
        new GoldenCase(
            "IRR 2-period closed-form quadratic", CorpusTier.IndependentAnchor,
            "quadratic 6x²+5x−10=0, x=1/(1+i), x=(−5+√265)/12 → i ≈ 0.06394", "0.0639", 4,
            () => Rates.InternalRateOfReturn(
                new List<(Money Amount, int Period)> { (new Money(-100_000L), 0), (new Money(50_000L), 1), (new Money(60_000L), 2) })),

        // --- TAEG, closed form: a 10%/quarter return annualized over m=4 → (1.10)^4 − 1 = 0.4641
        //     exactly. Validates the annualize-the-IRR composition on a vector with a clean root.
        new GoldenCase(
            "TAEG 10%/quarter annualized (m=4)", CorpusTier.IndependentAnchor,
            "closed form =(1.10)^4-1 = 0.4641; IRR = 1100/1000 − 1 = 0.10/quarter", "0.4641", 4,
            () => Rates.Taeg(
                new List<(Money Amount, int Period)> { (new Money(-100_000L), 0), (new Money(110_000L), 1) }, 4)),
    };
}
