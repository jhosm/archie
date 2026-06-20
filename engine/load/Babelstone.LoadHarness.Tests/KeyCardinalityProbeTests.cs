using Xunit;

namespace Babelstone.LoadHarness.Tests;

/// <summary>
/// Pure (no-container) tests for the §M.6 key-cardinality probe's measurement logic (bd c14p.2): the
/// p99 reduction and the cardinality-slope verdict (<see cref="KeyCardinalityReport.LatencyIsFlat"/>).
/// These run in the default unit lane — they do NOT need OpenBao; the real seed+measure pass against a
/// dev-mode OpenBao is <see cref="KeyCardinalityProbeIntegrationTests"/> in the Integration lane.
/// </summary>
public sealed class KeyCardinalityReportTests
{
    [Fact]
    public void Flat_op_latency_across_growing_cardinality_passes()
    {
        // A constant-time transit engine: encrypt/decrypt/destroy p99 are independent of how many keys
        // are resident — the verdict the per-subject-named-key design needs to scale to v4 cardinality.
        var report = new KeyCardinalityReport(Seed: 1, TotalSubjects: 1000,
        [
            new KeyCardinalityCheckpoint(ResidentKeys: 100, EncryptP99Ms: 2.0, DecryptP99Ms: 1.5, DestroyP99Ms: 3.0),
            new KeyCardinalityCheckpoint(ResidentKeys: 500, EncryptP99Ms: 2.1, DecryptP99Ms: 1.6, DestroyP99Ms: 3.1),
            new KeyCardinalityCheckpoint(ResidentKeys: 1000, EncryptP99Ms: 2.2, DecryptP99Ms: 1.4, DestroyP99Ms: 2.9),
        ]);

        Assert.True(report.LatencyIsFlat());
        Assert.Contains("FLAT", report.Summary());
    }

    [Fact]
    public void Op_latency_that_climbs_with_cardinality_fails_the_verdict()
    {
        // The v4-scale risk realised: per-key op latency rises sharply as the resident population grows
        // (e.g. a memory-resident keyspace pushing the engine into GC/page pressure). LatencyIsFlat must
        // catch it — this is the falsifiable signal that triggers a sharding / destroyable-DEK mitigation.
        var report = new KeyCardinalityReport(Seed: 2, TotalSubjects: 1000,
        [
            new KeyCardinalityCheckpoint(ResidentKeys: 100, EncryptP99Ms: 2.0, DecryptP99Ms: 1.5, DestroyP99Ms: 3.0),
            new KeyCardinalityCheckpoint(ResidentKeys: 1000, EncryptP99Ms: 40.0, DecryptP99Ms: 30.0, DestroyP99Ms: 50.0),
        ]);

        Assert.False(report.LatencyIsFlat());
        Assert.Contains("DEGRADING", report.Summary());
    }

    [Fact]
    public void A_single_checkpoint_has_no_slope_to_judge_and_is_vacuously_flat()
    {
        var report = new KeyCardinalityReport(Seed: 3, TotalSubjects: 50,
            [new KeyCardinalityCheckpoint(ResidentKeys: 50, EncryptP99Ms: 2.0, DecryptP99Ms: 1.5, DestroyP99Ms: 3.0)]);

        Assert.True(report.LatencyIsFlat());
    }

    [Fact]
    public void Sub_millisecond_jitter_off_a_near_zero_baseline_does_not_read_as_degradation()
    {
        // In-memory dev ops are sub-ms; a 0.2ms → 0.6ms wobble is 3× by ratio but absolutely trivial.
        // The floor guard keeps that jitter from reading as a cardinality slope (a false DEGRADING).
        var report = new KeyCardinalityReport(Seed: 4, TotalSubjects: 1000,
        [
            new KeyCardinalityCheckpoint(ResidentKeys: 100, EncryptP99Ms: 0.2, DecryptP99Ms: 0.2, DestroyP99Ms: 0.3),
            new KeyCardinalityCheckpoint(ResidentKeys: 1000, EncryptP99Ms: 0.6, DecryptP99Ms: 0.5, DestroyP99Ms: 0.9),
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
}
