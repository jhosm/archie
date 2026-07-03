using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using Avro;
using Avro.Generic;
using Avro.IO;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using ConfluentSchema = Confluent.SchemaRegistry.Schema;

namespace Babelstone.Notification.Delivery;

/// <summary>
/// The backbone-publish seam of the §D4 exhaustion relay (ADR-IC-011 §P3 step 7): publish ONE
/// <c>operations.NotificationDeliveryExhausted</c> event and return only on broker ack. A throw is
/// BACKPRESSURE — the relay pass leaves the row PENDING and retries next tick (never FAILED,
/// ADR-IC-004). A seam (not the Confluent producer directly) so the relay's ordering/flip behaviour is
/// unit-testable without a broker — the same posture as every other transport seam in this estate.
/// </summary>
public interface IExhaustedEventPublisher
{
    /// <summary>Publish one exhausted-delivery announcement; returns on broker ack, throws on failure.</summary>
    Task PublishAsync(ExhaustedDelivery exhausted, CancellationToken ct = default);
}

/// <summary>
/// The production <see cref="IExhaustedEventPublisher"/>: the governed Avro payload
/// (<c>contracts/avro/operations/NotificationDeliveryExhausted.avsc</c>, embedded in this assembly so a
/// deploy carries its own schema) in Confluent wire format (magic byte <c>0x00</c> ‖ big-endian
/// <c>schema_id</c> ‖ Avro value), keyed by <c>instance_id</c>, with CloudEvents 1.0 binary-mode Kafka
/// headers, produced to the <c>operations</c> topic (topic == aggregate_type, the relay convention) —
/// byte-for-byte the posture of the engine's <c>OutboxDrainer</c>, self-contained here because the
/// notification subtree takes no engine-spine reference (ADR-IC-019 §P2, NOTIFICATION_FAMILY_AGNOSTIC).
/// </summary>
/// <remarks>
/// The <c>schema_id</c> is resolved against the Schema Registry ONCE, lazily, on the first publish
/// (register-if-absent by default — the same ADR-IC-002 walking-skeleton convenience as the engine's
/// <c>ConfluentSchemaIdResolver</c>; production registers in CI and flips <c>registerIfAbsent</c> off
/// for pure lookup). An unreachable SR at publish time therefore throws — backpressure, rows stay
/// PENDING — and never blocks host boot.
/// </remarks>
public sealed class KafkaExhaustedEventPublisher : IExhaustedEventPublisher, IAsyncDisposable
{
    /// <summary>The synthetic cross-cutting aggregate_type — the topic AND the ce_aggregatetype
    /// (event-store §4.3; topic == aggregate_type, the relay's documented convention).</summary>
    public const string Topic = "operations";

    /// <summary>The Schema-Registry subject (ADR-IC-002: fully-qualified record name + -value).</summary>
    public const string Subject = "operations.NotificationDeliveryExhausted-value";

    /// <summary>The producing service's CloudEvents source URI — the notification estate, never the
    /// engine (the same identity the SCHEDULED NotificationDue catalogue entry names).</summary>
    public const string Source = "urn:babelstone:notification";

    /// <summary>Reverse-DNS CloudEvents type (ADR-IC-015), mirroring the catalogue entry.</summary>
    public const string CloudEventType = "com.bank.operations.NotificationDeliveryExhausted";

    private const byte MagicByte = 0x00;

    private static readonly Lazy<RecordSchema> EmbeddedSchema = new(LoadEmbeddedSchema);

    private readonly ISchemaRegistryClient _schemaRegistry;
    private readonly IProducer<byte[], byte[]> _producer;
    private readonly bool _registerIfAbsent;
    private readonly bool _ownsClients;
    private readonly SemaphoreSlim _schemaIdGate = new(1, 1);
    private int? _schemaId;

    /// <summary>Production wiring: own producer + own SR client from endpoint configuration.</summary>
    public KafkaExhaustedEventPublisher(string bootstrapServers, string schemaRegistryUrl, bool registerIfAbsent = true)
        : this(
            new ProducerBuilder<byte[], byte[]>(new ProducerConfig
            {
                BootstrapServers = string.IsNullOrWhiteSpace(bootstrapServers)
                    ? throw new ArgumentException("Kafka bootstrap servers are required.", nameof(bootstrapServers))
                    : bootstrapServers,
                // The same delivery discipline as the engine relay: idempotent producer, full acks —
                // the broker never reorders this producer's retries, an ack means replicated.
                EnableIdempotence = true,
                Acks = Acks.All,
            }).Build(),
            new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = schemaRegistryUrl }),
            registerIfAbsent,
            ownsClients: true)
    {
    }

    /// <summary>Test wiring: injected producer + SR client (e.g. mocks / Testcontainers).</summary>
    public KafkaExhaustedEventPublisher(
        IProducer<byte[], byte[]> producer,
        ISchemaRegistryClient schemaRegistry,
        bool registerIfAbsent = true,
        bool ownsClients = false)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        _schemaRegistry = schemaRegistry ?? throw new ArgumentNullException(nameof(schemaRegistry));
        _registerIfAbsent = registerIfAbsent;
        _ownsClients = ownsClients;
    }

    /// <inheritdoc />
    public async Task PublishAsync(ExhaustedDelivery exhausted, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exhausted);

        var schemaId = await ResolveSchemaIdAsync(ct);
        var message = new Message<byte[], byte[]>
        {
            // Partition by instance_id — the same keying as every operations-stream event, so
            // per-instance order is a partition-order guarantee.
            Key = exhausted.InstanceId.ToByteArray(),
            Value = ToConfluentWireFormat(schemaId, EncodeAvro(exhausted)),
            Headers = BuildHeaders(exhausted),
        };

        await _producer.ProduceAsync(Topic, message, ct);
    }

    /// <summary>magic byte 0x00 ‖ big-endian int32 schema_id ‖ avro value (ADR-IC-002 / ADR-IC-004).</summary>
    public static byte[] ToConfluentWireFormat(int schemaId, ReadOnlySpan<byte> avroValue)
    {
        var framed = new byte[5 + avroValue.Length];
        framed[0] = MagicByte;
        BinaryPrimitives.WriteInt32BigEndian(framed.AsSpan(1, 4), schemaId);
        avroValue.CopyTo(framed.AsSpan(5));
        return framed;
    }

    /// <summary>The governed Avro value bytes for one exhausted delivery — the embedded
    /// <c>NotificationDeliveryExhausted.avsc</c> is the single source of shape.</summary>
    public static byte[] EncodeAvro(ExhaustedDelivery exhausted)
    {
        var schema = EmbeddedSchema.Value;
        var record = new GenericRecord(schema);
        record.Add("notification_id", exhausted.NotificationId);
        record.Add("instance_id", exhausted.InstanceId);
        record.Add("customer_id", exhausted.CustomerRef.HasValue ? exhausted.CustomerRef.Value : null);
        record.Add("template_ref", exhausted.TemplateRef);
        record.Add("template_pack_version", exhausted.TemplatePackVersion);
        record.Add("trigger_kind", new GenericEnum(
            (EnumSchema)schema["trigger_kind"].Schema, TriggerKindWire.ToWire(exhausted.TriggerKind)));
        record.Add("attempts", exhausted.Attempts);
        record.Add("last_error", exhausted.LastError);
        record.Add("exhausted_at", exhausted.ExhaustedAt.UtcDateTime);

        using var stream = new MemoryStream();
        var writer = new GenericDatumWriter<GenericRecord>(schema);
        writer.Write(record, new BinaryEncoder(stream));
        return stream.ToArray();
    }

    /// <summary>The embedded governed schema (parsed once) — exposed for round-trip tests.</summary>
    public static RecordSchema PayloadSchema => EmbeddedSchema.Value;

    // CloudEvents 1.0 Binary Content Mode (ADR-IC-015): attributes as Kafka headers, the Avro value
    // as the message value — every header derivable from the outbox row alone (ADR-IC-004). ce_id is
    // the row's DB-generated event_id, so a relay retry republishes the SAME id.
    public static Headers BuildHeaders(ExhaustedDelivery exhausted)
    {
        var headers = new Headers();
        Add(headers, "ce_specversion", "1.0");
        Add(headers, "ce_id", exhausted.EventId.ToString());
        Add(headers, "ce_source", Source);
        Add(headers, "ce_type", CloudEventType);
        Add(headers, "ce_time", exhausted.ExhaustedAt.ToString("O", CultureInfo.InvariantCulture));
        Add(headers, "ce_datacontenttype", "application/avro");
        Add(headers, "ce_subject", exhausted.InstanceId.ToString());
        Add(headers, "ce_aggregatetype", Topic);
        return headers;
    }

    public async ValueTask DisposeAsync()
    {
        _schemaIdGate.Dispose();
        if (_ownsClients)
        {
            // Flush pending acks bounded, then release the sockets.
            await Task.Run(() => _producer.Flush(TimeSpan.FromSeconds(10)));
            _producer.Dispose();
            _schemaRegistry.Dispose();
        }
    }

    private static void Add(Headers headers, string key, string value)
        => headers.Add(key, System.Text.Encoding.UTF8.GetBytes(value));

    private static RecordSchema LoadEmbeddedSchema()
    {
        var assembly = typeof(KafkaExhaustedEventPublisher).Assembly;
        var resource = assembly.GetManifestResourceNames()
                .SingleOrDefault(name => name.EndsWith("NotificationDeliveryExhausted.avsc", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "The embedded NotificationDeliveryExhausted.avsc resource is missing — the governed "
                + "schema must ride in the assembly (check the .csproj EmbeddedResource).");

        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded resource '{resource}' could not be opened.");
        using var reader = new StreamReader(stream);
        return (RecordSchema)global::Avro.Schema.Parse(reader.ReadToEnd());
    }

    private async Task<int> ResolveSchemaIdAsync(CancellationToken ct)
    {
        if (_schemaId is { } cached)
        {
            return cached;
        }

        await _schemaIdGate.WaitAsync(ct);
        try
        {
            if (_schemaId is { } resolved)
            {
                return resolved;
            }

            // Register-if-absent is idempotent (an identical schema returns the existing id); pure
            // lookup is the production posture once CI owns registration (ADR-IC-002).
            var schema = new ConfluentSchema(EmbeddedSchema.Value.ToString(), SchemaType.Avro);
            var id = _registerIfAbsent
                ? await _schemaRegistry.RegisterSchemaAsync(Subject, schema)
                : await _schemaRegistry.GetSchemaIdAsync(Subject, schema);
            _schemaId = id;
            return id;
        }
        finally
        {
            _schemaIdGate.Release();
        }
    }
}
