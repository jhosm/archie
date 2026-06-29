using System.Buffers.Binary;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using Babelstone.EventStore;
using Babelstone.Telemetry;
using Confluent.Kafka;
using Npgsql;

namespace Babelstone.OutboxPublisher;

/// <summary>
/// The IC-004 polling relay. One <see cref="DrainOnceAsync"/> call:
/// SELECTs PENDING outbox rows in drain order, claiming each aggregate with a per-aggregate
/// advisory lock and each row <c>FOR UPDATE SKIP LOCKED</c> (so concurrent relay instances claim
/// disjoint rows — never the same row twice, and never concurrent in-flight rows for the same
/// <c>aggregate_id</c> — ADR-IC-004 §P2 / §Residual-risks), builds the Confluent wire-format value
/// from the row's embedded <c>schema_id</c> (NO Schema-Registry lookup — ADR-IC-004 §P3), sets the
/// CloudEvents Binary-mode Kafka headers (ADR-IC-015), produces keyed by <c>aggregate_id</c> to a
/// topic named after <c>aggregate_type</c>, flips the row to PUBLISHED (the only verbs the engine
/// role holds on outbox), and records the per-row publish-latency histogram on the shared meter.
/// The §P4 publish-lag SLI itself — the gauge of the oldest PENDING row's age — is emitted by
/// <see cref="OutboxLagObserver"/>, which keeps reporting during an outage when nothing publishes.
/// </summary>
/// <remarks>
/// The read + publish + flip run in ONE transaction: the per-aggregate advisory locks and the
/// <c>FOR UPDATE</c> row locks are held until commit, so a second concurrent drainer's read steps
/// over this drainer's in-flight rows (SKIP LOCKED) AND over any aggregate this drainer already
/// holds (the advisory lock is what makes cross-instance per-aggregate order a §P2 hard guarantee,
/// not just per-row no-double-publish). The §Residual-risks dual-publish window shrinks to a crash
/// between produce-ack and commit, which consumer-inbox idempotency absorbs. On Redpanda
/// unavailability the produce throws, the transaction rolls back, the rows stay PENDING (NEVER
/// FAILED — ADR-IC-004 §P7), and the hosted loop backs off.
/// </remarks>
public sealed class OutboxDrainer : IAsyncDisposable
{
    // Confluent wire format (ADR-IC-002 §P3 / ADR-IC-004 §P3): magic byte 0x00, then the
    // 4-byte big-endian schema_id, then the bare Avro value the codec produced.
    private const byte MagicByte = 0x00;

    // The per-row publish-LATENCY histogram on the shared Babelstone meter (ADR-IC-007 Layer 1): one
    // histogram of seconds-from-enqueue-to-ack, tagged by aggregate_type. This is an ADDITION, NOT
    // the §P4 SLI — that is the oldest-PENDING-row gauge in OutboxLagObserver, which (unlike a per-row
    // metric) keeps reporting during an outage. A host turns this on with
    // AddMeter(BabelstoneTelemetry.MeterName); with no listener Record is a near no-op. The lag value
    // is computed single-clock in the DB (published_at − created_at, both DB-stamped) so host/DB clock
    // skew cannot bias or negate it.
    private static readonly Histogram<double> PublishLatencySeconds =
        BabelstoneTelemetry.Meter.CreateHistogram<double>(
            BabelstoneAttributes.OutboxPublishLatencyMetric,
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
            // Order is preserved by draining and producing one row at a time per aggregate, with a
            // per-aggregate advisory lock keeping a second drainer off the same aggregate (ADR-IC-004
            // §P2). EnableIdempotence keeps the broker from reordering this producer's retries.
            var config = new ProducerConfig
            {
                BootstrapServers = options.BootstrapServers,
                EnableIdempotence = true,
                Acks = Acks.All,
            };
            // ADR-IC-016 plane ii: present the relay's distinct SASL/SCRAM identity to Redpanda when a
            // credential is configured (resolved by the host through ISecretProvider). A no-op in local
            // dev where no credential is supplied — additive, leaving idempotence/acks untouched.
            options.Sasl.ApplyTo(config);
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
    /// moving to the next — per-aggregate FIFO by construction, held across instances by the
    /// per-aggregate advisory lock the read takes (ADR-IC-004 §P2).
    /// </summary>
    /// <remarks>
    /// A produce failure rolls the whole transaction back, leaving every row in the batch PENDING for
    /// the next cycle (ADR-IC-004 §P7) — no row is ever marked PUBLISHED without an ack. The §P2
    /// concurrency seam that makes the read claim disjoint rows is detailed where the SQL takes it.
    /// </remarks>
    /// <remarks>
    /// One transaction holds the row + advisory locks across the batch's synchronous produce-and-ack
    /// round-trips, so <see cref="OutboxRelayOptions.BatchSize"/> governs the worst-case lock-hold
    /// time: a slow/backpressured Redpanda lengthens it. At banking volumes (§S1) and the default
    /// batch this is acceptable; tune BatchSize down if the §S1 polling-query SLA is at risk.
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
            var latencySeconds = await MarkPublishedAsync(connection, transaction, row.EventId, ct);
            RecordPublishLatency(row, latencySeconds);
            published++;
        }

        // Commit releases the FOR UPDATE locks and makes the PUBLISHED flips visible together. If a
        // produce above threw, we never reach here — the using-dispose rolls back and the rows stay
        // PENDING (ADR-IC-004 §P7), so no row is ever lost or marked PUBLISHED without an ack.
        await transaction.CommitAsync(ct);
        return published;
    }

    /// <summary>
    /// Records the per-row publish-latency histogram for one row (an addition, NOT the §P4 SLI):
    /// the seconds between the row's enqueue (<c>created_at</c>) and its publish ack
    /// (<c>published_at</c>), tagged by <c>aggregate_type</c> so latency is breakable by topic. The
    /// value is computed single-clock in the DB by <see cref="MarkPublishedAsync"/> so host/DB clock
    /// skew cannot bias it.
    /// </summary>
    private static void RecordPublishLatency(OutboxRow row, double latencySeconds)
    {
        PublishLatencySeconds.Record(
            latencySeconds,
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

    // CloudEvents 1.0 Binary Content Mode (ADR-IC-015): attributes as Kafka headers, the Avro
    // value as the message value. Every header here is derivable from the outbox row alone.
    private Headers BuildHeaders(OutboxRow row) => BuildHeadersCore(row, _options.Source);

    // The pure header transform, lifted to internal static so it is unit-testable without a producer
    // or DB (mirrors ReverseDnsType / ToConfluentWireFormat). The only instance input is the source
    // URI, passed explicitly.
    internal static Headers BuildHeadersCore(OutboxRow row, string source)
    {
        var headers = new Headers();
        Add(headers, "ce_specversion", "1.0");
        Add(headers, "ce_id", row.EventId.ToString());
        Add(headers, "ce_source", source);
        Add(headers, "ce_type", ReverseDnsType(row.EventType));
        Add(headers, "ce_time", row.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        Add(headers, "ce_datacontenttype", "application/avro");
        Add(headers, "ce_subject", row.AggregateId.ToString());
        Add(headers, "ce_aggregatetype", row.AggregateType);
        // The family-declared CloudEvents extension attributes (ADR-IC-018 §P5): each entry the event
        // declared (via DomainEvent.IntegrationHeaders, persisted on the row's integration_headers
        // column) becomes a ce_<key> header, e.g. autorenewalpolicy -> ce_autorenewalpolicy. The relay
        // names no key — it copies whatever the event declared, so the seam is family-agnostic. Still
        // derivable from the outbox row alone (ADR-IC-004): the column IS on the row.
        if (row.IntegrationHeaders is { } extensions)
        {
            foreach (var (key, value) in extensions)
            {
                Add(headers, $"ce_{key}", value);
            }
        }

        return headers;
    }

    private static void Add(Headers headers, string key, string value)
        => headers.Add(key, System.Text.Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// Reverse-DNS CloudEvents type (ADR-IC-015): "term_deposit.DepositConstituted" →
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
        //
        // Two locks, two distinct §P2 jobs:
        //
        //   • pg_try_advisory_xact_lock(hashtextextended(aggregate_id::text, 0)) — the §P2
        //     "lock granularity that prevents concurrent in-flight rows for the same aggregate_id".
        //     SKIP LOCKED alone would let drainer A hold seq 1 of aggregate X while drainer B claims
        //     the still-unlocked seq 2 and produces it FIRST → out-of-order on X's partition. The
        //     per-aggregate advisory lock closes that gap: a row is a candidate only if THIS
        //     transaction can take its aggregate's lock, so no two drainers ever hold in-flight rows
        //     for the same aggregate. The lock is transaction-scoped (released at commit/rollback),
        //     re-entrant within the transaction (claiming several rows of one aggregate re-takes the
        //     same key harmlessly), and pg_try_* is non-blocking so a contended aggregate is skipped,
        //     not waited on (no lock-wait, no deadlock).
        //
        //   • FOR UPDATE SKIP LOCKED (ADR-IC-004 §P2 / §Residual-risks) — the row-level
        //     HA-coordination seam: each returned row is row-locked for the open transaction so a
        //     concurrent drainer steps over it, claiming a disjoint set — competing instances never
        //     select the same PENDING row, so they cannot double-publish.
        //
        // The advisory lock is evaluated inside the candidate CTE's WHERE, so an aggregate another
        // drainer already holds is filtered out BEFORE the LIMIT — the batch fills with the oldest
        // claimable rows rather than starving on a contended leading aggregate. The outer SELECT then
        // takes the row locks (FOR UPDATE SKIP LOCKED) on those candidates in drain order.
        const string sql = """
            WITH candidate AS (
                SELECT event_id
                FROM outbox
                WHERE status = 'PENDING'
                  AND pg_try_advisory_xact_lock(hashtextextended(aggregate_id::text, 0))
                ORDER BY created_at, sequence_number
                LIMIT @batch_size
            )
            SELECT o.event_id, o.aggregate_type, o.aggregate_id, o.sequence_number, o.event_type,
                   o.payload, o.schema_id, o.status, o.created_at, o.published_at, o.integration_headers
            FROM outbox o
            JOIN candidate c ON c.event_id = o.event_id
            WHERE o.status = 'PENDING'
            ORDER BY o.created_at, o.sequence_number
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
        PublishedAt: r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9),
        // The family-declared CloudEvents extension attributes (ADR-IC-018 §P5), read back from the
        // integration_headers JSONB column. NULL (the common case, every pre-seam row) → no extension
        // headers; BuildHeaders then emits the standard CE set only.
        IntegrationHeaders: r.IsDBNull(10)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetFieldValue<string>(10)));

    private static async Task<double> MarkPublishedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid eventId, CancellationToken ct)
    {
        // UPDATE(status, published_at) — the only mutating verb the babelstone_engine role holds on
        // outbox (0002_append_only_role.sql). RETURNING the publish-latency computed IN THE DB
        // (published_at − created_at, both DB-clock) gives the per-row latency histogram a single-clock
        // value — no app-vs-DB clock subtraction — without a second round-trip.
        const string sql = """
            UPDATE outbox
            SET status = 'PUBLISHED', published_at = clock_timestamp()
            WHERE event_id = @event_id
            RETURNING EXTRACT(EPOCH FROM (published_at - created_at));
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("event_id", eventId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            // The row we locked and produced for must still exist to be flipped; no row back means
            // a contract violation (a concurrent delete of a PENDING row the relay holds), not a
            // routine case — fail loud rather than silently record a bogus latency.
            throw new InvalidOperationException(
                $"Outbox row {eventId} vanished before it could be marked PUBLISHED.");
        }

        return reader.GetFieldValue<double>(0);
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
