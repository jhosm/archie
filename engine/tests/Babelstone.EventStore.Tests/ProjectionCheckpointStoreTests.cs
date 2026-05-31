using Npgsql;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// Integration tests for <see cref="PostgresProjectionCheckpointStore"/> (migration 0011): the
/// per-(projection_kind, stream_id) high-water marks the async drainer resumes from.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProjectionCheckpointStoreTests(PostgresEventStoreFixture fixture)
    : IClassFixture<PostgresEventStoreFixture>
{
    private const string Kind = "test.position";
    private readonly PostgresProjectionCheckpointStore _store = new(fixture.ConnectionString);

    private async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("TRUNCATE projection_checkpoints;", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Read_is_null_when_absent()
    {
        await ResetAsync();
        Assert.Null(await _store.ReadAsync(Kind, Guid.NewGuid()));
    }

    [Fact]
    public async Task Write_then_read_roundtrips()
    {
        await ResetAsync();
        var streamId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        await _store.WriteAsync(new ProjectionCheckpointRecord(Kind, streamId, 5, at));

        var read = await _store.ReadAsync(Kind, streamId);
        Assert.NotNull(read);
        Assert.Equal(5, read.LastSequenceNumber);
        Assert.Equal(streamId, read.StreamId);
    }

    [Fact]
    public async Task Write_upserts_on_conflict()
    {
        await ResetAsync();
        var streamId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        await _store.WriteAsync(new ProjectionCheckpointRecord(Kind, streamId, 5, at));
        await _store.WriteAsync(new ProjectionCheckpointRecord(Kind, streamId, 9, at.AddMinutes(1)));

        var read = await _store.ReadAsync(Kind, streamId);
        Assert.Equal(9, read!.LastSequenceNumber);
    }

    [Fact]
    public async Task Reset_deletes_every_stream_for_the_kind()
    {
        await ResetAsync();
        var at = DateTimeOffset.UtcNow;
        var streamA = Guid.NewGuid();
        var streamB = Guid.NewGuid();
        await _store.WriteAsync(new ProjectionCheckpointRecord(Kind, streamA, 1, at));
        await _store.WriteAsync(new ProjectionCheckpointRecord(Kind, streamB, 2, at));

        await _store.ResetAsync(Kind);

        Assert.Null(await _store.ReadAsync(Kind, streamA));
        Assert.Null(await _store.ReadAsync(Kind, streamB));
    }
}
