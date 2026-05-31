using Babelstone.EventStore;
using Babelstone.EventStore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// Path-A bitemporal projection storage against real PostgreSQL (ADR-PC-002 §P1/§P2).
/// Tagged Integration so the default (Docker-free) engine CI job skips it; the
/// integration lane runs it (ADR-IC-009). A dedicated container (not the shared
/// fixture) is used so the 0005 migration and the babelstone_engine UPDATE-grant
/// assertions stand on a clean, fully-migrated database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresProjectionStoreTests : IAsyncLifetime
{
    // Match the dev stack's pinned major version (the other integration tests use the
    // same image) so the schema and §P3-style privilege behaviour are tested against
    // the PostgreSQL the engine actually deploys on.
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine")
        .Build();

    private string ConnectionString => _pg.GetConnectionString();

    private PostgresProjectionStore Store => new(ConnectionString);

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await new MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    private static readonly DateTimeOffset ValidFrom = new(2026, 5, 31, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RecordedAtV1 = new(2026, 5, 31, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RecordedAtV2 = new(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);

    private static ProjectionRecord Record(
        Guid streamId,
        byte[] structural,
        DateTimeOffset recordedAt,
        DateTimeOffset? validTo = null,
        byte[]? piiCiphertext = null) =>
        new(
            StreamId: streamId,
            ValidFrom: ValidFrom,
            ValidTo: validTo,
            RecordedAt: recordedAt,
            SupersededAt: null,
            StructuralPayload: structural,
            PiiCiphertext: piiCiphertext ?? ReadOnlyMemory<byte>.Empty);

    [Fact]
    public async Task Migration_0005_is_applied_and_ledgered()
    {
        // The fixture already applied the set; 0005 must be present in the ledger and
        // its table must exist (ADR-PC-001 §P5 forward-only discovery via MigrationSet).
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        var ledgered = (long)(await new NpgsqlCommand(
            "SELECT count(*) FROM schema_migrations WHERE name = 'projections';",
            connection).ExecuteScalarAsync())!;
        Assert.Equal(1L, ledgered);

        var tableExists = (long)(await new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.tables WHERE table_name = 'projections';",
            connection).ExecuteScalarAsync())!;
        Assert.Equal(1L, tableExists);

        var indexExists = (long)(await new NpgsqlCommand(
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'projections_current_belief_idx';",
            connection).ExecuteScalarAsync())!;
        Assert.Equal(1L, indexExists);
    }

    [Fact]
    public async Task Write_then_ReadCurrentBelief_round_trips()
    {
        var streamId = Guid.NewGuid();
        var structural = new byte[] { 0xAA, 0xBB, 0xCC };
        await Store.WriteAsync(Record(streamId, structural, RecordedAtV1, validTo: ValidFrom.AddDays(365)));

        var loaded = await Store.ReadCurrentBeliefAsync(streamId);

        Assert.NotNull(loaded);
        Assert.Equal(streamId, loaded.StreamId);
        Assert.Equal(ValidFrom, loaded.ValidFrom);
        Assert.Equal(ValidFrom.AddDays(365), loaded.ValidTo);
        Assert.Equal(RecordedAtV1, loaded.RecordedAt);
        Assert.Null(loaded.SupersededAt); // currently-believed
        Assert.Equal(structural, loaded.StructuralPayload.ToArray());
        Assert.True(loaded.PiiCiphertext.IsEmpty);
    }

    [Fact]
    public async Task Open_ended_world_time_round_trips_as_null_valid_to()
    {
        // ADR-PC-002 §P1 — valid_to NULL is an open-ended world-time slice.
        var streamId = Guid.NewGuid();
        await Store.WriteAsync(Record(streamId, [0x01], RecordedAtV1, validTo: null));

        var loaded = await Store.ReadCurrentBeliefAsync(streamId);

        Assert.NotNull(loaded);
        Assert.Null(loaded.ValidTo);
    }

    [Fact]
    public async Task Forced_correction_supersedes_v1_and_keeps_both_rows_queryable()
    {
        // ADR-PC-002 §P2 / event-store §6.3 criterion #1 — the forced-correction
        // round-trip. Write v1, supersede it, write the corrected v2: ReadCurrentBelief
        // must return v2, the prior belief (v1) must NOT be deleted, and exactly one row
        // is currently-believed.
        var streamId = Guid.NewGuid();
        var v1 = new byte[] { 0x01, 0x01 };
        var v2 = new byte[] { 0x02, 0x02 };

        await Store.WriteAsync(Record(streamId, v1, RecordedAtV1));
        await Store.SupersedeAsync(streamId, RecordedAtV2);
        await Store.WriteAsync(Record(streamId, v2, RecordedAtV2));

        var current = await Store.ReadCurrentBeliefAsync(streamId);
        Assert.NotNull(current);
        Assert.Equal(v2, current.StructuralPayload.ToArray()); // the corrected belief wins
        Assert.Null(current.SupersededAt);

        // Both beliefs remain on disk — the superseded row is kept, never overwritten.
        Assert.Equal(2L, await CountRowsAsync(streamId));
        Assert.Equal(1L, await CountSupersededAsync(streamId)); // v1 stamped
        Assert.Equal(1L, await CountCurrentBeliefAsync(streamId)); // only v2 is current

        // The superseded v1 row stays queryable with its original payload.
        var supersededPayloads = await SupersededPayloadsAsync(streamId);
        Assert.Single(supersededPayloads);
        Assert.Equal(v1, supersededPayloads[0]);
    }

    [Fact]
    public async Task Supersede_with_no_current_belief_is_a_noop()
    {
        // Superseding a stream that has no currently-believed row stamps nothing and
        // does not throw — the UPDATE is bounded to superseded_at IS NULL.
        var streamId = Guid.NewGuid();
        await Store.SupersedeAsync(streamId, RecordedAtV2);

        Assert.Equal(0L, await CountRowsAsync(streamId));
        Assert.Null(await Store.ReadCurrentBeliefAsync(streamId));
    }

    [Fact]
    public async Task Unknown_stream_has_no_current_belief()
    {
        Assert.Null(await Store.ReadCurrentBeliefAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Pii_ciphertext_bytea_round_trips()
    {
        // ADR-PC-004 §P2 — the PII ciphertext envelope is opaque BYTEA; it must survive
        // a write/read round-trip byte-for-byte (no key handling here, just storage).
        var streamId = Guid.NewGuid();
        var ciphertext = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        await Store.WriteAsync(Record(streamId, [0x07], RecordedAtV1, piiCiphertext: ciphertext));

        var loaded = await Store.ReadCurrentBeliefAsync(streamId);

        Assert.NotNull(loaded);
        Assert.False(loaded.PiiCiphertext.IsEmpty);
        Assert.Equal(ciphertext, loaded.PiiCiphertext.ToArray());
    }

    [Fact]
    public async Task Runtime_role_can_insert_update_and_select_the_projection()
    {
        // The projection is a rebuildable cache (ADR-PC-002 §P4), so unlike the
        // append-only events table the babelstone_engine role is granted UPDATE — needed
        // for supersession (§P2). Assert the privilege envelope as the engine role.
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await new NpgsqlCommand("SET ROLE babelstone_engine;", connection).ExecuteNonQueryAsync();

        var streamId = Guid.NewGuid();
        await InsertAsRoleAsync(connection, streamId);

        // SELECT is granted.
        var count = (long)(await new NpgsqlCommand(
            $"SELECT count(*) FROM projections WHERE stream_id = '{streamId}';",
            connection).ExecuteScalarAsync())!;
        Assert.Equal(1L, count);

        // UPDATE is granted (supersession): this must NOT raise InsufficientPrivilege.
        var updated = await new NpgsqlCommand(
            $"UPDATE projections SET superseded_at = now() WHERE stream_id = '{streamId}';",
            connection).ExecuteNonQueryAsync();
        Assert.Equal(1, updated);
    }

    private static async Task InsertAsRoleAsync(NpgsqlConnection connection, Guid streamId)
    {
        const string sql = """
            INSERT INTO projections
                (stream_id, valid_from, recorded_at, structural_payload)
            VALUES (@stream_id, now(), now(), @structural_payload);
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("stream_id", streamId);
        command.Parameters.AddWithValue("structural_payload", new byte[] { 0x01 });
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountRowsAsync(Guid streamId) =>
        await ScalarCountAsync(
            "SELECT count(*) FROM projections WHERE stream_id = @id;", streamId);

    private async Task<long> CountSupersededAsync(Guid streamId) =>
        await ScalarCountAsync(
            "SELECT count(*) FROM projections WHERE stream_id = @id AND superseded_at IS NOT NULL;",
            streamId);

    private async Task<long> CountCurrentBeliefAsync(Guid streamId) =>
        await ScalarCountAsync(
            "SELECT count(*) FROM projections WHERE stream_id = @id AND superseded_at IS NULL;",
            streamId);

    private async Task<long> ScalarCountAsync(string sql, Guid streamId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", streamId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<List<byte[]>> SupersededPayloadsAsync(Guid streamId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT structural_payload FROM projections " +
            "WHERE stream_id = @id AND superseded_at IS NOT NULL ORDER BY row_id;",
            connection);
        command.Parameters.AddWithValue("id", streamId);
        var payloads = new List<byte[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            payloads.Add(reader.GetFieldValue<byte[]>(0));
        }

        return payloads;
    }
}
