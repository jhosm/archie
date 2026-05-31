using System.Runtime.CompilerServices;
using Npgsql;

namespace Babelstone.EventStore;

/// <summary>
/// The PostgreSQL-backed <see cref="IEventStore"/>. Hand-rolled against the
/// ADR-PC-001 §P1 / ADR-IC-004 §P1 table contracts (no ORM, ADR-PC-010). All event
/// and outbox SQL is private to this type — the §P2 one-transaction guarantee lives
/// in exactly one place.
/// </summary>
public sealed class PostgresEventStore(string connectionString) : IEventStore
{
    public async Task AppendAsync(
        Guid streamId,
        long expectedVersion,
        IReadOnlyList<EventEnvelope> events,
        IReadOnlyList<OutboxRow> outboxRows,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(events.Count, 1, nameof(events));
        // §P2: an append never writes an event without its outbox row.
        ArgumentOutOfRangeException.ThrowIfLessThan(outboxRows.Count, 1, nameof(outboxRows));
        ValidateContiguous(streamId, expectedVersion, events);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        // Fast path: reject a stale append before writing anything.
        var actualVersion = await ReadHeadAsync(connection, tx, streamId, ct);
        if (actualVersion != expectedVersion)
        {
            await tx.RollbackAsync(ct);
            throw new ConcurrencyException(streamId, expectedVersion, actualVersion);
        }

        try
        {
            await InsertEventsAsync(connection, tx, events, ct);
            await InsertOutboxAsync(connection, tx, outboxRows, ct);
            await tx.CommitAsync(ct);
        }
        catch (PostgresException e)
            when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "events_stream_seq_uq")
        {
            // Race backstop: a concurrent appender committed the same (stream_id,
            // sequence_number) between our read and our insert. The UNIQUE constraint
            // is the real guarantee; surface it as the same concurrency conflict.
            // Other unique violations (e.g. an outbox PK collision) are not concurrency
            // conflicts and propagate unchanged.
            await tx.RollbackAsync(ct);
            var head = await ReadHeadAsync(connectionString, streamId, ct);
            throw new ConcurrencyException(streamId, expectedVersion, head);
        }
    }

    public async IAsyncEnumerable<EventEnvelope> LoadAsync(
        Guid streamId,
        long fromSequence = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        const string sql = """
            SELECT event_id, stream_id, sequence_number, event_type, event_schema_version,
                   family, partition_key, pack_version, schema_version, valid_time,
                   transaction_time, causation_id, correlation_id, actor, payload, payload_schema_id
            FROM events
            WHERE stream_id = @stream_id AND sequence_number >= @from_sequence
            ORDER BY sequence_number;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("stream_id", streamId);
        command.Parameters.AddWithValue("from_sequence", fromSequence);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return ReadEnvelope(reader);
        }
    }

    public async Task<IReadOnlyList<Guid>> ReadStreamIdsAsync(string family, CancellationToken ct = default)
    {
        // The set of streams the async projection drainer iterates for this family. The events
        // table has no cluster-wide total order, so draining is per stream (each stream's tail
        // folded from its own checkpoint). DISTINCT over the family's events; v1 scale only.
        const string sql = "SELECT DISTINCT stream_id FROM events WHERE family = @family;";

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("family", family);

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static EventEnvelope ReadEnvelope(NpgsqlDataReader r) => new(
        EventId: r.GetGuid(0),
        StreamId: r.GetGuid(1),
        SequenceNumber: r.GetInt64(2),
        EventType: r.GetString(3),
        EventSchemaVersion: r.GetInt32(4),
        Family: r.GetString(5),
        PartitionKey: r.GetGuid(6),
        PackVersion: r.GetString(7),
        SchemaVersion: r.GetString(8),
        ValidTime: r.GetFieldValue<DateTimeOffset>(9),
        TransactionTime: r.GetFieldValue<DateTimeOffset>(10),
        CausationId: r.IsDBNull(11) ? null : r.GetGuid(11),
        CorrelationId: r.IsDBNull(12) ? null : r.GetGuid(12),
        Actor: r.GetString(13),
        Payload: r.GetFieldValue<byte[]>(14),
        PayloadSchemaId: r.GetInt32(15));

    private static void ValidateContiguous(Guid streamId, long expectedVersion, IReadOnlyList<EventEnvelope> events)
    {
        for (var i = 0; i < events.Count; i++)
        {
            var expectedSeq = expectedVersion + 1 + i;
            if (events[i].SequenceNumber != expectedSeq)
            {
                throw new ArgumentException(
                    $"Event {i} on stream {streamId} has sequence_number {events[i].SequenceNumber}; " +
                    $"expected {expectedSeq} (contiguous from expectedVersion + 1).", nameof(events));
            }

            if (events[i].StreamId != streamId)
            {
                throw new ArgumentException(
                    $"Event {i} carries stream_id {events[i].StreamId}, not {streamId}.", nameof(events));
            }
        }
    }

    private static async Task<long> ReadHeadAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid streamId, CancellationToken ct)
    {
        // NULL (no rows yet) reads back as the "stream must not exist" sentinel, -1.
        await using var command = new NpgsqlCommand(
            "SELECT coalesce(max(sequence_number), -1) FROM events WHERE stream_id = @stream_id;", connection, tx);
        command.Parameters.AddWithValue("stream_id", streamId);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<long> ReadHeadAsync(string connectionString, Guid streamId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT coalesce(max(sequence_number), -1) FROM events WHERE stream_id = @stream_id;", connection);
        command.Parameters.AddWithValue("stream_id", streamId);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task InsertEventsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, IReadOnlyList<EventEnvelope> events, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO events (
                event_id, stream_id, sequence_number, event_type, event_schema_version,
                family, partition_key, pack_version, schema_version, valid_time,
                transaction_time, causation_id, correlation_id, actor, payload, payload_schema_id)
            VALUES (
                $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16);
            """;

        await using var batch = new NpgsqlBatch(connection, tx);
        foreach (var e in events)
        {
            var command = new NpgsqlBatchCommand(sql);
            command.Parameters.Add(new NpgsqlParameter { Value = e.EventId });
            command.Parameters.Add(new NpgsqlParameter { Value = e.StreamId });
            command.Parameters.Add(new NpgsqlParameter { Value = e.SequenceNumber });
            command.Parameters.Add(new NpgsqlParameter { Value = e.EventType });
            command.Parameters.Add(new NpgsqlParameter { Value = e.EventSchemaVersion });
            command.Parameters.Add(new NpgsqlParameter { Value = e.Family });
            command.Parameters.Add(new NpgsqlParameter { Value = e.PartitionKey });
            command.Parameters.Add(new NpgsqlParameter { Value = e.PackVersion });
            command.Parameters.Add(new NpgsqlParameter { Value = e.SchemaVersion });
            command.Parameters.Add(new NpgsqlParameter { Value = e.ValidTime });
            command.Parameters.Add(new NpgsqlParameter { Value = e.TransactionTime });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)e.CausationId ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)e.CorrelationId ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter { Value = e.Actor });
            command.Parameters.Add(new NpgsqlParameter { Value = e.Payload.ToArray() });
            command.Parameters.Add(new NpgsqlParameter { Value = e.PayloadSchemaId });
            batch.BatchCommands.Add(command);
        }

        await batch.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertOutboxAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, IReadOnlyList<OutboxRow> rows, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO outbox (
                event_id, aggregate_type, aggregate_id, sequence_number, event_type, payload,
                schema_id, status, created_at, published_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10);
            """;

        await using var batch = new NpgsqlBatch(connection, tx);
        foreach (var r in rows)
        {
            var command = new NpgsqlBatchCommand(sql);
            command.Parameters.Add(new NpgsqlParameter { Value = r.EventId });
            command.Parameters.Add(new NpgsqlParameter { Value = r.AggregateType });
            command.Parameters.Add(new NpgsqlParameter { Value = r.AggregateId });
            command.Parameters.Add(new NpgsqlParameter { Value = r.SequenceNumber });
            command.Parameters.Add(new NpgsqlParameter { Value = r.EventType });
            command.Parameters.Add(new NpgsqlParameter { Value = r.Payload.ToArray() });
            command.Parameters.Add(new NpgsqlParameter { Value = r.SchemaId });
            command.Parameters.Add(new NpgsqlParameter { Value = r.Status == OutboxStatus.Published ? "PUBLISHED" : "PENDING" });
            command.Parameters.Add(new NpgsqlParameter { Value = r.CreatedAt });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)r.PublishedAt ?? DBNull.Value });
            batch.BatchCommands.Add(command);
        }

        await batch.ExecuteNonQueryAsync(ct);
    }
}
