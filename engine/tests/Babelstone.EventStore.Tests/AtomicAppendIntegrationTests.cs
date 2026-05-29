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
