using Npgsql;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// Integration tests for <see cref="PostgresProjectionStore"/> against a real PostgreSQL
/// (Testcontainers). Exercises the byte-oriented bitemporal contract (ADR-PC-002 §P1/§P2) after
/// the D.2 migration 0010: the (stream_id, projection_kind) scoping, the atomic
/// supersede-then-insert, the exactly-one-current-belief invariant, the forced-correction
/// round-trip, and the rebuild supersede-all.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresProjectionStoreTests(PostgresEventStoreFixture fixture)
    : IClassFixture<PostgresEventStoreFixture>
{
    private const string Kind = "test.position";
    private readonly PostgresProjectionStore _store = new(fixture.ConnectionString);

    private static ProjectionRecord Sample(
        Guid streamId,
        ReadOnlyMemory<byte> payload,
        DateTimeOffset recordedAt,
        long sourceSequence = 0,
        string kind = Kind) =>
        new(
            StreamId: streamId,
            ProjectionKind: kind,
            SourceSequence: sourceSequence,
            ValidFrom: recordedAt,
            ValidTo: null,
            RecordedAt: recordedAt,
            SupersededAt: null,
            StructuralPayload: payload,
            PiiCiphertext: ReadOnlyMemory<byte>.Empty);

    private async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("TRUNCATE projections;", connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountCurrentBeliefsAsync(Guid streamId, string kind)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM projections WHERE stream_id = @s AND projection_kind = @k AND superseded_at IS NULL;",
            connection);
        command.Parameters.AddWithValue("s", streamId);
        command.Parameters.AddWithValue("k", kind);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Write_then_read_returns_current_belief()
    {
        await ResetAsync();
        var streamId = Guid.NewGuid();
        var payload = new byte[] { 1, 2, 3 };
        var recordedAt = DateTimeOffset.UtcNow;

        await _store.WriteAsync(Sample(streamId, payload, recordedAt, sourceSequence: 7));

        var current = await _store.ReadCurrentBeliefAsync(streamId, Kind);
        Assert.NotNull(current);
        Assert.Equal(payload, current.StructuralPayload.ToArray());
        Assert.Equal(7, current.SourceSequence);
        Assert.Equal(Kind, current.ProjectionKind);
    }

    [Fact]
    public async Task ReadCurrentBelief_is_null_when_absent()
    {
        await ResetAsync();
        Assert.Null(await _store.ReadCurrentBeliefAsync(Guid.NewGuid(), Kind));
    }

    [Fact]
    public async Task ReadCurrentBelief_scopes_to_projection_kind()
    {
        await ResetAsync();
        var streamId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await _store.WriteAsync(Sample(streamId, new byte[] { 0xAA }, now, kind: "test.position"));
        await _store.WriteAsync(Sample(streamId, new byte[] { 0xBB }, now, kind: "test.ledger"));

        var position = await _store.ReadCurrentBeliefAsync(streamId, "test.position");
        var ledger = await _store.ReadCurrentBeliefAsync(streamId, "test.ledger");
        Assert.Equal(new byte[] { 0xAA }, position!.StructuralPayload.ToArray());
        Assert.Equal(new byte[] { 0xBB }, ledger!.StructuralPayload.ToArray());
    }

    [Fact]
    public async Task SupersedeAndWrite_keeps_exactly_one_current_belief_and_advances_it()
    {
        await ResetAsync();
        var streamId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        var t1 = t0.AddMinutes(1);

        await _store.SupersedeAndWriteAsync(Sample(streamId, new byte[] { 1 }, t0, sourceSequence: 0));
        await _store.SupersedeAndWriteAsync(Sample(streamId, new byte[] { 2 }, t1, sourceSequence: 1));

        Assert.Equal(1, await CountCurrentBeliefsAsync(streamId, Kind));
        var current = await _store.ReadCurrentBeliefAsync(streamId, Kind);
        Assert.Equal(new byte[] { 2 }, current!.StructuralPayload.ToArray());
        Assert.Equal(1, current.SourceSequence);
    }

    [Fact]
    public async Task ForcedCorrection_roundtrip_preserves_both_beliefs()
    {
        await ResetAsync();
        var streamId = Guid.NewGuid();
        var recordedThen = DateTimeOffset.UtcNow;
        var correctedAt = recordedThen.AddHours(1);

        // What we knew then.
        await _store.WriteAsync(Sample(streamId, new byte[] { 0x10 }, recordedThen, sourceSequence: 3));
        // A forced correction: supersede the prior belief, insert what we now know.
        await _store.SupersedeAsync(streamId, Kind, correctedAt);
        await _store.WriteAsync(Sample(streamId, new byte[] { 0x20 }, correctedAt, sourceSequence: 3));

        // Current belief is the correction; exactly one current row.
        var current = await _store.ReadCurrentBeliefAsync(streamId, Kind);
        Assert.Equal(new byte[] { 0x20 }, current!.StructuralPayload.ToArray());
        Assert.Equal(1, await CountCurrentBeliefsAsync(streamId, Kind));

        // The superseded original is still on the table (belief history, never deleted).
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM projections WHERE stream_id = @s AND superseded_at IS NOT NULL;", connection);
        command.Parameters.AddWithValue("s", streamId);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task SupersedeAll_closes_every_stream_for_the_kind()
    {
        await ResetAsync();
        var streamA = Guid.NewGuid();
        var streamB = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await _store.SupersedeAndWriteAsync(Sample(streamA, new byte[] { 1 }, now));
        await _store.SupersedeAndWriteAsync(Sample(streamB, new byte[] { 2 }, now));

        await _store.SupersedeAllAsync(Kind, now.AddMinutes(5));

        Assert.Equal(0, await CountCurrentBeliefsAsync(streamA, Kind));
        Assert.Equal(0, await CountCurrentBeliefsAsync(streamB, Kind));
    }

    [Fact]
    public async Task UniqueIndex_blocks_a_second_current_belief_for_the_same_pair()
    {
        await ResetAsync();
        var streamId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // A bare WriteAsync without a preceding supersede leaves the prior belief current; the
        // partial UNIQUE index (migration 0010) makes the second current row fail loud.
        await _store.WriteAsync(Sample(streamId, new byte[] { 1 }, now));
        await Assert.ThrowsAsync<PostgresException>(
            () => _store.WriteAsync(Sample(streamId, new byte[] { 2 }, now.AddMinutes(1))));
    }
}
