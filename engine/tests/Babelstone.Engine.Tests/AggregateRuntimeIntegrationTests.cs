using Babelstone.Engine;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// The durable runtime end-to-end (A.6): append → reload round-trips folded state,
/// snapshot-then-tail rehydration matches a cold fold, and replay is deterministic.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AggregateRuntimeIntegrationTests(EngineFixture fixture) : IClassFixture<EngineFixture>
{
    [Fact]
    public async Task Append_then_load_round_trips_folded_state()
    {
        var runtime = fixture.DurableRuntime();
        var streamId = Guid.NewGuid();

        await runtime.AppendAsync(streamId, -1, [new Incremented(10), new Incremented(5)], fixture.Context());
        var hydrated = await runtime.LoadAsync(streamId);

        Assert.Equal(15, hydrated.State.Total);
        Assert.Equal(1, hydrated.Version); // sequences 0 and 1
    }

    [Fact]
    public async Task Append_respects_optimistic_concurrency_through_the_runtime()
    {
        var runtime = fixture.DurableRuntime();
        var streamId = Guid.NewGuid();
        await runtime.AppendAsync(streamId, -1, [new Incremented(1)], fixture.Context());

        // Appending again as if the stream were empty conflicts at the store.
        await Assert.ThrowsAsync<EventStore.ConcurrencyException>(() =>
            runtime.AppendAsync(streamId, -1, [new Incremented(1)], fixture.Context()));
    }

    [Fact]
    public async Task Snapshot_then_tail_load_matches_a_cold_fold()
    {
        var streamId = Guid.NewGuid();
        var snapshotting = fixture.DurableRuntime(withSnapshots: true);

        // Append a few events, snapshot the state at the head, then append more.
        await snapshotting.AppendAsync(streamId, -1, [new Incremented(3), new Incremented(4)], fixture.Context());
        var atSnapshot = await snapshotting.LoadAsync(streamId);
        await new SnapshotStore<CounterState>(fixture.SnapshotStorage, new JsonStateSerializer<CounterState>())
            .PutAsync(streamId, atSnapshot.Version, atSnapshot.LastEventId!.Value, atSnapshot.State,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await snapshotting.AppendAsync(streamId, atSnapshot.Version, [new Incremented(5)], fixture.Context());

        // Snapshot-then-tail (uses the snapshot + folds only sequence 2) must equal the cold fold.
        var viaSnapshot = await snapshotting.LoadAsync(streamId);
        var coldFold = await fixture.DurableRuntime().LoadAsync(streamId);

        Assert.Equal(12, viaSnapshot.State.Total);
        Assert.Equal(coldFold.State, viaSnapshot.State);
    }

    [Fact]
    public async Task Cold_replay_is_deterministic()
    {
        var streamId = Guid.NewGuid();
        await fixture.DurableRuntime().AppendAsync(
            streamId, -1, [new Incremented(7), new Reset(), new Incremented(2)], fixture.Context());

        var first = await fixture.DurableRuntime().LoadAsync(streamId);
        var second = await fixture.DurableRuntime().LoadAsync(streamId);

        Assert.Equal(2, first.State.Total);
        Assert.Equal(first.State, second.State);
    }
}
