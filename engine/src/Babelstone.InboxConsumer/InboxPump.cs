using System.Buffers.Binary;
using System.Diagnostics.Metrics;
using System.Text;
using Babelstone.Engine;
using Babelstone.Engine.Avro;
using Babelstone.Telemetry;
using Confluent.Kafka;
using Npgsql;

namespace Babelstone.InboxConsumer;

/// <summary>
/// The IC-004 consumer half (Document 04 "Inbox Pattern"): the MIRROR of <c>OutboxDrainer</c>. One
/// <see cref="PumpOnceAsync"/> call consumes the next Redpanda record, un-frames the Confluent
/// wire-format value (magic byte ‖ big-endian schema_id ‖ Avro), decodes the Avro via the codec,
/// then in ONE PostgreSQL transaction INSERTs the <c>message_id</c> dedup row and runs the handler —
/// committing the Kafka offset only after the DB transaction commits.
/// </summary>
/// <remarks>
/// <para>
/// <b>Schema resolution (ADR-IC-002):</b> the decode resolves the
/// WRITER schema from the embedded wire-format <c>schema_id</c> via the Schema Registry (an
/// <see cref="ISchemaByIdResolver"/> with a client-side cache) and reads writer→reader through Avro
/// schema resolution (the <c>GenericDatumReader</c> gets BOTH the writer and this consumer's local
/// reader schema). This is the consumer contract ADR-IC-002 prescribes — the wire-format schema id is
/// meaningless without the registry, so the consumer resolves it there. It is the OPPOSITE of
/// ADR-IC-004's "no SR lookup" optimisation, which is a PUBLISH-path guarantee only. CROSS-CONTEXT FORWARD/BACKWARD
/// EVOLUTION now decodes correctly: a producer on a NEWER, BACKWARD-compatible writer schema (an
/// additive field under a default; BACKWARD is the registry compatibility default) embeds a
/// different id; resolving it lets the codec drop a writer-only field and default a reader-only one,
/// instead of mis-decoding → poison. An unknown/unresolvable id is undecodable and routes to the poison
/// path (skip-and-commit), never a silent mis-decode. When NO resolver is wired (a unit test, or a
/// deployment that has not yet supplied an SR), the decode falls back to the writer == reader fast path
/// — correct for same-version intra-context topics — so the consumer degrades safely rather than
/// failing closed.
/// </para>
/// <para>
/// <b>Scope — writer→reader SR resolution is a BUS concern only:</b> it is wired here, on the
/// InboxConsumer bus-consume path, because the bus wire format is Avro (ADR-IC-002). The event-store
/// REPLAY / projection-rebuild path (AggregateRuntime, ProjectionRunner, ProjectionReconciler,
/// SimulationRuntime, ReadModelRunner) needs <b>no</b> writer-schema resolution at all: the
/// <c>events.payload</c> is self-describing JSON (ADR-PC-028), decodable with no Schema Registry, so
/// replay never resolves against a writer schema. <c>EventEnvelope.PayloadSchemaId</c> is the outbound
/// Avro encoding's id (a bus cross-reference, ADR-IC-004), not a replay-decode key. (This supersedes
/// the earlier "deferred follow-up" framing: ADR-PC-028 decided the store stays JSON, obsoleting the
/// replay-side Avro-resolution work.)
/// </para>
/// <para>
/// <b>Dedup (Document 04 / ADR-IC-004 "mandatory, not optional"):</b> the inbox PK
/// on <c>message_id</c> is the dedup mechanism. A duplicate physical delivery (the dual-publish window
/// the outbox leaves open, or a Kafka rebalance replay) collides on the PK; the INSERT
/// throws a unique-violation, the transaction rolls back, and the loop treats it as "already processed
/// → commit the offset and move on". No business effect runs twice — effectively-once.
/// </para>
/// <para>
/// <b>Offset ⇄ transaction ordering:</b> the DB transaction commits FIRST, the offset SECOND. A crash
/// between them redelivers the record, the dedup INSERT collides, and the offset advances on the retry
/// — at-least-once delivery, effectively-once effect. The reverse (commit offset first) would risk
/// losing a message whose effect had not committed. Auto-commit is OFF for exactly this reason.
/// </para>
/// <para>
/// <b>Poison message:</b> a record that cannot be turned into an <see cref="InboxMessage"/> — bad wire
/// framing, an unknown event type, an undecodable Avro value, or a missing <c>ce_id</c> — can never be
/// retried into correctness, so blocking the partition on it would stall every well-formed message
/// behind it. The pump records the poison counter, invokes the optional poison sink (a host's
/// dead-letter seam), and commits the offset to step past it. This is a deliberate consumer-side
/// policy, distinct from a handler EXCEPTION (a transient failure) which rolls back and is redelivered.
/// A null-payload TOMBSTONE is a THIRD, distinct path (see below): it is not poison and not redelivered.
/// </para>
/// <para>
/// <b>Tombstone (GDPR erasure):</b> a record with a key but a null/empty value is Redpanda log
/// compaction's right-to-erasure signal on a <c>cleanup.policy=compact</c> topic (ADR-IC-001 /
/// ADR-IC-002). It is recognised BEFORE the Avro decode — never deserialised as Avro — and skipped
/// past on its OWN counter, deliberately NOT the poison counter, so a routine crypto-shred upstream
/// never raises a false dead-letter alert. A handler that owns projection state plugs the actual
/// erasure in via the saga seam; this dedup-only assembly's contract-honouring action is
/// skip-and-commit.
/// </para>
/// </remarks>
public sealed class InboxPump : IDisposable
{
    // Confluent wire format (ADR-IC-002 / ADR-IC-004): magic byte 0x00, then the 4-byte
    // big-endian schema_id, then the bare Avro value — the exact framing OutboxDrainer produces.
    private const byte MagicByte = 0x00;
    private const int WireFormatHeaderLength = 5; // magic byte + 4-byte schema_id

    // Counters on the shared Babelstone meter (ADR-IC-007 Layer 1), tagged by source_topic only —
    // operational tier, never PII. A host turns them on with AddMeter(BabelstoneTelemetry.MeterName);
    // with no listener Add is a near no-op. handled = first-time effects; duplicates = dedup hits
    // (the dedup backstop firing); poison = un-processable records skipped.
    private static readonly Counter<long> HandledMessages =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.InboxHandledMetric,
            description: "Inbox messages handled for the first time (dedup row inserted + handler ran).");

    private static readonly Counter<long> DuplicateMessages =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.InboxDuplicatesMetric,
            description: "Inbox messages skipped as duplicate deliveries (message_id PK collision).");

    private static readonly Counter<long> PoisonMessages =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.InboxPoisonMetric,
            description: "Inbox records skipped as poison (un-decodable / unknown / missing message_id).");

    private static readonly Counter<long> TombstoneMessages =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.InboxTombstoneMetric,
            description: "Inbox null-payload tombstones skipped (GDPR compaction erasure signal, ADR-IC-002).");

    // PostgreSQL unique-violation SQLSTATE — the inbox_pkey collision that IS the "already processed"
    // signal (Document 04). Matching the SQLSTATE (not the message text) keeps it locale-independent.
    private const string UniqueViolation = PostgresErrorCodes.UniqueViolation;

    // The inbox PRIMARY KEY constraint (0012_inbox.sql: CONSTRAINT inbox_pkey PRIMARY KEY (message_id)).
    // The dedup catch narrows on BOTH the SQLSTATE and this constraint name so a handler-side unique
    // violation on a DIFFERENT constraint is never misread as an inbox duplicate.
    private const string InboxPkey = "inbox_pkey";

    private readonly InboxConsumerOptions _options;
    private readonly AvroEventSerializer _serializer;
    private readonly ISchemaByIdResolver? _writerSchemas;
    private readonly IInboxEventTypeResolver _eventTypes;
    private readonly IInboxMessageHandler _handler;
    private readonly IInboxPoisonSink? _poisonSink;
    private readonly IConsumer<byte[], byte[]> _consumer;
    private readonly bool _ownsConsumer;

    public InboxPump(
        InboxConsumerOptions options,
        AvroEventSerializer serializer,
        IInboxEventTypeResolver eventTypes,
        IInboxMessageHandler handler,
        IInboxPoisonSink? poisonSink = null,
        IConsumer<byte[], byte[]>? consumer = null,
        ISchemaByIdResolver? writerSchemas = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _eventTypes = eventTypes ?? throw new ArgumentNullException(nameof(eventTypes));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _poisonSink = poisonSink;
        // The SR writer-schema resolver: when present, the decode resolves the WRITER schema by the
        // embedded wire-format schema_id and reads writer→reader (Avro schema resolution, ADR-IC-002).
        // When null, the decode falls back to the writer == reader fast path (the same-version
        // intra-context case) — a unit test without an SR, or a deployment that has not wired one yet.
        _writerSchemas = writerSchemas;

        if (consumer is null)
        {
            // EnableAutoCommit = false is load-bearing (see the class remarks): the offset commits
            // only after the DB transaction, so a crash redelivers rather than skips. AutoOffsetReset
            // only matters for a brand-new group with no committed offset.
            var config = new ConsumerConfig
            {
                BootstrapServers = options.BootstrapServers,
                GroupId = options.GroupId,
                EnableAutoCommit = false,
                AutoOffsetReset = options.StartFromEarliest ? AutoOffsetReset.Earliest : AutoOffsetReset.Latest,
            };
            // ADR-IC-016 plane ii: present this consumer's distinct SASL/SCRAM identity to Redpanda when
            // a credential is configured (resolved by the host through ISecretProvider), so topic ACLs can
            // scope it to only the topics it subscribes to. A no-op in local dev — additive, leaving the
            // load-bearing auto-commit/offset-reset settings untouched.
            options.Sasl.ApplyTo(config);
            _consumer = new ConsumerBuilder<byte[], byte[]>(config).Build();
            _ownsConsumer = true;
            _consumer.Subscribe(options.Topics);
        }
        else
        {
            _consumer = consumer;
            _ownsConsumer = false;
        }
    }

    /// <summary>The disposition of one <see cref="PumpOnceAsync"/> call.</summary>
    public enum PumpOutcome
    {
        /// <summary>No record was available within the consume timeout (idle topic).</summary>
        Idle,

        /// <summary>A first-time message: dedup row inserted, handler ran, transaction + offset committed.</summary>
        Handled,

        /// <summary>A duplicate delivery: the dedup INSERT collided on the PK; offset committed, no effect ran.</summary>
        Duplicate,

        /// <summary>An un-processable record: skipped past (offset committed) after the poison sink saw it.</summary>
        Poison,

        /// <summary>A null-payload compaction tombstone (GDPR erasure signal): skipped past (offset
        /// committed) WITHOUT being decoded as Avro — distinct from <see cref="Poison"/>, never an alert.</summary>
        Tombstone,
    }

    /// <summary>
    /// Consume and process exactly one record (or return <see cref="PumpOutcome.Idle"/> if none is
    /// available within <see cref="InboxConsumerOptions.ConsumeTimeout"/>). A handler exception
    /// propagates AFTER the transaction has rolled back and BEFORE the offset is committed, so the
    /// caller's loop redelivers the record — the right behaviour for a transient failure.
    /// </summary>
    public async Task<PumpOutcome> PumpOnceAsync(CancellationToken ct = default)
    {
        var result = _consumer.Consume(_options.ConsumeTimeout);
        if (result?.Message is null)
        {
            return PumpOutcome.Idle; // idle poll — loop comes back round
        }

        // A null-payload tombstone (a record with a key but no value) is Redpanda log compaction's
        // GDPR right-to-erasure signal on a cleanup.policy=compact topic (ADR-IC-001). The
        // consumer contract (ADR-IC-002) requires it be recognised BEFORE the Avro decode: never
        // deserialise null as Avro, and — crucially — do not mistake it for a poison record. This
        // assembly is a dedup + dispatch seam, not a projection owner, so there is no materialised
        // state to delete; the correct, contract-honouring action is skip-and-commit (step past it,
        // count it as a tombstone, never as poison so a routine crypto-shred upstream never raises a
        // false dead-letter alert). A handler that owns projection state plugs that erasure in via the
        // saga seam; until then skip-and-commit is the conservative, GDPR-compliant behaviour.
        if (IsTombstone(result))
        {
            CommitPast(result);
            TombstoneMessages.Add(1, TopicTag(result.Topic));
            return PumpOutcome.Tombstone;
        }

        // A poison record can never be retried into correctness; skip past it (offset committed) so
        // it does not wedge the partition. A handler EXCEPTION is the opposite — it rolls back and
        // redelivers — so the two are kept on distinct paths.
        if (!TryDecode(result, out var message, out var poisonReason))
        {
            await OnPoisonAsync(result, poisonReason, ct);
            CommitPast(result);
            PoisonMessages.Add(1, TopicTag(result.Topic));
            return PumpOutcome.Poison;
        }

        bool handled;
        try
        {
            handled = await DedupAndHandleAsync(message, ct);
        }
        catch
        {
            // A transient handler/DB failure: the DB transaction already rolled back (DedupAndHandle
            // owns that), and the offset is NOT committed. But the in-memory consumer position has
            // already advanced past this record, so the NEXT Consume in this same session would skip
            // it — at-least-once would silently degrade to at-most-once until a restart re-read the
            // uncommitted offset. Seek BACK to this record's offset so the next Consume re-reads it
            // (in-session redelivery), then rethrow so the host loop backs off. The dedup INSERT will
            // absorb the redelivery if the effect did, against expectation, partially apply.
            SeekTo(result);
            throw;
        }

        // Offset commits AFTER the DB transaction (handled or deduped) — never before. A crash here
        // redelivers (a restart re-reads the uncommitted offset); the dedup INSERT then collides and
        // the offset advances on the retry.
        CommitPast(result);

        if (handled)
        {
            HandledMessages.Add(1, TopicTag(result.Topic));
            return PumpOutcome.Handled;
        }

        DuplicateMessages.Add(1, TopicTag(result.Topic));
        return PumpOutcome.Duplicate;
    }

    /// <summary>
    /// The in-transaction dedup + dispatch (Document 04's handler shape). Returns true if this was a
    /// first-time delivery (dedup row inserted, handler ran, committed); false if it was a duplicate
    /// (the <c>message_id</c> PK collided and the transaction rolled back). A handler exception
    /// surfaces to the caller with the transaction rolled back.
    /// </summary>
    private async Task<bool> DedupAndHandleAsync(InboxMessage message, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Document 04's exact shape: (1) IF EXISTS (the dedup row) → roll back + skip BEFORE the
        // handler runs, so a redelivery's effect never runs twice; (2) else run the handler; (3)
        // INSERT the dedup row. The SELECT short-circuits the common sequential-redelivery (the
        // handler is not invoked). The INSERT is the race-safe BACKSTOP: two CONCURRENT deliveries
        // both pass the SELECT, both run the handler in their own transaction, but only one INSERT
        // wins — the loser hits the unique-violation and rolls back (its effect discarded). The
        // whole sequence is one transaction, so the dedup row and the handler's effect commit (or
        // roll back) together — effectively-once.
        if (await AlreadyProcessedAsync(connection, transaction, message.MessageId, ct))
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        string? resultSummary;
        try
        {
            resultSummary = await _handler.HandleAsync(message, connection, transaction, ct);
            await InsertInboxRowAsync(connection, transaction, message, resultSummary, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation && ex.ConstraintName == InboxPkey)
        {
            // The concurrent-race loser (the SELECT above missed a row a racing transaction inserted
            // and committed between this SELECT and this INSERT). The inbox_pkey collision is the
            // dedup backstop doing its job: roll back (the handler's effect is
            // discarded — it must not commit twice) and report the duplicate. Not an error.
            //
            // The constraint filter is load-bearing: the handler runs INSIDE this try (it may INSERT
            // its own rows — a saga-state row, a local-outbox row, the outbox→inbox→outbox chain of
            // Document 04). A unique-violation on ANY OTHER constraint (a saga PK, a local-outbox
            // event_id) is NOT an inbox duplicate — treating it as one would roll back and commit the
            // offset, silently dropping a message whose effect never landed (at-most-once). So only
            // the inbox PK is the dedup signal; every other unique-violation propagates as a transient
            // failure, the caller seeks back, and the record is redelivered. (Same backstop shape as
            // PostgresEventStore's events_stream_seq_uq narrowing.)
            await transaction.RollbackAsync(ct);
            return false;
        }

        await transaction.CommitAsync(ct);
        return true;
    }

    private static async Task<bool> AlreadyProcessedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid messageId, CancellationToken ct)
    {
        // The Document 04 IF EXISTS check. It short-circuits the handler for an already-processed
        // message_id; the INSERT (not this SELECT) remains the authoritative race-safe gate, so this
        // is an optimisation against re-running effects, never the sole dedup decision.
        const string sql = "SELECT 1 FROM inbox WHERE message_id = @message_id;";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("message_id", messageId);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task InsertInboxRowAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        InboxMessage message, string? resultSummary, CancellationToken ct)
    {
        // processed_at defaults to clock_timestamp() in the schema — an audit/retention stamp, never
        // part of the dedup decision (the PK is). result_summary stays operational-tier (the handler
        // contract forbids PII). A duplicate message_id throws unique-violation here — caught above.
        const string sql = """
            INSERT INTO inbox (message_id, source_topic, result_summary)
            VALUES (@message_id, @source_topic, @result_summary);
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("message_id", message.MessageId);
        command.Parameters.AddWithValue("source_topic", message.SourceTopic);
        command.Parameters.AddWithValue("result_summary", (object?)resultSummary ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Turn a Kafka record into an <see cref="InboxMessage"/>: read the CloudEvents envelope headers
    /// (ADR-IC-015), un-frame the Confluent wire-format value, resolve the CLR event type, and decode
    /// the Avro. Returns false (with a reason) for any record that cannot be processed — the poison path.
    /// </summary>
    internal bool TryDecode(ConsumeResult<byte[], byte[]> result, out InboxMessage message, out string reason)
    {
        message = null!;

        // ce_id is the dedup identity (the producer's event_id). Missing/unparseable → poison: with
        // no message_id there is nothing to deduplicate on.
        var ceId = Header(result, "ce_id");
        if (!Guid.TryParse(ceId, out var messageId))
        {
            reason = $"missing or unparseable ce_id header ('{ceId}')";
            return false;
        }

        // ce_type is the reverse-DNS type (com.bank.deposits.DepositMatured); its last segment is the
        // record name (== the CLR type name == the Avro record name) the codec + resolver key on.
        var ceType = Header(result, "ce_type");
        var recordName = RecordName(ceType);
        if (recordName.Length == 0)
        {
            reason = $"missing ce_type header ('{ceType}')";
            return false;
        }

        if (!_eventTypes.TryResolve(recordName, out var payloadType))
        {
            reason = $"no event type registered for record '{recordName}' (ce_type '{ceType}')";
            return false;
        }

        if (!TryUnframe(result.Message.Value, out var schemaId, out var avroValue))
        {
            reason = "value is not Confluent wire format (bad magic byte or too short)";
            return false;
        }

        DomainEvent decoded;
        try
        {
            decoded = DecodeWithSchemaResolution(avroValue, payloadType, schemaId);
        }
        catch (Exception ex)
        {
            // An undecodable Avro value (schema/type mismatch, or an unknown/unresolvable writer
            // schema_id) is poison — it cannot be retried into correctness, so it is skipped past, not
            // redelivered forever.
            reason = $"Avro decode failed for record '{recordName}': {ex.Message}";
            return false;
        }

        // ce_subject is the aggregate_id (the stream); absent on a malformed record → Guid.Empty,
        // which is informational only (the dedup key is ce_id) so it is not poison on its own.
        _ = Guid.TryParse(Header(result, "ce_subject"), out var aggregateId);

        message = new InboxMessage(messageId, result.Topic, aggregateId, ceType, decoded);
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Decode the bare Avro value into a <see cref="DomainEvent"/>. When a writer-schema resolver is
    /// wired, resolve the WRITER schema by the embedded wire-format <paramref name="schemaId"/> from the
    /// Schema Registry (cached) and read writer→reader via Avro schema resolution (ADR-IC-002) — so a
    /// producer on a NEWER, BACKWARD-compatible writer schema decodes correctly
    /// against this consumer's OLDER reader schema rather than mis-decoding → poison. With no resolver
    /// wired, fall back to the writer == reader fast path (same-version intra-context topics).
    /// A <c>ce_type</c>↔<c>schema_id</c> record-name mismatch (the header names one record but the id
    /// resolves to a different writer schema) surfaces as an Avro resolution FAILURE → the poison/skip-
    /// and-commit path, not a silent mis-decode: the divergence is by-design and observable on the
    /// poison counter.
    /// </summary>
    private DomainEvent DecodeWithSchemaResolution(ReadOnlyMemory<byte> avroValue, Type payloadType, int schemaId)
    {
        if (_writerSchemas is null)
        {
            return _serializer.Decode(avroValue, payloadType);
        }

        var writerSchema = _writerSchemas.ResolveWriterSchema(schemaId);
        return _serializer.Decode(avroValue, payloadType, writerSchema);
    }

    /// <summary>A compaction tombstone: a record present but with a null OR zero-length value (the GDPR
    /// erasure signal, ADR-IC-002). Detected BEFORE any Avro decode so a null payload is never
    /// deserialised and never mis-routed to the poison path. Confluent.Kafka surfaces a tombstone as a
    /// null <c>Message.Value</c>; a zero-length value is treated the same, defensively.</summary>
    internal static bool IsTombstone(ConsumeResult<byte[], byte[]> result)
        => result.Message.Value is null || result.Message.Value.Length == 0;

    /// <summary>magic byte 0x00 ‖ big-endian int32 schema_id ‖ avro value → the embedded WRITER
    /// <paramref name="schemaId"/> and the bare Avro value. The schema_id is no longer discarded: the
    /// decode resolves the writer schema from it via the Schema Registry (when a resolver is wired) and
    /// reads writer→reader under Avro schema resolution (ADR-IC-002). Both the framing AND
    /// the id are returned here. The inverse of <c>OutboxDrainer.ToConfluentWireFormat</c>.</summary>
    internal static bool TryUnframe(ReadOnlySpan<byte> framed, out int schemaId, out ReadOnlyMemory<byte> avroValue)
    {
        schemaId = 0;
        avroValue = ReadOnlyMemory<byte>.Empty;
        if (framed.Length < WireFormatHeaderLength || framed[0] != MagicByte)
        {
            return false;
        }

        schemaId = BinaryPrimitives.ReadInt32BigEndian(framed.Slice(1, 4));
        avroValue = framed[WireFormatHeaderLength..].ToArray();
        return true;
    }

    /// <summary>The schema_id the relay embedded. Read at decode (it resolves the writer schema via the
    /// SR) and exposed here for diagnostics + framing-parity tests. Kept internal.</summary>
    internal static int ReadSchemaId(ReadOnlySpan<byte> framed)
        => BinaryPrimitives.ReadInt32BigEndian(framed.Slice(1, 4));

    /// <summary>The record name = the last dot-segment of a reverse-DNS ce_type
    /// (com.bank.deposits.DepositMatured → DepositMatured). Empty for an empty ce_type.</summary>
    internal static string RecordName(string ceType)
    {
        if (string.IsNullOrEmpty(ceType))
        {
            return string.Empty;
        }

        var dot = ceType.LastIndexOf('.');
        return dot >= 0 ? ceType[(dot + 1)..] : ceType;
    }

    private async Task OnPoisonAsync(ConsumeResult<byte[], byte[]> result, string reason, CancellationToken ct)
    {
        if (_poisonSink is not null)
        {
            await _poisonSink.OnPoisonAsync(result, reason, ct);
        }
    }

    // Commit the offset PAST this record (Confluent commits the next-to-read offset). Synchronous
    // commit so a failure surfaces before the loop advances — at-least-once, never at-most-once.
    private void CommitPast(ConsumeResult<byte[], byte[]> result) => _consumer.Commit(result);

    // Seek the consumer back to THIS record's own offset so the next Consume re-reads it (in-session
    // redelivery after a transient handler failure). result.TopicPartitionOffset is this record's
    // offset; seeking to it (not offset+1) re-delivers the same record.
    private void SeekTo(ConsumeResult<byte[], byte[]> result) => _consumer.Seek(result.TopicPartitionOffset);

    private static KeyValuePair<string, object?> TopicTag(string topic)
        => new(BabelstoneAttributes.SourceTopic, topic);

    private static string Header(ConsumeResult<byte[], byte[]> result, string key)
        => result.Message.Headers.TryGetLastBytes(key, out var bytes)
            ? Encoding.UTF8.GetString(bytes)
            : string.Empty;

    public void Dispose()
    {
        if (_ownsConsumer)
        {
            // Close commits the final offsets and leaves the group cleanly (a fast rebalance for the
            // next instance) before disposing the client.
            _consumer.Close();
            _consumer.Dispose();
        }
    }
}
