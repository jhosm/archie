using Babelstone.Engine;
using FsCheck.Xunit;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// A.9 spine property: snapshot-then-tail rehydration is observationally identical to a
/// cold fold for any event sequence and any snapshot point — the invariant that lets
/// snapshots be a pure optimisation (feature-design event-store §8 / §10.5). Real PG18.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SnapshotEquivalenceProperties(EngineFixture fixture) : IClassFixture<EngineFixture>
{
    private static readonly DateTimeOffset SnapshotTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Property(MaxTest = 30)]
    public void Snapshot_then_tail_equals_cold_fold(byte[] increments, byte splitSeed)
    {
        var amounts = (increments.Length >= 1 ? increments : [1]).Select(b => 1 + (b % 9)).ToArray();
        var head = amounts.Length;
        // Generated split over the full range [0, head] so the boundaries the old fixed
        // head/2 never reached are exercised: split == 0 (no snapshot — whole stream is
        // tail) and split == head (snapshot at head — empty tail). Review finding I4/#5.
        var split = splitSeed % (head + 1);

        var streamId = Guid.NewGuid();
        var runtime = fixture.DurableRuntime(withSnapshots: true);
        var snapshotStore = new SnapshotStore<CounterState>(fixture.SnapshotStorage, new JsonStateSerializer<CounterState>());

        // 1. Append the head events, 2. snapshot the state there (if any), 3. append the tail.
        for (var i = 0; i < split; i++)
        {
            runtime.AppendAsync(streamId, i - 1, [new Incremented(amounts[i])], fixture.Context()).GetAwaiter().GetResult();
        }

        if (split > 0)
        {
            // split == 0 leaves the stream empty (Version -1, no LastEventId): there is
            // nothing to snapshot, so the snapshot-then-tail path is just a cold fold.
            var atSplit = runtime.LoadAsync(streamId).GetAwaiter().GetResult();
            snapshotStore.PutAsync(streamId, atSplit.Version, atSplit.LastEventId!.Value, atSplit.State, SnapshotTime)
                .GetAwaiter().GetResult();
        }

        for (var i = split; i < head; i++)
        {
            runtime.AppendAsync(streamId, i - 1, [new Incremented(amounts[i])], fixture.Context()).GetAwaiter().GetResult();
        }

        // Snapshot-then-tail (snapshot + folds only [split, head)) must equal the cold fold.
        var viaSnapshot = runtime.LoadAsync(streamId).GetAwaiter().GetResult();
        var coldFold = fixture.DurableRuntime().LoadAsync(streamId).GetAwaiter().GetResult();

        Assert.Equal(amounts.Sum(), viaSnapshot.State.Total);
        Assert.Equal(coldFold.State, viaSnapshot.State);
    }
}
