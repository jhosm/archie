using Npgsql;

namespace Babelstone.EventStore;

/// <summary>
/// PostgreSQL-backed <see cref="IProjectionStorage"/>. Hand-rolled, Npgsql-only,
/// all <c>deposit_position_projection</c> SQL private to this type — the
/// storage-boundary discipline of <see cref="PostgresSnapshotStore"/> applied to
/// the bitemporal projection cache (ADR-PC-002 §P1/§P2, ADR-PC-004 §P2).
/// </summary>
public sealed class PostgresProjectionStore(string connectionString) : IProjectionStorage
{
    public async Task WriteAsync(ProjectionRecord record, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO deposit_position_projection (
                stream_id, valid_from, valid_to,
                recorded_at, superseded_at,
                structural_payload, pii_ciphertext)
            VALUES (
                @stream_id, @valid_from, @valid_to,
                @recorded_at, @superseded_at,
                @structural_payload, @pii_ciphertext);
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("stream_id",          record.StreamId);
        command.Parameters.AddWithValue("valid_from",         record.ValidFrom);
        command.Parameters.AddWithValue("valid_to",           (object?)record.ValidTo ?? DBNull.Value);
        command.Parameters.AddWithValue("recorded_at",        record.RecordedAt);
        command.Parameters.AddWithValue("superseded_at",      (object?)record.SupersededAt ?? DBNull.Value);
        command.Parameters.AddWithValue("structural_payload", record.StructuralPayload.ToArray());
        command.Parameters.AddWithValue("pii_ciphertext",     record.PiiCiphertext.ToArray());
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SupersedeAsync(Guid streamId, DateTimeOffset supersededAt, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE deposit_position_projection
            SET superseded_at = @superseded_at
            WHERE stream_id = @stream_id
              AND superseded_at IS NULL;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("stream_id",     streamId);
        command.Parameters.AddWithValue("superseded_at", supersededAt);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<ProjectionRecord?> ReadCurrentBeliefAsync(Guid streamId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT stream_id, valid_from, valid_to,
                   recorded_at, superseded_at,
                   structural_payload, pii_ciphertext
            FROM deposit_position_projection
            WHERE stream_id = @stream_id
              AND superseded_at IS NULL
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

        return new ProjectionRecord(
            StreamId:          reader.GetGuid(0),
            ValidFrom:         reader.GetFieldValue<DateTimeOffset>(1),
            ValidTo:           reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            RecordedAt:        reader.GetFieldValue<DateTimeOffset>(3),
            SupersededAt:      reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            StructuralPayload: reader.GetFieldValue<byte[]>(5),
            PiiCiphertext:     reader.GetFieldValue<byte[]>(6));
    }
}
