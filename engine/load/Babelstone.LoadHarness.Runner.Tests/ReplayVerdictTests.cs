using Babelstone.LoadHarness;
using Babelstone.Telemetry;
using Xunit;

namespace Babelstone.LoadHarness.Runner.Tests;

/// <summary>
/// Docker-free tests for the L.3d cold-replay budget + no-rebuild-divergence verdicts
/// (bd babelstone-2e6q.4). The budgets are the feature-design event-store §8.2 numbers verbatim
/// (5s with-a-plan / 30s irregular); the no-divergence verdict is the §8.3 reliability invariant
/// (a cold rebuild reproduces the running belief byte-for-byte), with "zero streams checked" an
/// explicit FAIL — the same anti-vacuous-pass posture the latency observer takes on "no spans".
/// </summary>
public sealed class ReplayVerdictTests
{
    private static SyncLatencyBand Band => new("current_balance", BabelstoneAttributes.SpanAccrualComputed, 20, 80, 200);

    private static LatencyVerdict GreenBand() =>
        new(Band, new LatencyPercentiles(10, 5, 10, 15), Passed: true, "within budget");

    // --- ReplayVerdict: the §8.2 5s/30s budgets ---

    [Fact]
    public void Budget_constants_match_event_store_section_8_2()
    {
        Assert.Equal(5_000, ReplayVerdict.WithAPlanBudgetMs);   // §8.2 v1 with-a-plan: under 5 seconds
        Assert.Equal(30_000, ReplayVerdict.IrregularBudgetMs);  // §8.2 v4 irregular: under 30 seconds
    }

    [Fact]
    public void Passes_under_the_with_a_plan_5s_budget()
    {
        var v = new ReplayVerdict("with-a-plan", 200, 1_200, ReplayVerdict.WithAPlanBudgetMs);
        Assert.True(v.Passed);
        Assert.Contains("cold-rebuilt 200 events", v.Reason);
    }

    [Fact]
    public void Fails_over_the_irregular_30s_budget()
    {
        var v = new ReplayVerdict("irregular", 1_000, 31_000, ReplayVerdict.IrregularBudgetMs);
        Assert.False(v.Passed);
        Assert.Contains("exceeded budget", v.Reason);
    }

    // --- NoDivergenceVerdict: the §8.3 reliability invariant ---

    [Fact]
    public void No_divergence_passes_when_every_rebuilt_stream_matches()
    {
        var v = new NoDivergenceVerdict(StreamsChecked: 5, DivergentStreams: 0, EventsRefolded: 25);
        Assert.True(v.Passed);
        Assert.Contains("clean", v.Reason);
    }

    [Fact]
    public void No_divergence_fails_on_any_divergent_stream()
    {
        var v = new NoDivergenceVerdict(StreamsChecked: 5, DivergentStreams: 1, EventsRefolded: 25);
        Assert.False(v.Passed);
        Assert.Contains("DIVERGENCE", v.Reason);
    }

    [Fact]
    public void No_divergence_over_zero_streams_is_an_explicit_fail()
    {
        var v = new NoDivergenceVerdict(StreamsChecked: 0, DivergentStreams: 0, EventsRefolded: 0);
        Assert.False(v.Passed);
        Assert.Contains("vacuous", v.Reason);
    }

    // --- RunArtefact folding for the replay slice ---

    [Fact]
    public void A_replay_budget_breach_fails_the_run_even_with_green_bands()
    {
        var artefact = new RunArtefact(
            1, "rev", Calibration.V4Placeholder(), [GreenBand()], 100,
            Replay: new ReplayVerdict("with-a-plan", 300, 9_000, ReplayVerdict.WithAPlanBudgetMs));
        Assert.False(artefact.Passed);
        Assert.Contains("replay FAIL", artefact.Summary());
    }

    [Fact]
    public void A_divergence_fails_the_run_even_with_green_bands()
    {
        var artefact = new RunArtefact(
            1, "rev", Calibration.V4Placeholder(), [GreenBand()], 100,
            NoDivergence: new NoDivergenceVerdict(3, 1, 12));
        Assert.False(artefact.Passed);
        Assert.Contains("no-divergence FAIL", artefact.Summary());
    }

    [Fact]
    public void All_green_replay_verdicts_pass_and_summary_names_each()
    {
        var artefact = new RunArtefact(
            7, "abc123", Calibration.V4Placeholder(), [GreenBand()], 250,
            Replay: new ReplayVerdict("with-a-plan", 300, 1_200, ReplayVerdict.WithAPlanBudgetMs),
            NoDivergence: new NoDivergenceVerdict(10, 0, 50));

        Assert.True(artefact.Passed);
        var summary = artefact.Summary();
        Assert.Contains("replay PASS", summary);
        Assert.Contains("no-divergence PASS", summary);
    }
}
