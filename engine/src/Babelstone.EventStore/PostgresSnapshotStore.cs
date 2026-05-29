using Npgsql;

namespace Babelstone.EventStore;

/// <summary>
/// PostgreSQL-backed <see cref="ISnapshotStorage"/>. Hand-rolled, Npgsql-only,
/// all <c>snapshots</c> SQL private to this type — the storage-boundary discipline
/// of <see cref="PostgresEventStore"/> applied to the snapshot cache.
/// </summary>
public sealed class PostgresSnapshotStore(string connectionString) : ISnapshotStorage
{
    public async Task<SnapshotRecord?> TryGetLatestAsync(Guid streamId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT stream_id, at_sequence, last_event_id, state_hash, state, trusted, created_at
            FROM snapshots
            WHERE stream_id = @stream_id
            ORDER BY at_sequence DESC
            LIMIT 1;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("stream_id", streamId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new SnapshotRecord(
            StreamId: reader.GetGuid(0),
            AtSequence: reader.GetInt64(1),
            LastEventId: reader.GetGuid(2),
            StateHash: reader.GetString(3),
            State: reader.GetFieldValue<byte[]>(4),
            Trusted: reader.GetBoolean(5),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(6));
    }

    public async Task PutAsync(SnapshotRecord snapshot, CancellationToken ct = default)
    {
        // Re-putting the same (stream, sequence) overwrites — promotes to trusted or
        // refreshes a recomputed snapshot — without a separate update path.
        const string sql = """
            INSERT INTO snapshots (stream_id, at_sequence, last_event_id, state_hash, state, trusted, created_at)
            VALUES (@stream_id, @at_sequence, @last_event_id, @state_hash, @state, @trusted, @created_at)
            ON CONFLICT (stream_id, at_sequence) DO UPDATE SET
                last_event_id = EXCLUDED.last_event_id,
                state_hash    = EXCLUDED.state_hash,
                state         = EXCLUDED.state,
                trusted       = EXCLUDED.trusted,
                created_at    = EXCLUDED.created_at;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("stream_id", snapshot.StreamId);
        command.Parameters.AddWithValue("at_sequence", snapshot.AtSequence);
        command.Parameters.AddWithValue("last_event_id", snapshot.LastEventId);
        command.Parameters.AddWithValue("state_hash", snapshot.StateHash);
        command.Parameters.AddWithValue("state", snapshot.State.ToArray());
        command.Parameters.AddWithValue("trusted", snapshot.Trusted);
        command.Parameters.AddWithValue("created_at", snapshot.CreatedAt);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> DiscardAsync(Guid streamId, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "DELETE FROM snapshots WHERE stream_id = @stream_id;", connection);
        command.Parameters.AddWithValue("stream_id", streamId);
        return await command.ExecuteNonQueryAsync(ct);
    }
}
