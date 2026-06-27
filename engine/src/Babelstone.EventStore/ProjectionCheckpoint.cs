using Npgsql;

namespace Babelstone.EventStore;

/// <summary>
/// An async projection drainer's per-stream progress marker (migration 0011): the last
/// <c>sequence_number</c> of <see cref="StreamId"/> folded into the <see cref="ProjectionKind"/>
/// projection. A high-water mark, NOT durable belief state — the <c>projections</c> rows are
/// the truth and the <c>source_sequence</c> guard makes re-folding idempotent, so losing a
/// checkpoint just re-reads (and skips) from sequence 0.
/// </summary>
public sealed record ProjectionCheckpointRecord(
    string ProjectionKind,
    Guid StreamId,
    long LastSequenceNumber,
    DateTimeOffset LastProcessedAt);

/// <summary>
/// Stores the async projection drainers' per-stream high-water marks (one row per
/// <c>(projection_kind, stream_id)</c>). Hand-rolled, Npgsql-only — same storage-boundary
/// discipline as <see cref="IProjectionStorage"/>.
/// </summary>
public interface IProjectionCheckpointStore
{
    /// <summary>The checkpoint for the pair, or <see langword="null"/> if the stream was never drained for this kind.</summary>
    Task<ProjectionCheckpointRecord?> ReadAsync(string projectionKind, Guid streamId, CancellationToken ct = default);

    /// <summary>Upserts the per-stream checkpoint (advance the high-water mark after draining a stream's tail).</summary>
    Task WriteAsync(ProjectionCheckpointRecord record, CancellationToken ct = default);

    /// <summary>
    /// Resets every checkpoint for a kind (across all streams) for a rebuild (ADR-PC-002),
    /// so the next drain re-folds each stream from <c>sequence_number</c> 0. Safe precisely
    /// because a checkpoint is a high-water mark, not belief state.
    /// </summary>
    Task ResetAsync(string projectionKind, CancellationToken ct = default);
}

/// <summary>PostgreSQL-backed <see cref="IProjectionCheckpointStore"/>; own-connection-per-call.</summary>
public sealed class PostgresProjectionCheckpointStore(string connectionString) : IProjectionCheckpointStore
{
    public async Task<ProjectionCheckpointRecord?> ReadAsync(
        string projectionKind, Guid streamId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT projection_kind, stream_id, last_sequence_number, last_processed_at
            FROM projection_checkpoints
            WHERE projection_kind = @projection_kind AND stream_id = @stream_id;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("projection_kind", projectionKind);
        command.Parameters.AddWithValue("stream_id", streamId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new ProjectionCheckpointRecord(
            ProjectionKind: reader.GetString(0),
            StreamId: reader.GetGuid(1),
            LastSequenceNumber: reader.GetInt64(2),
            LastProcessedAt: reader.GetFieldValue<DateTimeOffset>(3));
    }

    public async Task WriteAsync(ProjectionCheckpointRecord record, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO projection_checkpoints (projection_kind, stream_id, last_sequence_number, last_processed_at)
            VALUES (@projection_kind, @stream_id, @last_sequence_number, @last_processed_at)
            ON CONFLICT (projection_kind, stream_id) DO UPDATE
            SET last_sequence_number = EXCLUDED.last_sequence_number,
                last_processed_at    = EXCLUDED.last_processed_at;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("projection_kind", record.ProjectionKind);
        command.Parameters.AddWithValue("stream_id", record.StreamId);
        command.Parameters.AddWithValue("last_sequence_number", record.LastSequenceNumber);
        command.Parameters.AddWithValue("last_processed_at", record.LastProcessedAt);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task ResetAsync(string projectionKind, CancellationToken ct = default)
    {
        const string sql = "DELETE FROM projection_checkpoints WHERE projection_kind = @projection_kind;";

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("projection_kind", projectionKind);
        await command.ExecuteNonQueryAsync(ct);
    }
}
