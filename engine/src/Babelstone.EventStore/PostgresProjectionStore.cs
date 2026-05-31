using Npgsql;

namespace Babelstone.EventStore;

/// <summary>
/// PostgreSQL-backed <see cref="IProjectionStorage"/>. Hand-rolled, Npgsql-only,
/// all <c>projections</c> SQL private to this type — the
/// storage-boundary discipline of <see cref="PostgresSnapshotStore"/> applied to the
/// Path-A bitemporal projection (ADR-PC-002 §P1/§P2). Every operation scopes to the
/// <c>(stream_id, projection_kind)</c> pair (migration 0010).
/// </summary>
public sealed class PostgresProjectionStore(string connectionString) : IProjectionStorage
{
    public async Task WriteAsync(ProjectionRecord record, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await InsertAsync(connection, transaction: null, record, ct);
    }

    public async Task SupersedeAsync(
        Guid streamId, string projectionKind, DateTimeOffset supersededAt, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await SupersedeAsync(connection, transaction: null, streamId, projectionKind, supersededAt, ct);
    }

    public async Task SupersedeAndWriteAsync(ProjectionRecord record, CancellationToken ct = default)
    {
        // ADR-PC-002 §P2 — the supersede-then-insert pair is atomic: one connection, one
        // transaction. A crash between the two halves can never leave the (stream, kind)
        // with zero or two current-belief rows. The prior belief is superseded AT the new
        // row's RecordedAt (the source event's transaction_time), keeping the belief-time
        // axis contiguous.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await SupersedeAsync(connection, transaction, record.StreamId, record.ProjectionKind, record.RecordedAt, ct);
        await InsertAsync(connection, transaction, record, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task SupersedeAllAsync(
        string projectionKind, DateTimeOffset supersededAt, CancellationToken ct = default)
    {
        // Rebuild supersede-all (ADR-PC-002 §P4): close every current belief for the kind
        // across all streams. Preserves them as belief history (UPDATE, not DELETE).
        const string sql = """
            UPDATE projections
            SET superseded_at = @superseded_at
            WHERE projection_kind = @projection_kind
              AND superseded_at IS NULL;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("projection_kind", projectionKind);
        command.Parameters.AddWithValue("superseded_at", supersededAt);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<ProjectionRecord?> ReadCurrentBeliefAsync(
        Guid streamId, string projectionKind, CancellationToken ct = default)
    {
        // ORDER BY recorded_at DESC (belief-time), row_id DESC only as a deterministic
        // tie-break — never row_id alone, because the BIGSERIAL surrogate is re-assigned on
        // rebuild and would make the read non-deterministic across rebuilds (ADR-PC-010 §P5).
        // For this projection class the tie-break never decides: the partial UNIQUE index
        // (projections_current_belief_uq) leaves exactly one current-belief row per
        // (stream_id, projection_kind), so LIMIT 1 is order-independent. A FUTURE projection
        // kind that legitimately keeps multiple un-superseded rows per stream (e.g. F.6's
        // per-period accrual schedule) and stamps two with an identical recorded_at would make
        // row_id the deciding factor and break rebuild byte-identity — such a kind must order on
        // an event-derived key (e.g. source_sequence), not row_id. Flagged for F.6.
        const string sql = """
            SELECT stream_id, projection_kind, source_sequence, valid_from, valid_to, recorded_at,
                   superseded_at, structural_payload, pii_ciphertext
            FROM projections
            WHERE stream_id = @stream_id
              AND projection_kind = @projection_kind
              AND superseded_at IS NULL
            ORDER BY recorded_at DESC, row_id DESC
            LIMIT 1;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("stream_id", streamId);
        command.Parameters.AddWithValue("projection_kind", projectionKind);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new ProjectionRecord(
            StreamId: reader.GetGuid(0),
            ProjectionKind: reader.GetString(1),
            SourceSequence: reader.GetInt64(2),
            ValidFrom: reader.GetFieldValue<DateTimeOffset>(3),
            ValidTo: await reader.IsDBNullAsync(4, ct) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            RecordedAt: reader.GetFieldValue<DateTimeOffset>(5),
            SupersededAt: await reader.IsDBNullAsync(6, ct) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            StructuralPayload: reader.GetFieldValue<byte[]>(7),
            PiiCiphertext: await reader.IsDBNullAsync(8, ct)
                ? ReadOnlyMemory<byte>.Empty
                : reader.GetFieldValue<byte[]>(8));
    }

    private static async Task InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, ProjectionRecord record, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO projections
                (stream_id, projection_kind, source_sequence, valid_from, valid_to, recorded_at,
                 superseded_at, structural_payload, pii_ciphertext)
            VALUES
                (@stream_id, @projection_kind, @source_sequence, @valid_from, @valid_to, @recorded_at,
                 @superseded_at, @structural_payload, @pii_ciphertext);
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("stream_id", record.StreamId);
        command.Parameters.AddWithValue("projection_kind", record.ProjectionKind);
        command.Parameters.AddWithValue("source_sequence", record.SourceSequence);
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

    private static async Task SupersedeAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction,
        Guid streamId, string projectionKind, DateTimeOffset supersededAt, CancellationToken ct)
    {
        // ADR-PC-002 §P2 — stamp superseded_at on the currently-believed row for the
        // (stream_id, projection_kind) pair only; already-superseded rows keep their original
        // stamp so the belief history stays intact.
        const string sql = """
            UPDATE projections
            SET superseded_at = @superseded_at
            WHERE stream_id = @stream_id
              AND projection_kind = @projection_kind
              AND superseded_at IS NULL;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("stream_id", streamId);
        command.Parameters.AddWithValue("projection_kind", projectionKind);
        command.Parameters.AddWithValue("superseded_at", supersededAt);
        await command.ExecuteNonQueryAsync(ct);
    }
}
