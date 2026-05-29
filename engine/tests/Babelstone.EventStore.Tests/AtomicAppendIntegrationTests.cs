using Babelstone.EventStore;
using Npgsql;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// The ES_ATOMIC_APPEND_OUTBOX fitness function (ADR-PC-001 §P2 / ADR-IC-004 §P6)
/// plus optimistic concurrency (§P4). Real PostgreSQL; tagged Integration.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AtomicAppendIntegrationTests(PostgresEventStoreFixture fixture)
    : IClassFixture<PostgresEventStoreFixture>
{
    private string ConnectionString => fixture.ConnectionString;

    [Fact]
    public async Task Append_commits_events_and_outbox_in_both_tables()
    {
        var store = new PostgresEventStore(ConnectionString);
        var streamId = Guid.NewGuid();
        var (e0, o0) = TestData.Pair(streamId, 0);
        var (e1, o1) = TestData.Pair(streamId, 1);

        await store.AppendAsync(streamId, expectedVersion: -1, [e0, e1], [o0, o1]);

        Assert.Equal(2L, await CountAsync("events", streamId));
        Assert.Equal(2L, await CountAsync("outbox", streamId, idColumn: "aggregate_id"));
    }

    [Fact]
    public async Task Failed_outbox_insert_rolls_back_the_events_in_the_same_transaction()
    {
        var store = new PostgresEventStore(ConnectionString);
        var streamId = Guid.NewGuid();
        var (e0, o0) = TestData.Pair(streamId, 0);
        var (e1, _) = TestData.Pair(streamId, 1);

        // Two outbox rows sharing one event_id collide on outbox's PK *after* the
        // events have been inserted in the same transaction. Atomicity means the
        // events must not survive the rollback.
        await Assert.ThrowsAsync<PostgresException>(() =>
            store.AppendAsync(streamId, expectedVersion: -1, [e0, e1], [o0, o0]));

        Assert.Equal(0L, await CountAsync("events", streamId));
        Assert.Equal(0L, await CountAsync("outbox", streamId, idColumn: "aggregate_id"));
    }

    [Fact]
    public async Task Stale_expected_version_is_rejected_and_writes_nothing()
    {
        var store = new PostgresEventStore(ConnectionString);
        var streamId = Guid.NewGuid();
        var (e0, o0) = TestData.Pair(streamId, 0);
        await store.AppendAsync(streamId, expectedVersion: -1, [e0], [o0]);

        // The head is now 0; appending again as if the stream were empty must fail.
        var (eStale, oStale) = TestData.Pair(streamId, 0);
        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() =>
            store.AppendAsync(streamId, expectedVersion: -1, [eStale], [oStale]));

        Assert.Equal(-1, ex.ExpectedVersion);
        Assert.Equal(0, ex.ActualVersion);
        Assert.Equal(1L, await CountAsync("events", streamId)); // only the first append survives
    }

    [Fact]
    public async Task Sequential_appends_advance_the_head()
    {
        var store = new PostgresEventStore(ConnectionString);
        var streamId = Guid.NewGuid();
        var (e0, o0) = TestData.Pair(streamId, 0);
        var (e1, o1) = TestData.Pair(streamId, 1);

        await store.AppendAsync(streamId, expectedVersion: -1, [e0], [o0]);
        await store.AppendAsync(streamId, expectedVersion: 0, [e1], [o1]);

        Assert.Equal(2L, await CountAsync("events", streamId));
    }

    [Fact]
    public async Task Conflicting_appends_on_a_non_empty_stream_reject_the_loser()
    {
        var store = new PostgresEventStore(ConnectionString);
        var streamId = Guid.NewGuid();

        // Build the stream up to head = 1 (two events).
        var (e0, o0) = TestData.Pair(streamId, 0);
        var (e1, o1) = TestData.Pair(streamId, 1);
        await store.AppendAsync(streamId, expectedVersion: -1, [e0], [o0]);
        await store.AppendAsync(streamId, expectedVersion: 0, [e1], [o1]);

        // Two appenders both believe the head is 1. The winner advances it to 2; the
        // loser is a stale-version conflict and must write nothing. This is the realistic
        // production race — on a NON-empty stream, not the empty-stream case the other
        // concurrency tests cover (review finding I4).
        var (winner, winnerOut) = TestData.Pair(streamId, 2);
        var (loser, loserOut) = TestData.Pair(streamId, 2);
        await store.AppendAsync(streamId, expectedVersion: 1, [winner], [winnerOut]);

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() =>
            store.AppendAsync(streamId, expectedVersion: 1, [loser], [loserOut]));
        Assert.Equal(1, ex.ExpectedVersion);
        Assert.Equal(2, ex.ActualVersion);
        Assert.Equal(3L, await CountAsync("events", streamId)); // only the winner's third event landed
    }

    [Fact]
    public async Task Outbox_rows_from_one_multi_event_append_drain_in_sequence_order()
    {
        var store = new PostgresEventStore(ConnectionString);
        var streamId = Guid.NewGuid();

        // One append emits three events for one aggregate. They share a single created_at,
        // so the publisher's drain tiebreaker must be sequence_number, not the random
        // event_id (ADR-IC-004 §P2, amended 2026-05-29). Assert the drain order is the
        // sequence order — the property that would silently fail with the old event_id
        // tiebreaker (review finding S1).
        var (e0, o0) = TestData.Pair(streamId, 0);
        var (e1, o1) = TestData.Pair(streamId, 1);
        var (e2, o2) = TestData.Pair(streamId, 2);
        await store.AppendAsync(streamId, expectedVersion: -1, [e0, e1, e2], [o0, o1, o2]);

        Assert.Equal([0L, 1L, 2L], await DrainOrderAsync(streamId));
    }

    private async Task<List<long>> DrainOrderAsync(Guid aggregateId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        // Mirror the publisher's poll (ADR-IC-004 §P2, amended): per-aggregate FIFO order.
        await using var command = new NpgsqlCommand(
            "SELECT sequence_number FROM outbox WHERE aggregate_id = @id AND status = 'PENDING' ORDER BY created_at, sequence_number;",
            connection);
        command.Parameters.AddWithValue("id", aggregateId);
        var order = new List<long>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            order.Add(reader.GetInt64(0));
        }

        return order;
    }

    private async Task<long> CountAsync(string table, Guid streamId, string idColumn = "stream_id")
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {table} WHERE {idColumn} = @id;", connection);
        command.Parameters.AddWithValue("id", streamId);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
