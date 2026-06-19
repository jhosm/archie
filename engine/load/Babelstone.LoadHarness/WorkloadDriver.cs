using System.Diagnostics;
using Babelstone.Engine;
using Babelstone.Engine.Avro;
using Confluent.Kafka;

namespace Babelstone.LoadHarness;

/// <summary>
/// The §P1 DRIVER: a rate-scheduled <c>Confluent.Kafka</c> producer that realises the generator's
/// stream onto Redpanda at the target TPS / peak shape (ADR-PC-011 §P1). It encodes each synthetic
/// event with the engine's OWN <see cref="AvroEventSerializer"/> (the exact production value bytes,
/// §G1), frames it in the Confluent wire format, attaches the CloudEvents Binary-mode headers
/// (ADR-IC-015), and produces keyed by <c>partition_key</c> so per-partition delivery order matches
/// event-store order (§8.3 reliability invariant).
/// </summary>
/// <remarks>
/// <para>
/// In plain English: this is the part that actually pushes the synthetic events onto the message bus,
/// paced to the right number per second. It uses the engine's real serializer so the bytes are
/// indistinguishable from production traffic — which is the whole point of an in-house harness
/// (ADR-PC-011 §S2): no separate test encoder that could drift from the real one.
/// </para>
/// <para>
/// Throughput (§P3) runs against REAL wall-clock — this driver paces emission to the target TPS with a
/// token-bucket-style sleep; the simulated clock governs only DOMAIN time, not emission rate. A caller
/// supplies the <see cref="IProducer{TKey,TValue}"/> (so a smoke test can inject an in-memory/mock
/// producer) or lets the driver build the same idempotent, acks-all producer the relay uses.
/// </para>
/// </remarks>
public sealed class WorkloadDriver : IAsyncDisposable
{
    private readonly AvroEventSerializer _serializer;
    private readonly AvroSchemaCatalog _catalog;
    private readonly IProducer<byte[], byte[]> _producer;
    private readonly bool _ownsProducer;
    private readonly string _source;

    /// <summary>
    /// Creates a driver. If <paramref name="producer"/> is null a producer is built from
    /// <paramref name="bootstrapServers"/> with the SAME EnableIdempotence + Acks.All config the outbox
    /// relay uses (so the broker never reorders this producer's retries — the §8.3 ordering invariant).
    /// </summary>
    public WorkloadDriver(
        AvroEventSerializer serializer,
        AvroSchemaCatalog catalog,
        string? bootstrapServers = null,
        IProducer<byte[], byte[]>? producer = null,
        string source = "urn:babelstone:loadharness")
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _source = source;

        if (producer is not null)
        {
            _producer = producer;
            _ownsProducer = false;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(bootstrapServers))
            {
                throw new ArgumentException(
                    "Either an IProducer or a non-empty bootstrapServers must be supplied.", nameof(bootstrapServers));
            }

            var config = new ProducerConfig
            {
                BootstrapServers = bootstrapServers,
                EnableIdempotence = true,
                Acks = Acks.All,
            };
            _producer = new ProducerBuilder<byte[], byte[]>(config).Build();
            _ownsProducer = true;
        }
    }

    /// <summary>
    /// Builds the production-shaped Kafka message for one synthetic event: the engine's Avro value
    /// bytes framed in Confluent wire format, keyed by <c>partition_key</c>, with the CloudEvents
    /// headers — exactly the shape the outbox relay produces. Exposed so the smoke test can assert the
    /// bytes/headers without a broker.
    /// </summary>
    internal (string Topic, Message<byte[], byte[]> Message) BuildMessage(SyntheticEvent synthetic)
    {
        // Encode with the ENGINE's own serializer — the drift-free production value bytes (§G1).
        EncodedPayload encoded = _serializer.Encode(synthetic.Event);

        var entry = _catalog.ForRecordName(synthetic.Event.GetType().Name);
        var eventType = entry.EventType;                                  // e.g. "term_deposit.DepositConstituted"
        var aggregateType = AggregateTypeOf(eventType);                  // e.g. "term_deposit" (the topic)

        var message = new Message<byte[], byte[]>
        {
            // Key == partition_key so per-partition_key delivery order matches event-store order (§8.3).
            Key = synthetic.PartitionKey.ToByteArray(),
            Value = WireFormat.ToConfluentWireFormat(encoded.SchemaId, encoded.Bytes),
            Headers = WireFormat.BuildCloudEventHeaders(
                eventId: Guid.NewGuid(),
                source: _source,
                eventType: eventType,
                aggregateType: aggregateType,
                partitionKey: synthetic.PartitionKey,
                time: synthetic.EmitInstant,
                extensionHeaders: synthetic.Event.IntegrationHeaders),
        };

        return (aggregateType, message);
    }

    /// <summary>
    /// Produces one synthetic event and awaits the broker ack (the relay's per-aggregate FIFO posture:
    /// produce + ack before the next). The publish-confirm time is NOT the latency metric — that is read
    /// from the engine's OTel spans by the <see cref="LatencyObserver"/> (§P2 / §G2).
    /// </summary>
    public async Task ProduceAsync(SyntheticEvent synthetic, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(synthetic);
        var (topic, message) = BuildMessage(synthetic);
        await _producer.ProduceAsync(topic, message, ct);
    }

    /// <summary>
    /// Drives a stream onto the bus paced to <paramref name="targetTps"/> against real wall-clock
    /// (§P3 — throughput is a wall-clock dimension; the simulated clock governs domain time only).
    /// Returns the number of events produced. The pacing is a simple inter-event sleep; at the §8.3
    /// 250/1000 TPS this is a single-well-sized-producer workload (ADR-PC-011 §S1), no distributed
    /// control plane needed.
    /// </summary>
    public async Task<long> DriveAsync(
        IEnumerable<SyntheticEvent> stream, double targetTps, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (targetTps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetTps), targetTps, "Target TPS must be positive.");
        }

        var intervalTicks = (long)(Stopwatch.Frequency / targetTps);
        var produced = 0L;
        var clock = Stopwatch.StartNew();
        var nextDue = clock.ElapsedTicks;

        foreach (var synthetic in stream)
        {
            ct.ThrowIfCancellationRequested();

            var wait = nextDue - clock.ElapsedTicks;
            if (wait > 0)
            {
                var ms = wait * 1000.0 / Stopwatch.Frequency;
                await Task.Delay(TimeSpan.FromMilliseconds(ms), ct);
            }

            await ProduceAsync(synthetic, ct);
            produced++;
            nextDue += intervalTicks;
        }

        return produced;
    }

    // The topic / aggregate_type is the segment of the event_type before the dot
    // ("term_deposit.DepositConstituted" → "term_deposit"), matching the relay's
    // topic-named-after-aggregate_type convention (ADR-IC-004 §Consequences).
    private static string AggregateTypeOf(string eventType)
    {
        var dot = eventType.IndexOf('.', StringComparison.Ordinal);
        return dot >= 0 ? eventType[..dot] : eventType;
    }

    /// <summary>Flushes in-flight produces and disposes the producer if the driver owns it.</summary>
    public ValueTask DisposeAsync()
    {
        if (_ownsProducer)
        {
            _producer.Flush(TimeSpan.FromSeconds(10));
            _producer.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
