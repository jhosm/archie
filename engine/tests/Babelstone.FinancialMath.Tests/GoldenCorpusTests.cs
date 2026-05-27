using System.Globalization;
using Xunit;

namespace Babelstone.FinancialMath.Tests;

/// <summary>
/// Replays the sealed <see cref="FinancialMathGoldenCorpus"/> against the live kernel — the
/// fitness function that pins every fin-math §5–§6 worked example and that B.9 (property-based
/// suite) and B.10 (mutation testing) extend. A drift in any primitive surfaces here as a named
/// case failure with its provenance, not a bare assertion. Governs ADR-PC-010 §P1–§P2.
/// </summary>
public class GoldenCorpusTests
{
    [Fact]
    public void Doc_seeded_corpus_reproduces_every_fin_math_worked_example()
    {
        var failures = CheckTier(CorpusTier.DocSeeded);
        Assert.True(failures.Count == 0, "Golden-corpus drift (doc-seeded tier):\n" + string.Join("\n", failures));
    }

    // Every case must be reachable and produce a finite result — guards against a case whose
    // Compute throws (a kernel guard wrongly tripped by a fixture) regardless of tier.
    [Fact]
    public void Every_corpus_case_has_provenance_and_computes()
    {
        Assert.All(FinancialMathGoldenCorpus.Cases, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Provenance), $"{c.Id} has no recorded provenance.");
            _ = c.Compute(); // must not throw
        });
    }

    // Collects all drifting cases in a tier rather than failing on the first, so one run reports
    // the full blast radius. Uses xUnit's precision overload — never Math.Round (BMNY001).
    private static List<string> CheckTier(CorpusTier tier)
    {
        var failures = new List<string>();
        foreach (var c in FinancialMathGoldenCorpus.Cases)
        {
            if (c.Tier != tier)
                continue;
            try
            {
                decimal expected = decimal.Parse(c.ExpectedText, CultureInfo.InvariantCulture);
                Assert.Equal(expected, c.Compute(), c.Precision);
            }
            catch (Exception e)
            {
                failures.Add($"  {c.Id} [{c.Provenance}]: {e.Message}");
            }
        }
        return failures;
    }
}
