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
            SELECT stream_id, at_sequence, last_event_id, state_hash, state, trusted, created_at, transaction_time
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

        return ReadRecord(reader);
    }

    public async Task<SnapshotRecord?> TryGetAtOrBeforeAsync(
        Guid streamId, long atOrBeforeSequence, CancellationToken ct = default)
    {
        // The §P1 readLatestSnapshot(..., atOrBeforeSequence): the highest snapshot that does NOT
        // sit past the as-of point. A snapshot above the point is "the future" relative to the read
        // and is excluded by the WHERE bound, so an as-of fold never seeds from a snapshot ahead of
        // its target.
        const string sql = """
            SELECT stream_id, at_sequence, last_event_id, state_hash, state, trusted, created_at, transaction_time
            FROM snapshots
            WHERE stream_id = @stream_id AND at_sequence <= @at_or_before
            ORDER BY at_sequence DESC
            LIMIT 1;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("stream_id", streamId);
        command.Parameters.AddWithValue("at_or_before", atOrBeforeSequence);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return ReadRecord(reader);
    }

    public async Task PutAsync(SnapshotRecord snapshot, CancellationToken ct = default)
    {
        // Re-putting the same (stream, sequence) overwrites — promotes to trusted or
        // refreshes a recomputed snapshot — without a separate update path.
        const string sql = """
            INSERT INTO snapshots
                (stream_id, at_sequence, last_event_id, state_hash, state, trusted, created_at, transaction_time)
            VALUES
                (@stream_id, @at_sequence, @last_event_id, @state_hash, @state, @trusted, @created_at, @transaction_time)
            ON CONFLICT (stream_id, at_sequence) DO UPDATE SET
                last_event_id    = EXCLUDED.last_event_id,
                state_hash       = EXCLUDED.state_hash,
                state            = EXCLUDED.state,
                trusted          = EXCLUDED.trusted,
                created_at       = EXCLUDED.created_at,
                transaction_time = EXCLUDED.transaction_time;
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
        command.Parameters.AddWithValue(
            "transaction_time", (object?)snapshot.TransactionTime ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Maps a positioned reader row to a <see cref="SnapshotRecord"/>. Both SELECTs project the same
    /// column list (… created_at, transaction_time), so the ordinals are shared. transaction_time is
    /// nullable — pre-0017 rows carry SQL NULL, surfaced as a null <see cref="SnapshotRecord.TransactionTime"/>.
    /// </summary>
    private static SnapshotRecord ReadRecord(NpgsqlDataReader reader)
        => new(
            StreamId: reader.GetGuid(0),
            AtSequence: reader.GetInt64(1),
            LastEventId: reader.GetGuid(2),
            StateHash: reader.GetString(3),
            State: reader.GetFieldValue<byte[]>(4),
            Trusted: reader.GetBoolean(5),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(6),
            TransactionTime: reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7));

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
