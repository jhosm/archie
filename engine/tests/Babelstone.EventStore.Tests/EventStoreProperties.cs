using Babelstone.EventStore;
using FsCheck.Xunit;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// Property-based invariants of the engine spine (A.9): per-stream sequence
/// monotonicity, no gaps under concurrent appends, and clean rollback on
/// optimistic-concurrency rejection. FsCheck over real PG18; tagged Integration.
/// MaxTest is bounded because each case is a round-trip to a real database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EventStoreProperties(PostgresEventStoreFixture fixture) : IClassFixture<PostgresEventStoreFixture>
{
    private PostgresEventStore Store => new(fixture.ConnectionString);

    [Property(MaxTest = 25)]
    public async Task Sequential_appends_are_contiguous_and_monotonic(byte seed)
    {
        var count = 1 + (seed % 15); // 1..15 single-event appends
        var streamId = Guid.NewGuid();

        for (long seq = 0; seq < count; seq++)
        {
            var (e, o) = TestData.Pair(streamId, seq);
            await Store.AppendAsync(streamId, expectedVersion: seq - 1, [e], [o]);
        }

        var sequences = await LoadSequencesAsync(streamId);
        Assert.Equal(Enumerable.Range(0, count).Select(i => (long)i), sequences); // 0..count-1, no gaps
    }

    [Property(MaxTest = 15)]
    public async Task Concurrent_appends_to_an_empty_stream_commit_exactly_one(byte seed)
    {
        var writers = 2 + (seed % 7); // 2..8 racers, all claiming expectedVersion -1
        var streamId = Guid.NewGuid();

        var attempts = Enumerable.Range(0, writers).Select(async _ =>
        {
            var (e, o) = TestData.Pair(streamId, 0);
            try
            {
                await Store.AppendAsync(streamId, expectedVersion: -1, [e], [o]);
                return true;
            }
            catch (ConcurrencyException)
            {
                return false; // lost the race — rejected, wrote nothing
            }
        }).ToArray();

        var successes = (await Task.WhenAll(attempts)).Count(won => won);

        Assert.Equal(1, successes);                              // exactly one writer commits
        Assert.Equal([0L], await LoadSequencesAsync(streamId));  // and the stream has no gaps / no double-commit
    }

    [Property(MaxTest = 25)]
    public async Task A_rejected_append_writes_nothing(byte seed)
    {
        var existing = 1 + (seed % 10);
        var streamId = Guid.NewGuid();
        for (long seq = 0; seq < existing; seq++)
        {
            var (e, o) = TestData.Pair(streamId, seq);
            await Store.AppendAsync(streamId, expectedVersion: seq - 1, [e], [o]);
        }

        // Append as if the stream were still empty: stale expectedVersion → rejected.
        var (stale, staleOut) = TestData.Pair(streamId, 0);
        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            Store.AppendAsync(streamId, expectedVersion: -1, [stale], [staleOut]));

        Assert.Equal(existing, (await LoadSequencesAsync(streamId)).Count); // unchanged: rejection rolled back cleanly
    }

    private async Task<List<long>> LoadSequencesAsync(Guid streamId)
    {
        var list = new List<long>();
        await foreach (var envelope in Store.LoadAsync(streamId))
        {
            list.Add(envelope.SequenceNumber);
        }

        return list;
    }
}
