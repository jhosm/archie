using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Avro;
using Avro.Generic;
using Avro.IO;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Babelstone.Notification.Delivery;

/// <summary>
/// The REAL EVENT_DRIVEN ingress: a Redpanda/Avro consumer of the engine's
/// <c>operations.NotificationDue</c> stream, replacing <see cref="NullNotificationDueSource"/> now the
/// producing leg exists (bd babelstone-60n8.7 / babelstone-60n8.11). In plain terms: the engine emits
/// <c>NotificationDue(trigger_kind=EVENT_DRIVEN)</c> onto the shared <c>operations</c> topic through its
/// outbox relay (ADR-PC-025 / ADR-IC-004); this source polls that topic, decodes each governed Avro
/// message into a <see cref="NotificationDueSignal"/>, and hands the batch to the delivery pass, which
/// drains it into the SAME outbox the SCHEDULED leg fills — one transport, parameterised by
/// <c>trigger_kind</c> (the 60n8.7 sharing requirement).
/// </summary>
/// <remarks>
/// <para>
/// <b>At-least-once, honestly.</b> The consumer runs with auto-commit OFF and commits its offsets only on
/// the NEXT poll — i.e. a full delivery tick AFTER a batch was handed out and the pass enqueued it into
/// the durable outbox. That is the <see cref="INotificationDueSource"/> "commit only after enqueued" rule
/// (ADR-IC-011 §P3 step 3): a crash between consume and enqueue re-presents the batch, and the outbox's
/// idempotent enqueue on the composite <c>notification_id</c> absorbs the duplicate (ADR-PC-025 slot 4).
/// </para>
/// <para>
/// <b>One topic, many record types.</b> <c>operations</c> is the shared cross-cutting topic — it also
/// carries <c>NotificationDeliveryExhausted</c> and any other <c>operations.*</c> event. Records are
/// discriminated by the CloudEvents <c>ce_type</c> header (ADR-IC-015), so this source decodes ONLY those
/// whose type ends in <c>NotificationDue</c>; anything else is skipped (its offset still advances, so a
/// foreign record never wedges the group). A malformed <c>NotificationDue</c> value is logged and skipped
/// as poison rather than throwing — an unavailable/garbled bus is ingress backpressure, never a stall of
/// the outbound retry drain.
/// </para>
/// <para>
/// <b>Schema posture (v1).</b> Decoding uses the embedded governed <c>NotificationDue.avsc</c> as BOTH
/// reader and writer schema — the same-version fast path, valid while the committed contract is the one on
/// the wire (the exact posture the exhausted relay's own round-trip proves). Confluent-registry
/// writer-schema resolution by embedded <c>schema_id</c> — the evolution-safe path the engine's inbox
/// consumer takes — is a named follow-up; until then a genuinely divergent writer schema surfaces as a
/// decode failure (poison-skipped), never a silent mis-read.
/// </para>
/// </remarks>
public sealed class KafkaNotificationDueSource : INotificationDueSource, IDisposable
{
    /// <summary>The synthetic cross-cutting aggregate_type — the topic this source subscribes to
    /// (topic == aggregate_type, the relay's documented convention; the same constant the exhausted
    /// producer publishes to).</summary>
    public const string Topic = "operations";

    /// <summary>The Avro record name the <c>ce_type</c> discriminator must end in for a message to be
    /// one of ours (the reverse-DNS type is <c>com.bank.operations.NotificationDue</c>).</summary>
    public const string RecordName = "NotificationDue";

    private const byte MagicByte = 0x00;

    private static readonly Lazy<RecordSchema> EmbeddedSchema = new(LoadEmbeddedSchema);

    private readonly IByteMessageConsumer _consumer;
    private readonly int _maxBatchSize;
    private readonly TimeSpan _consumeTimeout;
    private readonly ILogger<KafkaNotificationDueSource>? _logger;
    private readonly bool _ownsConsumer;

    // The offsets consumed on the LAST PollAsync, committed at the START of the NEXT one — the deferred
    // "commit only after enqueued" contract (see class remarks). Null until a batch has been consumed.
    private ConsumeResult<byte[], byte[]>? _pendingCommit;

    /// <summary>Production wiring: build a raw byte consumer subscribed to <see cref="Topic"/> with
    /// manual offset commit, from endpoint configuration. A consumer group id isolates this reader's
    /// offsets from every other <c>operations</c> consumer (e.g. the ACL erasure cascade).</summary>
    public KafkaNotificationDueSource(
        string bootstrapServers,
        string groupId,
        bool startFromEarliest = true,
        int maxBatchSize = 100,
        TimeSpan? consumeTimeout = null,
        ILogger<KafkaNotificationDueSource>? logger = null)
        : this(
            BuildConsumer(bootstrapServers, groupId, startFromEarliest),
            ownsConsumer: true,
            maxBatchSize,
            consumeTimeout,
            logger)
    {
    }

    /// <summary>Test wiring: an injected consumer seam (no broker), so the poll/decode/commit behaviour is
    /// unit-testable exactly like the exhausted publisher's producer seam.</summary>
    internal KafkaNotificationDueSource(
        IByteMessageConsumer consumer,
        bool ownsConsumer = false,
        int maxBatchSize = 100,
        TimeSpan? consumeTimeout = null,
        ILogger<KafkaNotificationDueSource>? logger = null)
    {
        _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
        _ownsConsumer = ownsConsumer;
        _maxBatchSize = maxBatchSize > 0
            ? maxBatchSize
            : throw new ArgumentOutOfRangeException(nameof(maxBatchSize), maxBatchSize, "must be positive.");
        _consumeTimeout = consumeTimeout ?? TimeSpan.FromMilliseconds(200);
        _logger = logger;
    }

    /// <summary>The embedded governed schema (parsed once) — exposed for round-trip tests.</summary>
    public static RecordSchema PayloadSchema => EmbeddedSchema.Value;

    /// <inheritdoc />
    public Task<IReadOnlyList<NotificationDueSignal>> PollAsync(CancellationToken ct = default)
    {
        // Commit the PREVIOUS batch's offsets first — only now, a full tick after we handed them to the
        // delivery pass, which enqueued them durably. Deferred commit + idempotent enqueue = at-least-once
        // with nothing lost between consume and record (INotificationDueSource contract / ADR-IC-011 §P3).
        CommitPending();

        var signals = new List<NotificationDueSignal>();
        ConsumeResult<byte[], byte[]>? last = null;

        for (var i = 0; i < _maxBatchSize; i++)
        {
            ct.ThrowIfCancellationRequested();

            var result = _consumer.Consume(_consumeTimeout);
            if (result is null)
            {
                // Nothing waiting within the poll window — a bounded, non-blocking batch (empty is normal).
                break;
            }

            last = result;

            var value = result.Message?.Value;
            if (value is null)
            {
                // A tombstone (compaction marker) — no obligation, but its offset must still advance.
                continue;
            }

            if (!IsNotificationDue(result.Message!.Headers))
            {
                // A different operations.* record sharing the topic — skip without decoding (a foreign
                // reader schema would mis-decode); the offset advances so it never wedges the group.
                continue;
            }

            if (TryDecode(value, out var signal))
            {
                signals.Add(signal);
            }
            else
            {
                _logger?.LogWarning(
                    "Skipped a NotificationDue message at {TopicPartitionOffset} that failed to decode "
                    + "(poison); its offset advances so it never wedges the group.",
                    result.TopicPartitionOffset);
            }
        }

        // Hold these offsets to commit next poll (see CommitPending). Null when the batch was empty.
        _pendingCommit = last;
        return Task.FromResult<IReadOnlyList<NotificationDueSignal>>(signals);
    }

    /// <summary>True iff the CloudEvents <c>ce_type</c> header names a <c>NotificationDue</c> record — the
    /// per-record discriminator on the shared <c>operations</c> topic (ADR-IC-015). Absent header ⇒ false
    /// (we only decode what announces itself as ours).</summary>
    public static bool IsNotificationDue(Headers? headers)
    {
        if (headers is null || !headers.TryGetLastBytes("ce_type", out var raw) || raw is null)
        {
            return false;
        }

        var ceType = Encoding.UTF8.GetString(raw);
        // Reverse-DNS type (com.bank.operations.NotificationDue) — match the final record-name segment,
        // so a namespace/vendor-prefix change never silently drops our own record.
        var lastDot = ceType.LastIndexOf('.');
        var recordName = lastDot >= 0 ? ceType[(lastDot + 1)..] : ceType;
        return string.Equals(recordName, RecordName, StringComparison.Ordinal);
    }

    /// <summary>Decode one Confluent-framed governed <c>NotificationDue</c> value (magic 0x00 ‖ big-endian
    /// schema_id ‖ Avro) into a <see cref="NotificationDueSignal"/>. Returns false on any framing/decode
    /// failure (poison) rather than throwing, so a single garbled record never aborts the batch.</summary>
    public static bool TryDecode(byte[] framedValue, out NotificationDueSignal signal)
    {
        signal = null!;
        try
        {
            if (!TryUnframe(framedValue, out var avroValue))
            {
                return false;
            }

            var schema = EmbeddedSchema.Value;
            using var stream = new MemoryStream(avroValue, writable: false);
            var reader = new GenericDatumReader<GenericRecord>(schema, schema);
            var record = reader.Read(default!, new BinaryDecoder(stream));
            signal = MapToSignal(record);
            return true;
        }
        catch (Exception)
        {
            // A malformed value, a foreign writer schema (v1 same-version posture), or an unexpected enum
            // symbol — all poison; the caller logs the skip. Deliberately broad: decode is the outer edge.
            return false;
        }
    }

    /// <summary>Project the decoded governed record onto the delivery-side CLR signal — the reader half of
    /// the <c>NotificationDue.avsc</c> contract, agreeing field-for-field with the consumer Pact
    /// (<c>NotificationDueMessagePactTests.BindLikeTheDeliveryEstate</c>).</summary>
    public static NotificationDueSignal MapToSignal(GenericRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var data = record["data"] is IDictionary<string, object> map
            ? map.ToDictionary(kv => kv.Key, kv => (string)kv.Value, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        return new NotificationDueSignal(
            NotificationId: (Guid)record["notification_id"],
            InstanceId: (Guid)record["instance_id"],
            // customer_id is a REQUIRED uuid on the wire (always present from the engine); carried as the
            // opaque recipient reference the renderer resolves at render time (ADR-PC-025 §PII).
            CustomerRef: record["customer_id"] is Guid customer ? customer : null,
            TemplateRef: (string)record["template_ref"],
            TemplatePackVersion: (string)record["template_pack_version"],
            TriggerKind: TriggerKindWire.FromWire(((GenericEnum)record["trigger_kind"]).Value),
            // causation_id is the [null, uuid] union — the causing domain event for EVENT_DRIVEN.
            CausationId: record["causation_id"] is Guid causation ? causation : null,
            Data: data,
            // due_at is an Avro date logical type (int days since epoch) — Apache.Avro surfaces a DateTime.
            DueAt: DateOnly.FromDateTime((DateTime)record["due_at"]));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_ownsConsumer)
        {
            return;
        }

        // A best-effort final commit of any handed-out batch (idempotent enqueue makes a double-deliver
        // harmless), then release the group membership so a restart resumes from the committed offset.
        try
        {
            CommitPending();
        }
        catch (KafkaException)
        {
            // Shutdown-time commit is best-effort; an unreachable broker here just means the next boot
            // re-reads from the last durable commit (at-least-once, absorbed by the outbox).
        }

        _consumer.Close();
        _consumer.Dispose();
    }

    private void CommitPending()
    {
        if (_pendingCommit is null)
        {
            return;
        }

        _consumer.Commit(_pendingCommit);
        _pendingCommit = null;
    }

    /// <summary>Strip the Confluent framing (magic byte 0x00 ‖ big-endian int32 schema_id) and return the
    /// bare Avro value. False when the buffer is too short or the magic byte is wrong — the inverse of the
    /// producer's <c>ToConfluentWireFormat</c>.</summary>
    private static bool TryUnframe(byte[] framed, out byte[] avroValue)
    {
        avroValue = [];
        if (framed is null || framed.Length < 5 || framed[0] != MagicByte)
        {
            return false;
        }

        // schema_id (framed[1..5], big-endian) is read past for the v1 same-version fast path — a
        // registry writer-schema lookup by this id is the evolution-safe follow-up.
        _ = BinaryPrimitives.ReadInt32BigEndian(framed.AsSpan(1, 4));
        avroValue = framed[5..];
        return true;
    }

    private static IByteMessageConsumer BuildConsumer(string bootstrapServers, string groupId, bool startFromEarliest)
    {
        if (string.IsNullOrWhiteSpace(bootstrapServers))
        {
            throw new ArgumentException("Kafka bootstrap servers are required.", nameof(bootstrapServers));
        }

        if (string.IsNullOrWhiteSpace(groupId))
        {
            throw new ArgumentException("A consumer group id is required.", nameof(groupId));
        }

        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = groupId,
            // Load-bearing: commit only after the batch is enqueued (deferred commit in PollAsync).
            EnableAutoCommit = false,
            AutoOffsetReset = startFromEarliest ? AutoOffsetReset.Earliest : AutoOffsetReset.Latest,
        };

        var consumer = new ConsumerBuilder<byte[], byte[]>(config).Build();
        consumer.Subscribe(Topic);
        return new ConfluentByteMessageConsumer(consumer);
    }

    private static RecordSchema LoadEmbeddedSchema()
    {
        var assembly = typeof(KafkaNotificationDueSource).Assembly;
        var resource = assembly.GetManifestResourceNames()
                .SingleOrDefault(name => name.EndsWith("NotificationDue.avsc", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "The embedded NotificationDue.avsc resource is missing — the governed schema must ride in "
                + "the assembly (check the .csproj EmbeddedResource).");

        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded resource '{resource}' could not be opened.");
        using var reader = new StreamReader(stream);
        return (RecordSchema)global::Avro.Schema.Parse(reader.ReadToEnd());
    }
}

/// <summary>
/// The narrow consume/commit seam <see cref="KafkaNotificationDueSource"/> drives — the three operations
/// it needs off a raw byte consumer. Production adapts a Confluent <c>IConsumer&lt;byte[], byte[]&gt;</c>
/// (<see cref="ConfluentByteMessageConsumer"/>); tests supply an in-memory double, so the poll/decode/
/// deferred-commit behaviour is exercised with no broker (the same fake-at-the-transport-seam technique as
/// the rest of this estate).
/// </summary>
internal interface IByteMessageConsumer : IDisposable
{
    /// <summary>Poll for the next record, up to <paramref name="timeout"/>; null when none arrived.</summary>
    ConsumeResult<byte[], byte[]>? Consume(TimeSpan timeout);

    /// <summary>Commit the offset of <paramref name="result"/> (i.e. mark it consumed).</summary>
    void Commit(ConsumeResult<byte[], byte[]> result);

    /// <summary>Leave the group and release resources.</summary>
    void Close();
}

/// <summary>The production seam: a thin pass-through to the real Confluent consumer.</summary>
internal sealed class ConfluentByteMessageConsumer(IConsumer<byte[], byte[]> inner) : IByteMessageConsumer
{
    private readonly IConsumer<byte[], byte[]> _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public ConsumeResult<byte[], byte[]>? Consume(TimeSpan timeout) => _inner.Consume(timeout);

    public void Commit(ConsumeResult<byte[], byte[]> result) => _inner.Commit(result);

    public void Close() => _inner.Close();

    public void Dispose() => _inner.Dispose();
}
