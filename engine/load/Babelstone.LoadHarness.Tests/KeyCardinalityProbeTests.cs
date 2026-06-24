using Xunit;

namespace Babelstone.LoadHarness.Tests;

/// <summary>
/// Pure (no-container) tests for the §M.6 key-cardinality probe's measurement logic (bd c14p.2): the
/// percentile reduction and the cardinality-slope verdict (<see cref="KeyCardinalityReport.LatencyIsFlat"/>),
/// which is judged on the MEDIAN so single-sample container jitter cannot flip it (bd babelstone-tihv).
/// These run in the default unit lane — they do NOT need OpenBao; the real seed+measure pass against a
/// dev-mode OpenBao is <see cref="KeyCardinalityProbeIntegrationTests"/> in the Integration lane.
/// </summary>
public sealed class KeyCardinalityReportTests
{
    [Fact]
    public void Flat_op_latency_across_growing_cardinality_passes()
    {
        // A constant-time transit engine: encrypt/decrypt/destroy latency is independent of how many keys
        // are resident — the verdict the per-subject-named-key design needs to scale to v4 cardinality.
        var report = new KeyCardinalityReport(Seed: 1, TotalSubjects: 1000,
        [
            new KeyCardinalityCheckpoint(ResidentKeys: 100, new(2.0, 4.0), new(1.5, 3.0), new(3.0, 5.0)),
            new KeyCardinalityCheckpoint(ResidentKeys: 500, new(2.1, 4.2), new(1.6, 3.1), new(3.1, 5.2)),
            new KeyCardinalityCheckpoint(ResidentKeys: 1000, new(2.2, 4.1), new(1.4, 2.9), new(2.9, 4.8)),
        ]);

        Assert.True(report.LatencyIsFlat());
        Assert.Contains("FLAT", report.Summary());
    }

    [Fact]
    public void Op_latency_that_climbs_with_cardinality_fails_the_verdict()
    {
        // The v4-scale risk realised: per-key op latency rises sharply as the resident population grows
        // (e.g. a memory-resident keyspace pushing the engine into GC/page pressure). A real slope shifts
        // the whole distribution, so the MEDIAN climbs — LatencyIsFlat must catch it. This is the
        // falsifiable signal that triggers a sharding / destroyable-DEK mitigation.
        var report = new KeyCardinalityReport(Seed: 2, TotalSubjects: 1000,
        [
            new KeyCardinalityCheckpoint(ResidentKeys: 100, new(2.0, 4.0), new(1.5, 3.0), new(3.0, 5.0)),
            new KeyCardinalityCheckpoint(ResidentKeys: 1000, new(40.0, 60.0), new(30.0, 45.0), new(50.0, 70.0)),
        ]);

        Assert.False(report.LatencyIsFlat());
        Assert.Contains("DEGRADING", report.Summary());
    }

    [Fact]
    public void A_single_transient_tail_spike_does_not_flip_the_verdict()
    {
        // The bug this issue fixes (bd babelstone-tihv): the OLD verdict compared p99s computed over a
        // 15-sample window, where nearest-rank p99 IS the single slowest request — so one contended
        // round-trip to the dev-mode container flipped FLAT→DEGRADING (observed on PR #316). The median is
        // immune: the median here is dead flat (2.0 → 2.1) while the peak p99 has spiked to 70ms; the
        // verdict must stay FLAT, because a lone tail spike is jitter, not a cardinality slope.
        var report = new KeyCardinalityReport(Seed: 5, TotalSubjects: 1000,
        [
            new KeyCardinalityCheckpoint(ResidentKeys: 100, new(2.0, 4.0), new(1.5, 3.0), new(3.0, 5.0)),
            new KeyCardinalityCheckpoint(ResidentKeys: 1000, new(2.1, 70.0), new(1.6, 65.0), new(3.1, 76.0)),
        ]);

        Assert.True(report.LatencyIsFlat());
        Assert.Contains("FLAT", report.Summary());
    }

    [Fact]
    public void A_single_checkpoint_has_no_slope_to_judge_and_is_vacuously_flat()
    {
        var report = new KeyCardinalityReport(Seed: 3, TotalSubjects: 50,
            [new KeyCardinalityCheckpoint(ResidentKeys: 50, new(2.0, 4.0), new(1.5, 3.0), new(3.0, 5.0))]);

        Assert.True(report.LatencyIsFlat());
    }

    [Fact]
    public void Sub_millisecond_jitter_off_a_near_zero_baseline_does_not_read_as_degradation()
    {
        // In-memory dev ops are sub-ms; a 0.2ms → 0.6ms median wobble is 3× by ratio but absolutely
        // trivial. The floor guard keeps that jitter from reading as a cardinality slope (a false DEGRADING).
        var report = new KeyCardinalityReport(Seed: 4, TotalSubjects: 1000,
        [
            new KeyCardinalityCheckpoint(ResidentKeys: 100, new(0.2, 0.4), new(0.2, 0.4), new(0.3, 0.6)),
            new KeyCardinalityCheckpoint(ResidentKeys: 1000, new(0.6, 1.0), new(0.5, 0.9), new(0.9, 1.4)),
        ]);

        Assert.True(report.LatencyIsFlat());
    }

    [Fact]
    public void P99_is_the_nearest_rank_of_the_sample()
    {
        // 100 samples 1..100 → nearest-rank p99 is the 99th value, 99.
        var samples = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();
        Assert.Equal(99.0, KeyCardinalityProbe.P99(samples));
    }

    [Fact]
    public void OpLatency_From_reduces_a_sample_to_its_median_and_p99()
    {
        // 100 samples 1..100 → nearest-rank median (p50) is the 50th value, p99 the 99th.
        var op = OpLatency.From(Enumerable.Range(1, 100).Select(i => (double)i).ToArray());
        Assert.Equal(50.0, op.MedianMs);
        Assert.Equal(99.0, op.P99Ms);
    }
}
