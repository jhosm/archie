using System.Diagnostics.Metrics;
using System.Text;
using Babelstone.Telemetry;
using Confluent.Kafka;
using Npgsql;

namespace Babelstone.Orchestrator.Inbox;

/// <summary>
/// The orchestrator's Redpanda consume half (ADR-IC-003 §S2 "the orchestrator is a Redpanda consumer
/// like every other service … the saga resumes when the triggering event arrives from Redpanda";
/// Document 05 §1). The MIRROR of the engine's <c>InboxPump</c>: one <see cref="ConsumeOnceAsync"/>
/// call consumes the next record, reads its CloudEvents Binary-mode headers (ADR-IC-015) into the
/// PII-free <see cref="SagaInboxEvent"/>, then — in ONE PostgreSQL transaction the loop OWNS — drives
/// the saga through <see cref="SagaAdvanceHandler.AdvanceAsync"/>, committing the Kafka offset only
/// after the DB transaction commits.
/// </summary>
/// <remarks>
/// <para>
/// <b>Headers, not payload (the extraction-ready boundary, ADR-PC-019 §P2).</b> The saga keys its
/// transition table on the inbound event's TYPE NAME alone (ADR-IC-003 §P2), never on the event's
/// payload — so this loop decodes the CloudEvents headers (<c>ce_id</c> → the dedup id, <c>ce_subject</c>
/// → the process id, <c>ce_type</c> → the event type, the optional <c>traceparent</c> → the trace
/// parent) and NEVER Avro-decodes the value. That is what lets the orchestrator depend only on
/// <c>Confluent.Kafka</c>, not on the engine's Avro codec or — crucially — the engine kernel
/// (<c>Babelstone.Engine</c>), keeping the orchestrator subtree shedable per ADR-PC-019. The Avro
/// value the engine relay frames is simply ignored here; a PII-free header projection is all the saga
/// reasons over.
/// </para>
/// <para>
/// <b>Offset ⇄ transaction ordering (the at-least-once / effectively-once contract).</b> The DB
/// transaction commits FIRST, the Kafka offset SECOND. <c>EnableAutoCommit = false</c> is load-bearing:
/// a crash between the two redelivers the record, the saga handler's inbox dedup INSERT collides on
/// the <c>message_id</c> PK, and the offset advances on the retry — at-least-once delivery,
/// effectively-once advance (Document 04 / ADR-IC-003 §P1). The reverse (commit offset first) would
/// risk losing a record whose advance had not committed.
/// </para>
/// <para>
/// <b>A transient failure is redelivered; a structurally-impossible event is skipped.</b> A handler
/// EXCEPTION (a transient DB failure, an optimistic-concurrency loss) rolls the transaction back and
/// leaves the offset UNCOMMITTED — the loop seeks back so the next consume re-reads the record, then
/// rethrows so the host loop backs off (the engine pump's exact shape). A NON-throwing rejection the
/// handler signals — <see cref="AdvanceOutcome.NoTransition"/> (an illegal (state, event) pair,
/// ADR-IC-003 §P2) or a record that cannot be turned into a <see cref="SagaInboxEvent"/> at all (a
/// missing <c>ce_id</c>/<c>ce_type</c>) — can never be retried into correctness, so the loop commits
/// past it rather than wedging the partition (the poison path). The handler itself writes the dedup
/// row for <see cref="AdvanceOutcome.NoTransition"/>/<see cref="AdvanceOutcome.UnknownSaga"/>/
/// <see cref="AdvanceOutcome.Terminal"/> so those advance the offset on commit.
/// </para>
/// <para>
/// <b>Tombstone (GDPR erasure, ADR-IC-002 §P4).</b> A keyed record with a null/empty value is a log
/// compaction erasure signal; it is recognised BEFORE any header processing and skipped past on its
/// own counter (never the poison counter, so a routine crypto-shred upstream raises no false alert).
/// The orchestrator owns no materialised PII state to erase, so skip-and-commit is the contract-honouring action.
/// </para>
/// <para>
/// <b>Determinism / purity (ADR-PC-010 §P5).</b> This loop is the impure SHELL — it owns Kafka, the
/// clock-free transaction lifecycle, and the offset. The pure decision core (the
/// <see cref="Saga.ConstitutionProcess"/> transition table) and the advance handler carry no clock, no
/// randomness, and no out-of-transaction I/O; this loop passes no time into the decision.
/// </para>
/// </remarks>
public sealed class SagaConsumeLoop : IDisposable
{
    // Counters on the SHARED Babelstone meter (ADR-IC-007 Layer 1), tagged by source_topic only —
    // operational tier, never PII. The SAME instrument names the engine inbox pump records, so a host
    // sees one consistent inbox_* metric family across the engine and the orchestrator. With no
    // listener, Add is a near no-op.
    private static readonly Counter<long> HandledMessages =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.InboxHandledMetric,
            description: "Saga inbox messages handled for the first time (dedup row inserted + saga advanced).");

    private static readonly Counter<long> DuplicateMessages =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.InboxDuplicatesMetric,
            description: "Saga inbox messages skipped as duplicate deliveries (message_id PK collision).");

    private static readonly Counter<long> PoisonMessages =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.InboxPoisonMetric,
            description: "Saga inbox records skipped as poison (no ce_id/ce_type, or an illegal transition).");

    private static readonly Counter<long> TombstoneMessages =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.InboxTombstoneMetric,
            description: "Saga inbox null-payload tombstones skipped (GDPR compaction erasure signal, ADR-IC-002 §P4).");

    private readonly SagaInboxConsumerOptions _options;
    private readonly SagaAdvanceHandler _handler;
    private readonly IConsumer<byte[], byte[]> _consumer;
    private readonly bool _ownsConsumer;

    public SagaConsumeLoop(
        SagaInboxConsumerOptions options,
        SagaAdvanceHandler handler,
        IConsumer<byte[], byte[]>? consumer = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));

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

    /// <summary>The disposition of one <see cref="ConsumeOnceAsync"/> call.</summary>
    public enum ConsumeOutcome
    {
        /// <summary>No record was available within the consume timeout (idle topic).</summary>
        Idle,

        /// <summary>A first-time message that started or advanced the saga: dedup row inserted, the
        /// state moved (or the saga was started/no-op'd on a terminal/unknown saga), transaction + offset committed.</summary>
        Handled,

        /// <summary>A duplicate delivery: the dedup row already existed; offset committed, no second advance ran.</summary>
        Duplicate,

        /// <summary>An un-processable record skipped past (offset committed): a record with no
        /// <c>ce_id</c>/<c>ce_type</c>, or an illegal (state, event) transition (ADR-IC-003 §P2).</summary>
        Poison,

        /// <summary>A null-payload compaction tombstone (GDPR erasure signal): skipped past (offset
        /// committed) WITHOUT any header processing — distinct from <see cref="Poison"/>, never an alert.</summary>
        Tombstone,
    }

    /// <summary>
    /// Consume and process exactly one record (or return <see cref="ConsumeOutcome.Idle"/> if none is
    /// available within <see cref="SagaInboxConsumerOptions.ConsumeTimeout"/>). A transient failure
    /// rolls the transaction back, seeks the consumer back to the record, and rethrows so the host loop
    /// backs off (the offset is NOT committed → redelivery). A structurally-impossible event commits
    /// past (poison), and a duplicate commits past after the dedup short-circuit.
    /// </summary>
    public async Task<ConsumeOutcome> ConsumeOnceAsync(CancellationToken ct = default)
    {
        var result = _consumer.Consume(_options.ConsumeTimeout);
        if (result?.Message is null)
        {
            return ConsumeOutcome.Idle; // idle poll — loop comes back round
        }

        // A null-payload tombstone (a record with a key but no value) is Redpanda log compaction's GDPR
        // right-to-erasure signal (ADR-IC-002 §P4). Recognised BEFORE any header processing and skipped
        // past on its OWN counter so a routine crypto-shred upstream never raises a false poison alert.
        if (IsTombstone(result))
        {
            CommitPast(result);
            TombstoneMessages.Add(1, TopicTag(result.Topic));
            return ConsumeOutcome.Tombstone;
        }

        // A record with no ce_id (the dedup identity) or no ce_type (the transition key) can never be
        // retried into correctness: skip past it (offset committed) rather than wedging the partition.
        if (!TryDecodeHeaders(result, out var message))
        {
            CommitPast(result);
            PoisonMessages.Add(1, TopicTag(result.Topic));
            return ConsumeOutcome.Poison;
        }

        AdvanceOutcome outcome;
        try
        {
            outcome = await AdvanceInTransactionAsync(message, ct);
        }
        catch
        {
            // A transient failure (a DB hiccup, a SagaConcurrencyException): the transaction already
            // rolled back and the offset is NOT committed. The in-memory consumer position has advanced
            // past this record, so the next Consume in this same session would skip it — seek BACK to
            // re-read it (in-session redelivery), then rethrow so the host loop backs off. The dedup
            // INSERT absorbs the redelivery if the effect did, against expectation, partially apply.
            SeekTo(result);
            throw;
        }

        // An illegal transition (NoTransition) is a structurally-impossible event the handler already
        // dedup-rowed and rejected (ADR-IC-003 §P2): commit past it as poison so it does not redeliver
        // forever — exactly like the engine pump skips an unknown ce_type. (UnknownSaga and Terminal
        // are non-poison no-ops the handler also dedup-rowed; they advance the offset as Handled.)
        if (outcome == AdvanceOutcome.NoTransition)
        {
            CommitPast(result);
            PoisonMessages.Add(1, TopicTag(result.Topic));
            return ConsumeOutcome.Poison;
        }

        // Offset commits AFTER the DB transaction — never before. A crash here redelivers (a restart
        // re-reads the uncommitted offset); the dedup INSERT then collides and the offset advances.
        CommitPast(result);

        if (outcome == AdvanceOutcome.Duplicate)
        {
            DuplicateMessages.Add(1, TopicTag(result.Topic));
            return ConsumeOutcome.Duplicate;
        }

        HandledMessages.Add(1, TopicTag(result.Topic));
        return ConsumeOutcome.Handled;
    }

    /// <summary>
    /// Open the connection + transaction the loop owns, drive the saga through the advance handler, and
    /// commit. A handler exception leaves the transaction rolled back (the using-dispose) and surfaces
    /// to the caller — the offset is never committed, so the record redelivers.
    /// </summary>
    private async Task<AdvanceOutcome> AdvanceInTransactionAsync(SagaInboxEvent message, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var outcome = await _handler.AdvanceAsync(connection, transaction, message, ct);
        await transaction.CommitAsync(ct);
        return outcome;
    }

    /// <summary>
    /// Turn a Kafka record into a PII-free <see cref="SagaInboxEvent"/> from its CloudEvents headers
    /// (ADR-IC-015) — the Avro VALUE is deliberately never decoded (the saga keys on the type name
    /// alone, ADR-IC-003 §P2). Returns false for a record missing the <c>ce_id</c> dedup identity or the
    /// <c>ce_type</c> transition key (the poison path). A missing/garbled <c>traceparent</c> or
    /// <c>ce_correlationid</c> is NOT poison — those are optional trace references (the advance simply
    /// roots a fresh trace / carries no correlation), per <see cref="SagaTraceContext.ParseTraceParent"/>.
    /// </summary>
    internal static bool TryDecodeHeaders(ConsumeResult<byte[], byte[]> result, out SagaInboxEvent message)
    {
        message = null!;

        // ce_id is the dedup identity (the producer's event_id). Missing/unparseable → poison: with no
        // message_id there is nothing to deduplicate the saga advance on (Document 04).
        if (!Guid.TryParse(Header(result, "ce_id"), out var messageId))
        {
            return false;
        }

        // ce_type is the reverse-DNS type (com.bank.deposits.ConstitutionRequested); its last segment is
        // the record name == the event type the transition table keys on (ADR-IC-003 §P2).
        var eventType = RecordName(Header(result, "ce_type"));
        if (eventType.Length == 0)
        {
            return false;
        }

        // ce_subject is the saga instance (process id) the event drives. Absent/garbled → Guid.Empty,
        // which the advance handler treats as an unknown saga (a non-start event for no row) — not a
        // decode poison on its own, so it is allowed through to the handler's domain rejection.
        _ = Guid.TryParse(Header(result, "ce_subject"), out var processId);

        // Optional trace references — operational, never PII (ADR-PC-004 §P2). A missing header is null
        // (the advance roots a fresh trace / carries no correlation).
        var traceParent = NullableHeader(result, "traceparent");
        Guid? correlationId = Guid.TryParse(Header(result, "ce_correlationid"), out var c) ? c : null;

        message = new SagaInboxEvent(messageId, processId, eventType, result.Topic, correlationId, traceParent);
        return true;
    }

    /// <summary>A compaction tombstone: a record present but with a null OR zero-length value (the GDPR
    /// erasure signal, ADR-IC-002 §P4). Detected before any header processing so a null payload is never
    /// mistaken for a poison record.</summary>
    internal static bool IsTombstone(ConsumeResult<byte[], byte[]> result)
        => result.Message.Value is null || result.Message.Value.Length == 0;

    /// <summary>The record name = the last dot-segment of a reverse-DNS ce_type
    /// (com.bank.deposits.ConstitutionRequested → ConstitutionRequested). Empty for an empty ce_type.</summary>
    internal static string RecordName(string ceType)
    {
        if (string.IsNullOrEmpty(ceType))
        {
            return string.Empty;
        }

        var dot = ceType.LastIndexOf('.');
        return dot >= 0 ? ceType[(dot + 1)..] : ceType;
    }

    // Commit the offset PAST this record (Confluent commits the next-to-read offset). Synchronous commit
    // so a failure surfaces before the loop advances — at-least-once, never at-most-once.
    private void CommitPast(ConsumeResult<byte[], byte[]> result) => _consumer.Commit(result);

    // Seek the consumer back to THIS record's own offset so the next Consume re-reads it (in-session
    // redelivery after a transient failure). Seeking to result.TopicPartitionOffset (not offset+1)
    // re-delivers the same record.
    private void SeekTo(ConsumeResult<byte[], byte[]> result) => _consumer.Seek(result.TopicPartitionOffset);

    private static KeyValuePair<string, object?> TopicTag(string topic)
        => new(BabelstoneAttributes.SourceTopic, topic);

    private static string Header(ConsumeResult<byte[], byte[]> result, string key)
        => NullableHeader(result, key) ?? string.Empty;

    private static string? NullableHeader(ConsumeResult<byte[], byte[]> result, string key)
        => result.Message.Headers.TryGetLastBytes(key, out var bytes)
            ? Encoding.UTF8.GetString(bytes)
            : null;

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
