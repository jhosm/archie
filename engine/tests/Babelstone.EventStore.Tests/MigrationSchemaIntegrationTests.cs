using Babelstone.EventStore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// Applies the migration set against a real PostgreSQL and asserts the §P1/§P4
/// schema and the §P3 append-only privilege envelope. Tagged Integration so the
/// default (Docker-free) engine CI job skips it; the integration lane runs it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MigrationSchemaIntegrationTests : IAsyncLifetime
{
    // Match the dev stack's pinned major version (infra/compose.yaml — the
    // ADR-PC-001 event store, latest stable PG 18) so the schema and §P3 role
    // behaviour are tested against the PostgreSQL the engine actually deploys on.
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine")
        .Build();

    private string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync() => await _pg.StartAsync();

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    [Fact]
    public async Task Apply_is_idempotent_and_ledgers_each_migration()
    {
        var runner = new MigrationRunner(ConnectionString);

        var firstRun = await runner.ApplyAsync();
        Assert.Equal(MigrationSet.All.Length, firstRun.Count);

        var secondRun = await runner.ApplyAsync();
        Assert.Empty(secondRun);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        var ledgered = (long)(await new NpgsqlCommand(
            "SELECT count(*) FROM schema_migrations;", connection).ExecuteScalarAsync())!;
        Assert.Equal(MigrationSet.All.Length, ledgered);
    }

    [Theory]
    [InlineData("events_stream_seq_uq")]
    [InlineData("events_partition_key_seq_idx")]
    [InlineData("outbox_pending_idx")]
    public async Task Creates_the_PC001_P4_indices(string indexName)
    {
        await new MigrationRunner(ConnectionString).ApplyAsync();

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_indexes WHERE indexname = @name;", connection);
        command.Parameters.AddWithValue("name", indexName);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Runtime_role_can_append_but_cannot_update_or_delete_events()
    {
        await new MigrationRunner(ConnectionString).ApplyAsync();

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        // Drop superuser bypass: act as the engine's runtime role for the rest.
        await new NpgsqlCommand("SET ROLE babelstone_engine;", connection).ExecuteNonQueryAsync();

        var streamId = Guid.NewGuid();
        await InsertEventAsync(connection, streamId, sequence: 0);

        // INSERT + SELECT are granted; the row is there.
        var count = (long)(await new NpgsqlCommand(
            $"SELECT count(*) FROM events WHERE stream_id = '{streamId}';", connection).ExecuteScalarAsync())!;
        Assert.Equal(1L, count);

        // UPDATE and DELETE on the append-only log are denied at the boundary (42501).
        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            new NpgsqlCommand("UPDATE events SET actor = 'tamper';", connection).ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, update.SqlState);

        var delete = await Assert.ThrowsAsync<PostgresException>(() =>
            new NpgsqlCommand("DELETE FROM events;", connection).ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, delete.SqlState);
    }

    private static async Task InsertEventAsync(NpgsqlConnection connection, Guid streamId, long sequence)
    {
        const string sql = """
            INSERT INTO events (
                event_id, stream_id, sequence_number, event_type, event_schema_version,
                family, partition_key, pack_version, schema_version, valid_time,
                actor, payload, payload_schema_id)
            VALUES (
                @event_id, @stream_id, @sequence_number, 'term_deposit.DepositConstituted', 1,
                'term_deposit', @stream_id, 'pt.2026.1', 'term_deposit@2026.1', now(),
                'test', @payload, 42);
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("event_id", Guid.NewGuid());
        command.Parameters.AddWithValue("stream_id", streamId);
        command.Parameters.AddWithValue("sequence_number", sequence);
        command.Parameters.AddWithValue("payload", new byte[] { 0x01, 0x02 });
        await command.ExecuteNonQueryAsync();
    }
}
