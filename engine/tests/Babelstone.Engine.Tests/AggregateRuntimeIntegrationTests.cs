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
    public async Task Load_as_of_sequence_folds_to_the_historical_state_at_a_point_not_current()
    {
        // The as-of / point-in-time fold (bd babelstone-b4wp): folding UP TO an inclusive sequence
        // returns the historical state at that point, never the current head. The family-agnostic
        // CounterFamily proves the KERNEL mechanism (no term-deposit specifics): 10 → 15 → 0 (Reset).
        var runtime = fixture.DurableRuntime();
        var streamId = Guid.NewGuid();
        await runtime.AppendAsync(
            streamId, -1, [new Incremented(10), new Incremented(5), new Reset()], fixture.Context());

        // As of sequence 0: only the first event folded — Total == 10.
        var atZero = await runtime.LoadAsOfSequenceAsync(streamId, 0);
        Assert.Equal(10, atZero.State.Total);
        Assert.Equal(0, atZero.Version);

        // As of sequence 1: the first two — Total == 15 (the Reset at sequence 2 is the future).
        var atOne = await runtime.LoadAsOfSequenceAsync(streamId, 1);
        Assert.Equal(15, atOne.State.Total);
        Assert.Equal(1, atOne.Version);

        // The current head (no upper bound) folds the Reset too — Total == 0, proving as-of read a
        // DIFFERENT point than "now".
        var head = await runtime.LoadAsync(streamId);
        Assert.Equal(0, head.State.Total);
        Assert.Equal(2, head.Version);
    }

    [Fact]
    public async Task Load_as_of_sequence_is_deterministic_and_reports_real_head_when_point_is_beyond_it()
    {
        var runtime = fixture.DurableRuntime();
        var streamId = Guid.NewGuid();
        await runtime.AppendAsync(streamId, -1, [new Incremented(2), new Incremented(3)], fixture.Context());

        // Repeated as-of reads at the same point return identical state (pure fold, no clock).
        var first = await runtime.LoadAsOfSequenceAsync(streamId, 1);
        var second = await runtime.LoadAsOfSequenceAsync(streamId, 1);
        Assert.Equal(first.State, second.State);

        // A point BEYOND the head folds to the real head (Version 1 < 999) — the method never throws;
        // the boundary (HTTP) reads Version < asOfSequence to reject the future point as a clean 4xx.
        var beyond = await runtime.LoadAsOfSequenceAsync(streamId, 999);
        Assert.Equal(1, beyond.Version);
        Assert.Equal(5, beyond.State.Total);

        // An unknown stream folds to Version -1 (the unknown-stream verdict the boundary maps to 404).
        var unknown = await runtime.LoadAsOfSequenceAsync(Guid.NewGuid(), 0);
        Assert.Equal(-1, unknown.Version);
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
