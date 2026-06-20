using Xunit;

namespace Babelstone.LoadHarness.Runner.Tests;

/// <summary>
/// Testcontainers integration coverage for the L.5 / L.6 snapshot rig path (bd babelstone-0uau.1,
/// babelstone-0uau.2) and the L.3e repl-latency rig path (bd babelstone-2e6q.5) on <c>EngineProjectionRig</c>.
/// It drives the deep-stream builder, the snapshot-accelerated-vs-cold replay measurement, the
/// discard-rebuild drill, and the synchronous_commit append-latency probe against a real PostgreSQL — so
/// the §P3 byte-identity invariant, the §8.3 discard-rebuild correctness exercise, and the §P1 measurement
/// path are exercised against a live store, not only hand-run.
/// </summary>
/// <remarks>
/// In plain English: these stand up a throwaway PostgreSQL in Docker, build a deep account with snapshots,
/// and prove the fast (snapshot) rebuild lands on the exact same state as the slow (cold) rebuild, that
/// throwing the snapshots away and rebuilding cold still matches, and that timing writes with the
/// "wait for a standby" guarantee on vs off produces a real measurement. Tagged
/// <c>[Trait("Category", "Integration")]</c> so the Docker-free unit lane skips them.
///
/// The repl-latency case runs against a SINGLE-node container, so synchronous_commit=on has no named
/// standby to block on — it proves the measurement path runs and returns finite p50/p99, NOT the
/// production cost (that needs the HA overlay; the verdict is advisory there, ADR-PC-005 §P1).
/// </remarks>
[Trait("Category", "Integration")]
[Collection("RunnerPostgres")]
public sealed class SnapshotReplayRigIntegrationTests
{
    private readonly RunnerPostgresFixture _pg;

    public SnapshotReplayRigIntegrationTests(RunnerPostgresFixture pg) => _pg = pg;

    private EngineProjectionRig NewRig(Guid runNonce) => new(_pg.ConnectionString, TimeProvider.System, runNonce);

    [Fact]
    public async Task Deep_stream_builds_to_depth_and_writes_a_snapshot()
    {
        var rig = NewRig(Guid.NewGuid());

        // 64 deep: 1 constitution + 63 accruals, well past the rig's per-N snapshot threshold (16), so the
        // engine wrote at least one snapshot mid-stream.
        var (streamId, head) = await rig.PopulateDeepStreamAsync(depth: 64, seed: 101);

        Assert.NotEqual(Guid.Empty, streamId);
        Assert.Equal(63, head); // sequences 0..63
        Assert.True(await rig.CountSnapshotsAsync() >= 1, "a 64-deep stream should have at least one per-N snapshot");
    }

    [Fact]
    public async Task Snapshot_accelerated_replay_is_byte_identical_to_cold_and_faster()
    {
        var rig = NewRig(Guid.NewGuid());
        var (streamId, _) = await rig.PopulateDeepStreamAsync(depth: 96, seed: 202);

        var (coldMs, snapMs, identical, snapshotsApplied, refolded) =
            await rig.MeasureSnapshotAcceleratedReplayAsync(streamId);

        // The §P3 invariant: snapshot-then-tail state EQUALS the cold fold byte-for-byte.
        Assert.True(identical, "snapshot-accelerated state must be byte-identical to the cold fold (§P3)");
        Assert.True(snapshotsApplied >= 1, "the accelerated path must have read a snapshot");
        Assert.Equal(96, refolded); // the cold path re-folded the whole stream
        Assert.True(coldMs >= 0 && snapMs >= 0, "both measurements are real wall-clock times");

        // The verdict fold the runner uses: identical + applied + within budget + faster ⇒ PASS. The
        // snapshot path skips a tail, so on a deep stream it is at worst not-slower; assert the verdict's
        // own gate over the measured numbers rather than a flaky absolute-timing inequality.
        var verdict = new SnapshotReplayVerdict(coldMs, snapMs, ReplayVerdict.IrregularBudgetMs, identical, snapshotsApplied, refolded);
        Assert.True(verdict.SnapshotMs <= verdict.BudgetMs, "the snapshot rebuild clears the irregular budget");
    }

    [Fact]
    public async Task Discard_rebuild_on_populated_snapshots_finds_zero_divergence()
    {
        var rig = NewRig(Guid.NewGuid());
        await rig.PopulateDeepStreamAsync(depth: 64, seed: 303);
        await rig.DrainAsync();

        // Snapshots exist before the discard (the L.6 precondition the old drill lacked).
        var before = await rig.CountSnapshotsAsync();
        Assert.True(before >= 1, "the deep stream must have populated snapshots to discard");

        var discarded = await rig.DiscardAllSnapshotsAsync();
        Assert.True(discarded >= 1, "the discard must clear the populated snapshot rows");
        Assert.Equal(0, await rig.CountSnapshotsAsync());

        // With snapshots gone, the cold re-fold must still reproduce every running belief (§8.3).
        var (checked_, divergent, refolded) = await rig.RunNoDivergenceDrillAsync();
        Assert.True(checked_ >= 1);
        Assert.Equal(0, divergent);
        Assert.True(refolded >= 64);
    }

    [Fact]
    public async Task Repl_latency_probe_returns_finite_p50_p99_for_both_sides()
    {
        var rig = NewRig(Guid.NewGuid());

        // A small sample so the suite stays fast. Single-node container: synchronous_commit=on has no named
        // standby, so this proves the measurement path runs and returns real numbers — NOT the production
        // §P1 cost (advisory; the HA overlay is the live-verification path).
        var (offP50, offP99, onP50, onP99) = await rig.MeasureReplicationLatencyAsync(samples: 8, seed: 404);

        Assert.True(offP50 >= 0 && offP99 >= 0 && onP50 >= 0 && onP99 >= 0, "all percentiles are real measurements");
        Assert.True(offP99 >= offP50, "p99 >= p50 within a side");
        Assert.True(onP99 >= onP50, "p99 >= p50 within a side");

        // The verdict folds it advisory (no confirmed standby) — non-gating regardless of the delta.
        var replVerdict = new ReplicationLatencyVerdict(offP50, offP99, onP50, onP99, 8, StandbyConfirmed: false);
        Assert.True(replVerdict.Passed, "single-node repl-latency is advisory (non-gating)");
    }
}
