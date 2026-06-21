using Babelstone.EventStore;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// Snapshot storage (A.4): round-trip, latest-wins, advisory-until-trusted promotion,
/// and the discard primitive the monthly drill is built on. Real PG18; Integration.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SnapshotStoreIntegrationTests(PostgresEventStoreFixture fixture)
    : IClassFixture<PostgresEventStoreFixture>
{
    private PostgresSnapshotStore Store => new(fixture.ConnectionString);

    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset TransactionTime = new(2025, 12, 31, 23, 0, 0, TimeSpan.Zero);

    private static SnapshotRecord Record(Guid streamId, long atSequence, bool trusted = false)
    {
        var lastEventId = Guid.NewGuid();
        var state = new byte[] { 0xAA, 0xBB };
        return new SnapshotRecord(
            StreamId: streamId,
            AtSequence: atSequence,
            LastEventId: lastEventId,
            StateHash: SnapshotHash.Compute(state, lastEventId),
            State: state,
            Trusted: trusted,
            CreatedAt: CreatedAt,
            // Deliberately DISTINCT from CreatedAt: the event-derived head transaction_time (0017) is a
            // different instant from when the row was written, so the round-trip proves they are stored
            // and read back as separate columns, not conflated.
            TransactionTime: TransactionTime);
    }

    [Fact]
    public async Task Round_trips_a_snapshot()
    {
        var streamId = Guid.NewGuid();
        var snapshot = Record(streamId, atSequence: 100);
        await Store.PutAsync(snapshot);

        var loaded = await Store.TryGetLatestAsync(streamId);

        Assert.NotNull(loaded);
        Assert.Equal(snapshot.AtSequence, loaded.AtSequence);
        Assert.Equal(snapshot.LastEventId, loaded.LastEventId);
        Assert.Equal(snapshot.StateHash, loaded.StateHash);
        Assert.Equal(snapshot.State.ToArray(), loaded.State.ToArray());
        Assert.False(loaded.Trusted); // advisory by default
        Assert.Equal(snapshot.CreatedAt, loaded.CreatedAt);
        Assert.Equal(snapshot.TransactionTime, loaded.TransactionTime); // the 0017 column round-trips distinctly
    }

    [Fact]
    public async Task TryGetLatest_returns_the_highest_sequence()
    {
        var streamId = Guid.NewGuid();
        await Store.PutAsync(Record(streamId, atSequence: 100));
        await Store.PutAsync(Record(streamId, atSequence: 250));
        await Store.PutAsync(Record(streamId, atSequence: 175));

        var loaded = await Store.TryGetLatestAsync(streamId);

        Assert.Equal(250, loaded!.AtSequence);
    }

    [Fact]
    public async Task TryGetAtOrBefore_returns_the_highest_snapshot_not_past_the_point()
    {
        // The §P1 readLatestSnapshot(..., atOrBeforeSequence) the as-of replay needs: of the snapshots
        // at 100/175/250, a read at-or-before 200 returns 175 (the highest NOT past the point), and a
        // read at-or-before 99 returns null (every snapshot is the future relative to that point).
        var streamId = Guid.NewGuid();
        await Store.PutAsync(Record(streamId, atSequence: 100));
        await Store.PutAsync(Record(streamId, atSequence: 250));
        await Store.PutAsync(Record(streamId, atSequence: 175));

        Assert.Equal(175, (await Store.TryGetAtOrBeforeAsync(streamId, 200))!.AtSequence);
        Assert.Equal(175, (await Store.TryGetAtOrBeforeAsync(streamId, 175))!.AtSequence); // inclusive
        Assert.Equal(100, (await Store.TryGetAtOrBeforeAsync(streamId, 100))!.AtSequence);
        Assert.Null(await Store.TryGetAtOrBeforeAsync(streamId, 99));                      // all in the future
    }

    [Fact]
    public async Task Re_putting_a_sequence_promotes_it_to_trusted()
    {
        var streamId = Guid.NewGuid();
        await Store.PutAsync(Record(streamId, atSequence: 100, trusted: false));
        await Store.PutAsync(Record(streamId, atSequence: 100, trusted: true));

        var loaded = await Store.TryGetLatestAsync(streamId);

        Assert.True(loaded!.Trusted);
    }

    [Fact]
    public async Task Discard_removes_all_snapshots_for_the_stream()
    {
        var streamId = Guid.NewGuid();
        await Store.PutAsync(Record(streamId, atSequence: 100));
        await Store.PutAsync(Record(streamId, atSequence: 200));

        var removed = await Store.DiscardAsync(streamId);

        Assert.Equal(2, removed);
        Assert.Null(await Store.TryGetLatestAsync(streamId));
    }

    [Fact]
    public async Task Unknown_stream_has_no_snapshot()
    {
        Assert.Null(await Store.TryGetLatestAsync(Guid.NewGuid()));
    }
}
