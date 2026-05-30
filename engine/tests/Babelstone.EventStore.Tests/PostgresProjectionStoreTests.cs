using Babelstone.EventStore;
using Babelstone.EventStore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// Integration tests for <see cref="PostgresProjectionStore"/> and the 0005
/// migration (ADR-PC-002 §P1/§P2, ADR-PC-004 §P2, ADR-IC-009).
/// Real PostgreSQL 18 via Testcontainers; tagged Integration so the default
/// (Docker-free) engine CI job skips them.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresProjectionStoreTests : IAsyncLifetime
{
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

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static ProjectionRecord MakeRecord(
        Guid streamId,
        DateTimeOffset? supersededAt = null,
        byte[]? structural = null,
        byte[]? pii = null) =>
        new(
            StreamId:          streamId,
            ValidFrom:         T0,
            ValidTo:           null,
            RecordedAt:        T0,
            SupersededAt:      supersededAt,
            StructuralPayload: structural ?? [0x01, 0x02],
            PiiCiphertext:     pii        ?? []);

    // -----------------------------------------------------------------------
    // Migration covers 0005
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MigrationRunner_applies_0005_on_top_of_0001_to_0004()
    {
        // MigrationRunner runs in InitializeAsync; we just check the ledger entry.
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT name FROM schema_migrations WHERE version = 5;", connection);
        var name = (string?)(await command.ExecuteScalarAsync());
        Assert.Equal("projections", name);
    }

    // -----------------------------------------------------------------------
    // Write + ReadCurrentBelief round-trip
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Write_then_ReadCurrentBelief_round_trips_all_fields()
    {
        var streamId   = Guid.NewGuid();
        var structural = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var pii        = new byte[] { 0xFF, 0x00 };
        var record     = new ProjectionRecord(
            StreamId:          streamId,
            ValidFrom:         T0,
            ValidTo:           T1,
            RecordedAt:        T0,
            SupersededAt:      null,
            StructuralPayload: structural,
            PiiCiphertext:     pii);

        await Store.WriteAsync(record);
        var loaded = await Store.ReadCurrentBeliefAsync(streamId);

        Assert.NotNull(loaded);
        Assert.Equal(streamId,   loaded.StreamId);
        Assert.Equal(T0,         loaded.ValidFrom);
        Assert.Equal(T1,         loaded.ValidTo);
        Assert.Equal(T0,         loaded.RecordedAt);
        Assert.Null(loaded.SupersededAt);
        Assert.Equal(structural, loaded.StructuralPayload.ToArray());
        Assert.Equal(pii,        loaded.PiiCiphertext.ToArray());
    }

    [Fact]
    public async Task ReadCurrentBelief_returns_null_for_unknown_stream()
    {
        Assert.Null(await Store.ReadCurrentBeliefAsync(Guid.NewGuid()));
    }

    // -----------------------------------------------------------------------
    // Forced-correction round-trip (ADR-PC-002 §P2 / §6.3 criterion #1)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Write_supersede_write_returns_v2_as_current_belief()
    {
        var streamId  = Guid.NewGuid();
        var v1Payload = new byte[] { 0x01 };
        var v2Payload = new byte[] { 0x02 };
        var v1 = MakeRecord(streamId, structural: v1Payload);
        var v2 = MakeRecord(streamId, structural: v2Payload);

        // Step 1: write initial belief.
        await Store.WriteAsync(v1);

        // Step 2: supersede (correction discovered).
        var supersededAt = T0.AddHours(1);
        await Store.SupersedeAsync(streamId, supersededAt);

        // Step 3: write corrected belief.
        await Store.WriteAsync(v2);

        // Current belief is v2.
        var current = await Store.ReadCurrentBeliefAsync(streamId);
        Assert.NotNull(current);
        Assert.Equal(v2Payload, current.StructuralPayload.ToArray());
        Assert.Null(current.SupersededAt);
    }

    [Fact]
    public async Task Both_rows_remain_in_the_table_after_correction()
    {
        // ADR-PC-002 §P2: history is never deleted — the old row is closed, not removed.
        var streamId  = Guid.NewGuid();
        var v1Payload = new byte[] { 0xAA };
        var v2Payload = new byte[] { 0xBB };

        await Store.WriteAsync(MakeRecord(streamId, structural: v1Payload));
        await Store.SupersedeAsync(streamId, T0.AddHours(1));
        await Store.WriteAsync(MakeRecord(streamId, structural: v2Payload));

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM deposit_position_projection WHERE stream_id = @sid;", connection);
        command.Parameters.AddWithValue("sid", streamId);
        var total = (long)(await command.ExecuteScalarAsync())!;
        Assert.Equal(2L, total);
    }

    [Fact]
    public async Task Superseded_row_has_superseded_at_set()
    {
        var streamId     = Guid.NewGuid();
        var supersededAt = T0.AddHours(2);

        await Store.WriteAsync(MakeRecord(streamId));
        await Store.SupersedeAsync(streamId, supersededAt);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT superseded_at
            FROM deposit_position_projection
            WHERE stream_id = @sid
              AND superseded_at IS NOT NULL;
            """, connection);
        command.Parameters.AddWithValue("sid", streamId);
        var stored = (DateTimeOffset?)(await command.ExecuteScalarAsync());
        Assert.NotNull(stored);
    }

    // -----------------------------------------------------------------------
    // PII BYTEA round-trip (ADR-PC-004 §P2)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PII_ciphertext_BYTEA_round_trips_intact()
    {
        var streamId  = Guid.NewGuid();
        var piiBytes  = new byte[] { 0x00, 0xFF, 0x7F, 0x80, 0xDE, 0xAD };
        var record    = MakeRecord(streamId, pii: piiBytes);

        await Store.WriteAsync(record);
        var loaded = await Store.ReadCurrentBeliefAsync(streamId);

        Assert.NotNull(loaded);
        Assert.Equal(piiBytes, loaded.PiiCiphertext.ToArray());
    }

    [Fact]
    public async Task Null_PII_ciphertext_stored_and_returned_as_empty()
    {
        // pii_ciphertext column is nullable; empty byte array maps to null in the DB.
        var streamId = Guid.NewGuid();
        var record   = MakeRecord(streamId, pii: []);

        await Store.WriteAsync(record);
        var loaded = await Store.ReadCurrentBeliefAsync(streamId);

        Assert.NotNull(loaded);
        // Empty array round-trips as either empty or null at the DB level; both are valid.
        Assert.True(loaded.PiiCiphertext.Length == 0);
    }

    // -----------------------------------------------------------------------
    // babelstone_engine role can UPDATE the projection table
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Engine_role_can_update_projection_for_supersession()
    {
        // ADR-PC-002 §P4: projections are rebuildable, not source-of-truth.
        // Unlike append-only events, UPDATE is granted to babelstone_engine.
        var streamId = Guid.NewGuid();
        await Store.WriteAsync(MakeRecord(streamId));

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        // Drop superuser bypass: act as the engine's runtime role.
        await new NpgsqlCommand("SET ROLE babelstone_engine;", connection).ExecuteNonQueryAsync();

        // UPDATE (supersession) must succeed under the engine role.
        await using var command = new NpgsqlCommand(
            """
            UPDATE deposit_position_projection
            SET superseded_at = now()
            WHERE stream_id = @sid
              AND superseded_at IS NULL;
            """, connection);
        command.Parameters.AddWithValue("sid", streamId);
        var affected = await command.ExecuteNonQueryAsync();
        Assert.Equal(1, affected);
    }
}
