using Babelstone.EventStore;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// A.11 snapshot write-side wiring (bd babelstone-e6fr.11 / ADR-PC-003 §P2). The runtime, once
/// handed a snapshot store + a per-N policy, must actually WRITE a snapshot in the post-commit path
/// when the policy fires — and the snapshot it writes must keep snapshot-then-tail rehydration
/// byte-identical to a cold fold (the invariant that lets snapshots be a pure optimisation,
/// event-store §8 / §10.5). This is the missing write side; <see cref="SnapshotEquivalenceProperties"/>
/// already proves the read side's equivalence over a manually-placed snapshot. Real PG18.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SnapshotWriteSideTests(EngineFixture fixture) : IClassFixture<EngineFixture>
{
    [Fact]
    public async Task Policy_fires_so_the_runtime_writes_a_snapshot_in_the_post_commit_path()
    {
        // every-2-events policy: appending two events crosses the threshold (events-since-snapshot == 2),
        // so the post-commit path must write a snapshot at the head.
        var runtime = fixture.SnapshottingRuntime(everyNEvents: 2);
        var streamId = Guid.NewGuid();

        await runtime.AppendAsync(streamId, -1, [new Incremented(3), new Incremented(4)], fixture.Context());

        // The snapshot was written by the runtime itself — no manual PutAsync. It covers the head
        // (sequence 1) and carries the folded total (7).
        var written = await new SnapshotStore<CounterState>(
            fixture.SnapshotStorage, new JsonStateSerializer<CounterState>()).TryGetAsync(streamId);
        Assert.NotNull(written);
        Assert.Equal(1, written.AtSequence);
        Assert.Equal(7, written.State.Total);
    }

    [Fact]
    public async Task Below_threshold_no_snapshot_is_written()
    {
        // every-100 policy: a single 1-event stream is well under the threshold, so NO snapshot is taken
        // (the cold fold remains the only path) — the per-N trigger must not fire spuriously.
        var runtime = fixture.SnapshottingRuntime(everyNEvents: 100);
        var streamId = Guid.NewGuid();

        await runtime.AppendAsync(streamId, -1, [new Incremented(5)], fixture.Context());

        var written = await new SnapshotStore<CounterState>(
            fixture.SnapshotStorage, new JsonStateSerializer<CounterState>()).TryGetAsync(streamId);
        Assert.Null(written);
    }

    [Fact]
    public async Task Snapshot_accelerated_load_equals_full_replay_for_a_multi_event_aggregate()
    {
        // The core event-sourcing discipline (task brief): loading from a runtime-WRITTEN snapshot plus
        // replaying the tail must produce state byte-identical to a full from-zero replay. Drive a
        // multi-event aggregate through a low threshold so the runtime snapshots mid-stream, append a
        // tail past the snapshot, then compare snapshot-then-tail against a cold fold on a fresh runtime.
        var snapshotting = fixture.SnapshottingRuntime(everyNEvents: 3);
        var streamId = Guid.NewGuid();

        // Sequences 0..2 cross the every-3 threshold → a snapshot is written at sequence 2 (total 12).
        await snapshotting.AppendAsync(
            streamId, -1, [new Incremented(2), new Incremented(4), new Incremented(6)], fixture.Context());
        var afterSnapshot = await new SnapshotStore<CounterState>(
            fixture.SnapshotStorage, new JsonStateSerializer<CounterState>()).TryGetAsync(streamId);
        Assert.NotNull(afterSnapshot);
        Assert.Equal(2, afterSnapshot.AtSequence);

        // A tail past the snapshot point (sequences 3..4): a Reset then an Incremented, so the snapshot's
        // state is NOT just carried forward — the tail must actually fold on top of it.
        await snapshotting.AppendAsync(streamId, 2, [new Reset(), new Incremented(9)], fixture.Context());

        // Snapshot-accelerated load (uses the written snapshot + folds only the tail [3, 4]).
        var viaSnapshot = await snapshotting.LoadAsync(streamId);

        // A cold fold on a runtime with NO snapshots: folds every event from sequence 0. This is the
        // full from-zero replay the snapshot path must equal.
        var coldFold = await fixture.DurableRuntime().LoadAsync(streamId);

        Assert.Equal(9, viaSnapshot.State.Total);      // Reset → 0, then +9
        Assert.Equal(coldFold.State, viaSnapshot.State); // byte-identical state (record value equality)
        Assert.Equal(coldFold.Version, viaSnapshot.Version);
    }

    [Fact]
    public async Task Snapshot_write_failure_is_fail_soft_the_committed_event_survives()
    {
        // ADR-PC-003 §P2: the snapshot write is eventually-consistent, NOT transactional with the append.
        // "If it fails the engine continues; the next rebuild is merely slower, never wrong." A storage
        // whose PutAsync always throws must NOT fail the append — the exception is surfaced via the
        // fail-soft sink, and the committed event is still readable (a cold fold reconstructs it).
        Exception? surfaced = null;
        var runtime = fixture.SnapshottingRuntime(
            everyNEvents: 1, onSnapshotError: ex => surfaced = ex, storage: new ThrowingSnapshotStorage());
        var streamId = Guid.NewGuid();

        // The append must SUCCEED and return the new head despite the snapshot write blowing up.
        var head = await runtime.AppendAsync(streamId, -1, [new Incremented(11)], fixture.Context());
        Assert.Equal(0, head);

        // The fail-soft sink saw the snapshot-write failure (so the host could log / alarm on it).
        Assert.NotNull(surfaced);

        // The committed event is the book of record — a cold fold (no snapshot) reconstructs it intact.
        var coldFold = await fixture.DurableRuntime().LoadAsync(streamId);
        Assert.Equal(11, coldFold.State.Total);
    }

    /// <summary>A snapshot storage that always throws on write — exercises the fail-soft post-commit path.</summary>
    private sealed class ThrowingSnapshotStorage : ISnapshotStorage
    {
        public Task<SnapshotRecord?> TryGetLatestAsync(Guid streamId, CancellationToken ct = default)
            => Task.FromResult<SnapshotRecord?>(null);

        public Task<SnapshotRecord?> TryGetAtOrBeforeAsync(
            Guid streamId, long atOrBeforeSequence, CancellationToken ct = default)
            => Task.FromResult<SnapshotRecord?>(null);

        public Task PutAsync(SnapshotRecord snapshot, CancellationToken ct = default)
            => throw new InvalidOperationException("snapshot store unavailable");

        public Task<int> DiscardAsync(Guid streamId, CancellationToken ct = default)
            => Task.FromResult(0);
    }
}
