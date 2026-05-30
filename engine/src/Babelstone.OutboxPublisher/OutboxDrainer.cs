using System.Buffers.Binary;
using System.Globalization;
using Babelstone.EventStore;
using Confluent.Kafka;
using Npgsql;

namespace Babelstone.OutboxPublisher;

/// <summary>
/// The IC-004 polling relay (Epic E.4, walking-skeleton MINIMAL). One <see cref="DrainOnceAsync"/>
/// call: SELECTs PENDING outbox rows in drain order, builds the Confluent wire-format value from
/// the row's embedded <c>schema_id</c> (NO Schema-Registry lookup — ADR-IC-004 §P3), sets the
/// CloudEvents Binary-mode Kafka headers (ADR-IC-008), produces keyed by <c>aggregate_id</c> to a
/// topic named after <c>aggregate_type</c>, and on ack flips the row to PUBLISHED (the only verbs
/// the engine role holds on outbox).
/// </summary>
/// <remarks>
/// Hardening — FOR UPDATE SKIP LOCKED, the publish-lag SLI, HA publisher coordination — is Epic G.1,
/// deliberately NOT here. On Redpanda unavailability the rows stay PENDING and the produce throws up
/// to the caller (the hosted loop backs off); rows are NEVER marked FAILED (ADR-IC-004 §P7).
/// </remarks>
public sealed class OutboxDrainer : IAsyncDisposable
{
    // Confluent wire format (ADR-IC-002 §P3 / ADR-IC-004 §P3): magic byte 0x00, then the
    // 4-byte big-endian schema_id, then the bare Avro value the codec produced.
    private const byte MagicByte = 0x00;

    private readonly OutboxRelayOptions _options;
    private readonly IProducer<byte[], byte[]> _producer;
    private readonly bool _ownsProducer;

    public OutboxDrainer(OutboxRelayOptions options, IProducer<byte[], byte[]>? producer = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (producer is null)
        {
            // Order is preserved by draining and producing one row at a time per aggregate
            // (ADR-IC-004 §P2). EnableIdempotence keeps the broker from reordering on retry.
            var config = new ProducerConfig
            {
                BootstrapServers = options.BootstrapServers,
                EnableIdempotence = true,
                Acks = Acks.All,
            };
            _producer = new ProducerBuilder<byte[], byte[]>(config).Build();
            _ownsProducer = true;
        }
        else
        {
            _producer = producer;
            _ownsProducer = false;
        }
    }

    /// <summary>
    /// Drains one batch of PENDING rows. Returns the number of rows published this cycle.
    /// Produces each row synchronously (produce + await ack) before marking it PUBLISHED and
    /// moving to the next — per-aggregate FIFO by construction (ADR-IC-004 §P2).
    /// </summary>
    public async Task<int> DrainOnceAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(ct);

        var rows = await ReadPendingAsync(connection, _options.BatchSize, ct);
        var published = 0;
        foreach (var row in rows)
        {
            await PublishAsync(row, ct);
            await MarkPublishedAsync(connection, row.EventId, ct);
            published++;
        }

        return published;
    }

    private async Task PublishAsync(OutboxRow row, CancellationToken ct)
    {
        var message = new Message<byte[], byte[]>
        {
            // Partition by aggregate_id so per-aggregate order is a partition-order guarantee.
            Key = row.AggregateId.ToByteArray(),
            Value = ToConfluentWireFormat(row.SchemaId, row.Payload.Span),
            Headers = BuildHeaders(row),
        };

        // Topic = aggregate_type verbatim (e.g. "term_deposit"). Documented convention; the
        // SMT-style auto-routing is out of scope (ADR-IC-004 §Consequences).
        var topic = row.AggregateType;
        await _producer.ProduceAsync(topic, message, ct);
    }

    /// <summary>magic byte 0x00 ‖ big-endian int32 schema_id ‖ avro value.</summary>
    internal static byte[] ToConfluentWireFormat(int schemaId, ReadOnlySpan<byte> avroValue)
    {
        var framed = new byte[5 + avroValue.Length];
        framed[0] = MagicByte;
        BinaryPrimitives.WriteInt32BigEndian(framed.AsSpan(1, 4), schemaId);
        avroValue.CopyTo(framed.AsSpan(5));
        return framed;
    }

    // CloudEvents 1.0 Binary Content Mode (ADR-IC-008): attributes as Kafka headers, the Avro
    // value as the message value. Every header here is derivable from the outbox row alone.
    private Headers BuildHeaders(OutboxRow row)
    {
        var headers = new Headers();
        Add(headers, "ce_specversion", "1.0");
        Add(headers, "ce_id", row.EventId.ToString());
        Add(headers, "ce_source", _options.Source);
        Add(headers, "ce_type", ReverseDnsType(row.EventType));
        Add(headers, "ce_time", row.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        Add(headers, "ce_datacontenttype", "application/avro");
        Add(headers, "ce_subject", row.AggregateId.ToString());
        Add(headers, "ce_aggregatetype", row.AggregateType);
        return headers;
    }

    private static void Add(Headers headers, string key, string value)
        => headers.Add(key, System.Text.Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// Reverse-DNS CloudEvents type (ADR-IC-008): "term_deposit.DepositConstituted" →
    /// "com.bank.deposits.DepositConstituted". The "deposits" domain mirrors the Avro
    /// namespace prefix (ADR-IC-002 §P1); "com.bank" is the deployment's reverse-DNS root.
    /// </summary>
    internal static string ReverseDnsType(string eventType)
    {
        var dot = eventType.IndexOf('.');
        var eventName = dot >= 0 ? eventType[(dot + 1)..] : eventType;
        return $"com.bank.deposits.{eventName}";
    }

    private static async Task<List<OutboxRow>> ReadPendingAsync(NpgsqlConnection connection, int batchSize, CancellationToken ct)
    {
        // The §P2 drain (amended 2026-05-29): ORDER BY created_at, sequence_number — NOT event_id.
        const string sql = """
            SELECT event_id, aggregate_type, aggregate_id, sequence_number, event_type, payload,
                   schema_id, status, created_at, published_at
            FROM outbox
            WHERE status = 'PENDING'
            ORDER BY created_at, sequence_number
            LIMIT @batch_size;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("batch_size", batchSize);

        var rows = new List<OutboxRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(ReadRow(reader));
        }

        return rows;
    }

    private static OutboxRow ReadRow(NpgsqlDataReader r) => new(
        EventId: r.GetGuid(0),
        AggregateType: r.GetString(1),
        AggregateId: r.GetGuid(2),
        SequenceNumber: r.GetInt64(3),
        EventType: r.GetString(4),
        Payload: r.GetFieldValue<byte[]>(5),
        SchemaId: r.GetInt32(6),
        Status: r.GetString(7) == "PUBLISHED" ? OutboxStatus.Published : OutboxStatus.Pending,
        CreatedAt: r.GetFieldValue<DateTimeOffset>(8),
        PublishedAt: r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9));

    private static async Task MarkPublishedAsync(NpgsqlConnection connection, Guid eventId, CancellationToken ct)
    {
        // UPDATE(status, published_at) — the only mutating verb the babelstone_engine role
        // holds on outbox (0002_append_only_role.sql).
        const string sql = """
            UPDATE outbox
            SET status = 'PUBLISHED', published_at = clock_timestamp()
            WHERE event_id = @event_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("event_id", eventId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsProducer)
        {
            // Flush in-flight produces before the process tears down.
            _producer.Flush(TimeSpan.FromSeconds(10));
            _producer.Dispose();
        }

        await ValueTask.CompletedTask;
    }
}
