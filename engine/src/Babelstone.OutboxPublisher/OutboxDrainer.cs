using System.Buffers.Binary;
using System.Diagnostics.Metrics;
using System.Globalization;
using Babelstone.EventStore;
using Babelstone.Telemetry;
using Confluent.Kafka;
using Npgsql;

namespace Babelstone.OutboxPublisher;

/// <summary>
/// The IC-004 polling relay (Epic E.4, hardened in G.1). One <see cref="DrainOnceAsync"/> call:
/// SELECTs PENDING outbox rows in drain order <c>FOR UPDATE SKIP LOCKED</c> (so concurrent relay
/// instances claim disjoint rows — never the same row twice — ADR-IC-004 §P2 / §Residual-risks),
/// builds the Confluent wire-format value from the row's embedded <c>schema_id</c> (NO
/// Schema-Registry lookup — ADR-IC-004 §P3), sets the CloudEvents Binary-mode Kafka headers
/// (ADR-IC-008), produces keyed by <c>aggregate_id</c> to a topic named after <c>aggregate_type</c>,
/// flips the row to PUBLISHED (the only verbs the engine role holds on outbox), and records the
/// publish-lag SLI (ADR-IC-004 §P4) — <c>published_at − created_at</c> — on the shared meter.
/// </summary>
/// <remarks>
/// The read + publish + flip run in ONE transaction: the <c>FOR UPDATE</c> row locks are held until
/// commit, so a second concurrent drainer's <c>SKIP LOCKED</c> read steps over this drainer's
/// in-flight rows rather than re-publishing them (the §Residual-risks dual-publish window shrinks to
/// a crash between produce-ack and commit, which consumer-inbox idempotency absorbs). On Redpanda
/// unavailability the produce throws, the transaction rolls back, the rows stay PENDING (NEVER
/// FAILED — ADR-IC-004 §P7), and the hosted loop backs off.
/// </remarks>
public sealed class OutboxDrainer : IAsyncDisposable
{
    // Confluent wire format (ADR-IC-002 §P3 / ADR-IC-004 §P3): magic byte 0x00, then the
    // 4-byte big-endian schema_id, then the bare Avro value the codec produced.
    private const byte MagicByte = 0x00;

    // The publish-lag SLI (ADR-IC-004 §P4) on the shared Babelstone meter (ADR-IC-007 Layer 1):
    // one histogram of seconds-from-enqueue-to-ack, tagged by aggregate_type. A host turns it on
    // with AddMeter(BabelstoneTelemetry.MeterName); with no listener Record is a near no-op.
    private static readonly Histogram<double> PublishLagSeconds =
        BabelstoneTelemetry.Meter.CreateHistogram<double>(
            BabelstoneAttributes.OutboxPublishLagMetric,
            unit: "s",
            description: "Seconds from outbox-row enqueue (created_at) to successful publish ack (published_at).");

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
    /// <remarks>
    /// The read claims rows <c>FOR UPDATE SKIP LOCKED</c> and the whole cycle runs in one
    /// transaction, so concurrent drainers claim disjoint batches (no double-publish) without
    /// blocking on each other (no deadlock). A produce failure rolls the transaction back, leaving
    /// every row in the batch PENDING for the next cycle (ADR-IC-004 §P7).
    /// </remarks>
    public async Task<int> DrainOnceAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var rows = await ReadPendingAsync(connection, transaction, _options.BatchSize, ct);
        var published = 0;
        foreach (var row in rows)
        {
            await PublishAsync(row, ct);
            var publishedAt = await MarkPublishedAsync(connection, transaction, row.EventId, ct);
            RecordPublishLag(row, publishedAt);
            published++;
        }

        // Commit releases the FOR UPDATE locks and makes the PUBLISHED flips visible together. If a
        // produce above threw, we never reach here — the using-dispose rolls back and the rows stay
        // PENDING (ADR-IC-004 §P7), so no row is ever lost or marked PUBLISHED without an ack.
        await transaction.CommitAsync(ct);
        return published;
    }

    /// <summary>
    /// Records the publish-lag SLI for one row (ADR-IC-004 §P4): the seconds between the row's
    /// enqueue (<c>created_at</c>, the domain-transaction time) and its DB-stamped publish ack
    /// (<c>published_at</c>), tagged by <c>aggregate_type</c> so lag is breakable by topic.
    /// </summary>
    private static void RecordPublishLag(OutboxRow row, DateTimeOffset publishedAt)
    {
        var lagSeconds = (publishedAt - row.CreatedAt).TotalSeconds;
        PublishLagSeconds.Record(
            lagSeconds,
            new KeyValuePair<string, object?>(BabelstoneAttributes.AggregateType, row.AggregateType));
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

    private static async Task<List<OutboxRow>> ReadPendingAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int batchSize, CancellationToken ct)
    {
        // The §P2 drain (amended 2026-05-29): ORDER BY created_at, sequence_number — NOT event_id.
        // FOR UPDATE SKIP LOCKED (ADR-IC-004 §P2 / §Residual-risks) is the HA-coordination seam:
        // each row this read returns is row-locked for the open transaction, so a concurrent
        // drainer SKIPs it and claims a disjoint batch instead — competing instances never select
        // the same PENDING row, so they cannot double-publish, and SKIP LOCKED (not a plain
        // FOR UPDATE) means neither blocks on the other (no lock-wait, no deadlock).
        const string sql = """
            SELECT event_id, aggregate_type, aggregate_id, sequence_number, event_type, payload,
                   schema_id, status, created_at, published_at
            FROM outbox
            WHERE status = 'PENDING'
            ORDER BY created_at, sequence_number
            LIMIT @batch_size
            FOR UPDATE SKIP LOCKED;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
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

    private static async Task<DateTimeOffset> MarkPublishedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid eventId, CancellationToken ct)
    {
        // UPDATE(status, published_at) — the only mutating verb the babelstone_engine role
        // holds on outbox (0002_append_only_role.sql). RETURNING the DB-stamped published_at gives
        // the publish-lag SLI (§P4) its authoritative ack time without a second round-trip.
        const string sql = """
            UPDATE outbox
            SET status = 'PUBLISHED', published_at = clock_timestamp()
            WHERE event_id = @event_id
            RETURNING published_at;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("event_id", eventId);
        // GetFieldValue<DateTimeOffset> maps timestamptz the same way ReadRow does; ExecuteScalar
        // would surface a bare DateTime (the default CLR mapping) and lose the offset typing.
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return reader.GetFieldValue<DateTimeOffset>(0);
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
