using Babelstone.LoadHarness;
using Babelstone.Telemetry;
using Xunit;

namespace Babelstone.LoadHarness.Runner.Tests;

/// <summary>
/// Docker-free tests for the L.5 snapshot-accelerated replay verdict (bd babelstone-0uau.1) and the L.3e
/// synchronous-replication append-latency verdict (bd babelstone-2e6q.5). The snapshot verdict gates on
/// BYTE-IDENTITY (ADR-PC-003 §P3: snapshots accelerate, never lie) with the speedup reported and budget
/// enforced; the repl-latency verdict folds the §P1 cost and is GATING only against a real warm standby
/// (advisory on the single-node dev stack, ADR-PC-005 §P1).
/// </summary>
public sealed class SnapshotReplayVerdictTests
{
    private static SyncLatencyBand Band => new("current_balance", BabelstoneAttributes.SpanAccrualComputed, 20, 80, 200);

    private static LatencyVerdict GreenBand() =>
        new(Band, new LatencyPercentiles(10, 5, 10, 15), Passed: true, "within budget");

    // --- SnapshotReplayVerdict: identity gates, speedup qualifies ---

    [Fact]
    public void Snapshot_replay_passes_when_identical_faster_and_within_budget()
    {
        // Cold 1000 ms, snapshot 200 ms ⇒ 5× faster, identical, within the 5s budget, 1 snapshot applied.
        var v = new SnapshotReplayVerdict(
            ColdMs: 1_000, SnapshotMs: 200, BudgetMs: ReplayVerdict.WithAPlanBudgetMs,
            StateIdentical: true, SnapshotsApplied: 1, EventsRefolded: 500);

        Assert.True(v.Passed);
        Assert.Equal(5.0, v.Speedup, 3);
        Assert.Contains("identical", v.Reason);
        Assert.Contains("faster", v.Reason);
    }

    [Fact]
    public void A_divergent_snapshot_fails_no_matter_how_fast()
    {
        // The worst event-sourcing failure: a fast snapshot that does NOT match the cold fold (§P3/§P4).
        var v = new SnapshotReplayVerdict(
            ColdMs: 1_000, SnapshotMs: 10, BudgetMs: ReplayVerdict.WithAPlanBudgetMs,
            StateIdentical: false, SnapshotsApplied: 1, EventsRefolded: 500);

        Assert.False(v.Passed);
        Assert.Contains("DIVERGENCE", v.Reason);
    }

    [Fact]
    public void Zero_snapshots_applied_is_an_explicit_fail()
    {
        // A "speedup" claim over a path that read no snapshot is vacuous — the same anti-vacuous-pass
        // posture the no-divergence verdict takes on zero streams.
        var v = new SnapshotReplayVerdict(
            ColdMs: 1_000, SnapshotMs: 900, BudgetMs: ReplayVerdict.WithAPlanBudgetMs,
            StateIdentical: true, SnapshotsApplied: 0, EventsRefolded: 500);

        Assert.False(v.Passed);
        Assert.Contains("no snapshot applied", v.Reason);
    }

    [Fact]
    public void Identical_but_not_faster_fails()
    {
        // Correct but no speedup (snapshot not faster than cold): the acceleration did not earn its keep on
        // a deep stream (ADR-PC-003 §8.2 frames snapshots as the mechanism that keeps deep replay in budget).
        var v = new SnapshotReplayVerdict(
            ColdMs: 500, SnapshotMs: 500, BudgetMs: ReplayVerdict.WithAPlanBudgetMs,
            StateIdentical: true, SnapshotsApplied: 1, EventsRefolded: 500);

        Assert.False(v.Passed);
        Assert.Contains("not faster enough", v.Reason);
    }

    [Fact]
    public void Identical_and_faster_but_over_budget_fails()
    {
        // Faster than cold and identical, but the snapshot path itself blew the §8.2 budget — still a FAIL.
        var v = new SnapshotReplayVerdict(
            ColdMs: 40_000, SnapshotMs: 31_000, BudgetMs: ReplayVerdict.IrregularBudgetMs,
            StateIdentical: true, SnapshotsApplied: 1, EventsRefolded: 1_000);

        Assert.False(v.Passed);
    }

    [Fact]
    public void A_failing_snapshot_replay_fails_the_run_even_with_green_bands()
    {
        var artefact = new RunArtefact(
            1, "rev", Calibration.V4Placeholder(), [GreenBand()], 100,
            SnapshotReplay: new SnapshotReplayVerdict(1_000, 10, ReplayVerdict.WithAPlanBudgetMs, StateIdentical: false, 1, 500));

        Assert.False(artefact.Passed);
        Assert.Contains("snapshot-replay FAIL", artefact.Summary());
    }

    [Fact]
    public void A_green_snapshot_replay_passes_and_summary_names_the_speedup()
    {
        var artefact = new RunArtefact(
            7, "abc", Calibration.V4Placeholder(), [GreenBand()], 250,
            SnapshotReplay: new SnapshotReplayVerdict(1_000, 200, ReplayVerdict.WithAPlanBudgetMs, StateIdentical: true, 1, 500));

        Assert.True(artefact.Passed);
        Assert.Contains("snapshot-replay PASS", artefact.Summary());
        // The speedup is reported next to the verdict (the decimal separator is culture-dependent, so
        // assert the stable framing rather than a locale-specific "5.0").
        Assert.Contains("× vs cold", artefact.Summary());
    }

    // --- ReplicationLatencyVerdict: §P1 cost; gating only against a real standby ---

    [Fact]
    public void Repl_latency_reports_the_p1_delta()
    {
        // off p50/p99 = 2/5 ms, on p50/p99 = 6/12 ms ⇒ delta p50 +4, p99 +7.
        var v = new ReplicationLatencyVerdict(
            SyncOffP50Ms: 2, SyncOffP99Ms: 5, SyncOnP50Ms: 6, SyncOnP99Ms: 12, Samples: 50, StandbyConfirmed: true);

        Assert.Equal(4, v.DeltaP50Ms, 3);
        Assert.Equal(7, v.DeltaP99Ms, 3);
    }

    [Fact]
    public void Repl_latency_is_advisory_and_always_passes_without_a_confirmed_standby()
    {
        // Single-node dev stack: the "on" side did not block on a second node, so even a large sync-ON p99
        // is a floor, not the production cost — advisory, non-gating (the KeyCardinalityProbe posture).
        var v = new ReplicationLatencyVerdict(
            SyncOffP50Ms: 2, SyncOffP99Ms: 5, SyncOnP50Ms: 400, SyncOnP99Ms: 900, Samples: 50, StandbyConfirmed: false);

        Assert.True(v.Passed);
        Assert.Contains("ADVISORY", v.Reason);
    }

    [Fact]
    public void Repl_latency_gates_on_the_sync_band_against_a_confirmed_standby()
    {
        // With a real standby the sync-ON append p99 is held to the §8.3 200ms band: 900ms breaches.
        var breach = new ReplicationLatencyVerdict(
            SyncOffP50Ms: 2, SyncOffP99Ms: 5, SyncOnP50Ms: 400, SyncOnP99Ms: 900, Samples: 50, StandbyConfirmed: true);
        Assert.False(breach.Passed);
        Assert.Contains("BREACHED", breach.Reason);

        // A modest sync-ON p99 within the band passes against a confirmed standby.
        var within = new ReplicationLatencyVerdict(
            SyncOffP50Ms: 2, SyncOffP99Ms: 5, SyncOnP50Ms: 30, SyncOnP99Ms: 120, Samples: 50, StandbyConfirmed: true);
        Assert.True(within.Passed);
        Assert.Contains("within", within.Reason);
    }

    [Fact]
    public void A_breaching_repl_latency_against_a_standby_fails_the_run()
    {
        var artefact = new RunArtefact(
            1, "rev", Calibration.V4Placeholder(), [GreenBand()], 100,
            ReplicationLatency: new ReplicationLatencyVerdict(2, 5, 400, 900, 50, StandbyConfirmed: true));

        Assert.False(artefact.Passed);
        Assert.Contains("repl-latency FAIL", artefact.Summary());
    }

    [Fact]
    public void An_advisory_repl_latency_does_not_fail_the_run()
    {
        var artefact = new RunArtefact(
            1, "rev", Calibration.V4Placeholder(), [GreenBand()], 100,
            ReplicationLatency: new ReplicationLatencyVerdict(2, 5, 400, 900, 50, StandbyConfirmed: false));

        Assert.True(artefact.Passed);
        Assert.Contains("repl-latency PASS", artefact.Summary());
    }
}
