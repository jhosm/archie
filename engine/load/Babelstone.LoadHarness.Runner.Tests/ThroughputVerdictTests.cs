using Babelstone.LoadHarness;
using Babelstone.Telemetry;
using Xunit;

namespace Babelstone.LoadHarness.Runner.Tests;

/// <summary>
/// Docker-free tests for the L.3b sustained-throughput verdict (bd babelstone-2e6q.2): the runner holds
/// a steady target TPS for a configured duration and folds an achieved-vs-target verdict into the
/// artefact. The falsifiable property: a producer that silently fell to a fraction of the target would
/// pass the latency bands vacuously — the throughput verdict is what catches it.
/// </summary>
public sealed class ThroughputVerdictTests
{
    private static SyncLatencyBand Band => new("current_balance", BabelstoneAttributes.SpanAccrualComputed, 20, 80, 200);

    private static LatencyVerdict GreenBand() =>
        new(Band, new LatencyPercentiles(10, 5, 10, 15), Passed: true, "within budget");

    [Fact]
    public void Passes_within_tolerance()
    {
        // 240 achieved vs 250 target at 10% tolerance (>= 225 required) -> pass.
        var v = new ThroughputVerdict("sustained", 250, 240, 0.10);
        Assert.True(v.Passed);
        Assert.Contains("held 240 TPS", v.Reason);
    }

    [Fact]
    public void Fails_a_silent_collapse_below_tolerance()
    {
        var v = new ThroughputVerdict("sustained", 250, 40, 0.10);
        Assert.False(v.Passed);
        Assert.Contains("only 40 TPS", v.Reason);
    }

    [Fact]
    public void Exactly_at_the_tolerance_floor_passes()
    {
        // 225 == 250 * (1 - 0.10): the boundary is inclusive.
        var v = new ThroughputVerdict("sustained", 250, 225, 0.10);
        Assert.True(v.Passed);
    }

    [Fact]
    public void A_throughput_breach_fails_the_run_even_with_green_bands()
    {
        var artefact = new RunArtefact(
            1, "rev", Calibration.V4Placeholder(), [GreenBand()], 100,
            Throughput: new ThroughputVerdict("sustained", 250, 40, 0.10));
        Assert.False(artefact.Passed);
        Assert.Contains("throughput FAIL", artefact.Summary());
    }

    [Fact]
    public void A_held_rate_passes_the_run_with_green_bands()
    {
        var artefact = new RunArtefact(
            7, "abc123", Calibration.V4Placeholder(), [GreenBand()], 2500,
            Throughput: new ThroughputVerdict("sustained", 250, 250, 0.10));
        Assert.True(artefact.Passed);
        Assert.Contains("throughput PASS", artefact.Summary());
    }
}
