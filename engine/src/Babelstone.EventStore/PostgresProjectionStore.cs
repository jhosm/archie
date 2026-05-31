using Npgsql;

namespace Babelstone.EventStore;

/// <summary>
/// PostgreSQL-backed <see cref="IProjectionStorage"/>. Hand-rolled, Npgsql-only,
/// all <c>deposit_position_projection</c> SQL private to this type — the
/// storage-boundary discipline of <see cref="PostgresSnapshotStore"/> applied to the
/// Path-A bitemporal projection (ADR-PC-002 §P1/§P2).
/// </summary>
public sealed class PostgresProjectionStore(string connectionString) : IProjectionStorage
{
    public async Task WriteAsync(ProjectionRecord record, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO deposit_position_projection
                (stream_id, valid_from, valid_to, recorded_at, superseded_at,
                 structural_payload, pii_ciphertext)
            VALUES
                (@stream_id, @valid_from, @valid_to, @recorded_at, @superseded_at,
                 @structural_payload, @pii_ciphertext);
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("stream_id", record.StreamId);
        command.Parameters.AddWithValue("valid_from", record.ValidFrom);
        command.Parameters.AddWithValue("valid_to", (object?)record.ValidTo ?? DBNull.Value);
        command.Parameters.AddWithValue("recorded_at", record.RecordedAt);
        command.Parameters.AddWithValue("superseded_at", (object?)record.SupersededAt ?? DBNull.Value);
        command.Parameters.AddWithValue("structural_payload", record.StructuralPayload.ToArray());
        // The PII ciphertext envelope is NULL until PII is added later (ADR-PC-004 §P2);
        // an empty payload maps to SQL NULL, not a zero-length BYTEA.
        command.Parameters.AddWithValue(
            "pii_ciphertext",
            record.PiiCiphertext.IsEmpty ? DBNull.Value : record.PiiCiphertext.ToArray());
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SupersedeAsync(Guid streamId, DateTimeOffset supersededAt, CancellationToken ct = default)
    {
        // ADR-PC-002 §P2 — stamp superseded_at on the currently-believed row(s) only;
        // already-superseded rows keep their original stamp so the belief history stays
        // intact. A corrected row is INSERTed separately by the caller.
        const string sql = """
            UPDATE deposit_position_projection
            SET superseded_at = @superseded_at
            WHERE stream_id = @stream_id
              AND superseded_at IS NULL;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("stream_id", streamId);
        command.Parameters.AddWithValue("superseded_at", supersededAt);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<ProjectionRecord?> ReadCurrentBeliefAsync(Guid streamId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT stream_id, valid_from, valid_to, recorded_at, superseded_at,
                   structural_payload, pii_ciphertext
            FROM deposit_position_projection
            WHERE stream_id = @stream_id
              AND superseded_at IS NULL
            ORDER BY row_id DESC
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
            StreamId: reader.GetGuid(0),
            ValidFrom: reader.GetFieldValue<DateTimeOffset>(1),
            ValidTo: await reader.IsDBNullAsync(2, ct) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            RecordedAt: reader.GetFieldValue<DateTimeOffset>(3),
            SupersededAt: await reader.IsDBNullAsync(4, ct) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            StructuralPayload: reader.GetFieldValue<byte[]>(5),
            PiiCiphertext: await reader.IsDBNullAsync(6, ct)
                ? ReadOnlyMemory<byte>.Empty
                : reader.GetFieldValue<byte[]>(6));
    }
}
