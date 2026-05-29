using Babelstone.EventStore;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// Ordered load / rehydrate (A.3): LoadAsync streams a stream in sequence order, and
/// fromSequence reads only the tail — the seam the snapshot-then-tail caller uses.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LoadIntegrationTests(PostgresEventStoreFixture fixture)
    : IClassFixture<PostgresEventStoreFixture>
{
    private PostgresEventStore Store => new(fixture.ConnectionString);

    [Fact]
    public async Task Loads_events_in_sequence_order()
    {
        var streamId = Guid.NewGuid();
        await AppendRangeAsync(streamId, count: 3);

        var loaded = await ToListAsync(Store.LoadAsync(streamId));

        Assert.Equal([0L, 1L, 2L], loaded.Select(e => e.SequenceNumber));
    }

    [Fact]
    public async Task FromSequence_reads_only_the_tail()
    {
        var streamId = Guid.NewGuid();
        await AppendRangeAsync(streamId, count: 3);

        // A snapshot taken at sequence 1 would rehydrate the tail from sequence 2.
        var tail = await ToListAsync(Store.LoadAsync(streamId, fromSequence: 2));

        Assert.Equal([2L], tail.Select(e => e.SequenceNumber));
    }

    [Fact]
    public async Task Unknown_stream_yields_nothing()
    {
        var loaded = await ToListAsync(Store.LoadAsync(Guid.NewGuid()));
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task Round_trips_the_envelope_fields()
    {
        var streamId = Guid.NewGuid();
        var (appended, outbox) = TestData.Pair(streamId, 0);
        await Store.AppendAsync(streamId, -1, [appended], [outbox]);

        var loaded = Assert.Single(await ToListAsync(Store.LoadAsync(streamId)));

        Assert.Equal(appended.EventId, loaded.EventId);
        Assert.Equal(appended.EventType, loaded.EventType);
        Assert.Equal(appended.PartitionKey, loaded.PartitionKey);
        Assert.Equal(appended.PackVersion, loaded.PackVersion);
        Assert.Equal(appended.ValidTime, loaded.ValidTime);
        Assert.Equal(appended.TransactionTime, loaded.TransactionTime);
        Assert.Null(loaded.CausationId);
        Assert.Equal(appended.PayloadSchemaId, loaded.PayloadSchemaId);
        Assert.Equal(appended.Payload.ToArray(), loaded.Payload.ToArray());
    }

    private async Task AppendRangeAsync(Guid streamId, int count)
    {
        for (long seq = 0; seq < count; seq++)
        {
            var (e, o) = TestData.Pair(streamId, seq);
            await Store.AppendAsync(streamId, expectedVersion: seq - 1, [e], [o]);
        }
    }

    private static async Task<List<EventEnvelope>> ToListAsync(IAsyncEnumerable<EventEnvelope> source)
    {
        var list = new List<EventEnvelope>();
        await foreach (var e in source)
        {
            list.Add(e);
        }

        return list;
    }
}
