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
    };
}
