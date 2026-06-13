using Babelstone.EventStore;
using Npgsql;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// The ENGINE_COMMAND_IDEMPOTENT fitness function (ADR-PC-029 slot 4) at the storage seam: a
/// replayed command id returns the original <c>commit_sequence</c> with no second append. The
/// in-transaction <c>command_dedup</c> ledger (migration 0015) is the crash-atomic backstop
/// behind the endpoint pre-check — its job is the dangerous case the pre-check can race past: a
/// duplicate command id that targets a DIFFERENT (server-generated) stream must still lose on the
/// command id before it can open a second stream. (The common sequential retry is short-circuited
/// at the endpoint by <see cref="PostgresCommandLog"/>; the end-to-end replay is covered by the
/// host test <c>DepositsApiIntegrationTests</c>.) Real PostgreSQL; tagged Integration.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CommandIdempotencyIntegrationTests(PostgresEventStoreFixture fixture)
    : IClassFixture<PostgresEventStoreFixture>
{
    private string ConnectionString => fixture.ConnectionString;

    [Fact]
    public async Task ENGINE_COMMAND_IDEMPOTENT_a_duplicate_command_id_cannot_open_a_second_stream_and_returns_the_original_head()
    {
        var store = new PostgresEventStore(ConnectionString);
        var commandId = Guid.NewGuid();
        var streamA = Guid.NewGuid();
        var streamB = Guid.NewGuid();
        var (a0, ao0) = TestData.Pair(streamA, 0);
        var (b0, bo0) = TestData.Pair(streamB, 0);

        // First apply: command lands on streamA, head 0, receipt written in the same transaction.
        await store.AppendAsync(streamA, expectedVersion: -1, [a0], [ao0], commandId);

        // Replay the SAME command id against a DIFFERENT fresh stream (the constitution case where
        // the deposit id is a server-generated OUTPUT, so a retry can carry a new one). streamB's
        // own head check passes (it is empty), but the command_dedup INSERT collides first — so no
        // second stream is opened, and the caller is handed back the ORIGINAL outcome.
        var dup = await Assert.ThrowsAsync<DuplicateCommandException>(() =>
            store.AppendAsync(streamB, expectedVersion: -1, [b0], [bo0], commandId));

        Assert.Equal(commandId, dup.CommandId);
        Assert.Equal(streamA, dup.StreamId);             // the ORIGINAL stream, not streamB
        Assert.Equal(0, dup.CommitSequence);             // the ORIGINAL head
        Assert.Equal(1L, await CountEventsAsync(streamA)); // first apply intact
        Assert.Equal(0L, await CountEventsAsync(streamB)); // NO second append
    }

    [Fact]
    public async Task ENGINE_COMMAND_IDEMPOTENT_replay_reports_the_multi_event_appends_head_not_zero()
    {
        var store = new PostgresEventStore(ConnectionString);
        var commandId = Guid.NewGuid();
        var streamA = Guid.NewGuid();
        var streamB = Guid.NewGuid();
        var (a0, ao0) = TestData.Pair(streamA, 0);
        var (a1, ao1) = TestData.Pair(streamA, 1);
        var (b0, bo0) = TestData.Pair(streamB, 0);

        // One command appends a multi-event batch (head reaches 1) — the receipt records that head,
        // not a hard-coded 0, so a replay returns the real read-your-writes token.
        await store.AppendAsync(streamA, expectedVersion: -1, [a0, a1], [ao0, ao1], commandId);

        var dup = await Assert.ThrowsAsync<DuplicateCommandException>(() =>
            store.AppendAsync(streamB, expectedVersion: -1, [b0], [bo0], commandId));

        Assert.Equal(1, dup.CommitSequence);
        Assert.Equal(0L, await CountEventsAsync(streamB));
    }

    [Fact]
    public async Task A_distinct_command_id_on_a_fresh_stream_appends_normally()
    {
        var store = new PostgresEventStore(ConnectionString);
        var streamA = Guid.NewGuid();
        var streamB = Guid.NewGuid();
        var (a0, ao0) = TestData.Pair(streamA, 0);
        var (b0, bo0) = TestData.Pair(streamB, 0);

        // Two DIFFERENT command ids are not duplicates — both apply.
        await store.AppendAsync(streamA, expectedVersion: -1, [a0], [ao0], Guid.NewGuid());
        await store.AppendAsync(streamB, expectedVersion: -1, [b0], [bo0], Guid.NewGuid());

        Assert.Equal(1L, await CountEventsAsync(streamA));
        Assert.Equal(1L, await CountEventsAsync(streamB));
    }

    [Fact]
    public async Task The_command_log_reads_back_the_receipt_after_an_idempotent_append()
    {
        var store = new PostgresEventStore(ConnectionString);
        var log = new PostgresCommandLog(ConnectionString);
        var commandId = Guid.NewGuid();
        var streamId = Guid.NewGuid();
        var (e0, o0) = TestData.Pair(streamId, 0);

        Assert.Null(await log.TryGetAsync(commandId)); // unknown before the append

        await store.AppendAsync(streamId, expectedVersion: -1, [e0], [o0], commandId);

        var receipt = await log.TryGetAsync(commandId);
        Assert.NotNull(receipt);
        Assert.Equal(commandId, receipt.CommandId);
        Assert.Equal(streamId, receipt.StreamId);
        Assert.Equal(0, receipt.CommitSequence);
    }

    private async Task<long> CountEventsAsync(Guid streamId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM events WHERE stream_id = @id;", connection);
        command.Parameters.AddWithValue("id", streamId);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
