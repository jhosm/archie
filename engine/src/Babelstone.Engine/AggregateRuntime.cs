using System.Diagnostics;
using Babelstone.EventStore;
using Babelstone.Telemetry;

namespace Babelstone.Engine;

/// <summary>Per-append envelope metadata the runtime cannot derive from the events themselves.</summary>
/// <param name="CommandId">
/// The caller's deterministic command id (ADR-PC-029 slot 4). When non-null it makes the append
/// idempotent on the command id — the runtime threads it to the sink, which records a
/// <c>command_dedup</c> receipt in the append transaction so a replay returns the original head
/// rather than appending again. Carries the COMMAND identity, distinct from CorrelationId /
/// CausationId (the EVENT-lineage trio that lands on each envelope). <c>null</c> = a
/// non-idempotent append (engine-internal lifecycle steps that no external caller retries).
/// </param>
public sealed record AppendContext(
    string         Family,
    string         PackVersion,
    string         SchemaVersion,
    string         Actor,
    DateTimeOffset ValidTime,
    Guid?          CorrelationId = null,
    Guid?          CausationId = null,
    Guid?          CommandId = null);

/// <summary>
/// The rehydrated state of a stream plus its head sequence (-1 when the stream is empty) and the
/// transaction_time of the last folded event (null only when the stream is empty). The
/// transaction_time is event-derived, so a read served from this live fold reports the SAME
/// <c>last_updated</c> a read served from the denormalized read-model row would (ADR-IC-005 §P3) —
/// the read-model-backed and fold-backed answers to <c>GET /v1/deposits/{id}</c> are identical on the
/// wire, which is what lets the fold stay an internal fallback rather than a separate public URL.
/// </summary>
public sealed record Hydrated<TState>(
    TState State, long Version, Guid? LastEventId, DateTimeOffset? LastTransactionTime);

/// <summary>
/// The durable aggregate runtime (skeleton §4.5): rehydrates state snapshot-then-tail,
/// and commits new events + their outbox rows through the <see cref="IEventSink"/>.
/// This is where the §5.3 encrypt seam, the codec, and the one-transaction guarantee
/// meet. The injected <see cref="TimeProvider"/> stamps transaction_time — handlers
/// stay pure, the runtime owns the clock (ADR-PC-010 §P5).
/// </summary>
/// <param name="serializer">
/// The STORE codec — encodes each event for the <c>events.payload</c> book of record and decodes it
/// back on replay/fold. Per ADR-PC-028 this is the self-describing JSON store codec: the book of record
/// is decodable with NO Schema Registry (EVENT_STORE_PAYLOAD_SELF_DESCRIBING). It is also the ONLY
/// decode path (<see cref="FoldAsync"/> reads <c>events.payload</c> through it), so the bus codec never
/// touches the store / replay path — the family-agnostic kernel stays registry-free.
/// </param>
/// <param name="busSerializer">
/// The BUS codec — encodes a CATALOGUED event for its <c>outbox.payload</c> as real Avro plus the
/// registered Schema-Registry <c>schema_id</c> (ADR-IC-002 §P3 / ADR-IC-004 §P3). When non-null the
/// append DUAL-ENCODES inside the one sink transaction (ADR-PC-028 §Decision): JSON → store, Avro →
/// outbox. Both encodings describe the same event (STORE_BUS_ENCODING_EQUIVALENCE). When <c>null</c>
/// the outbox reuses <paramref name="serializer"/> for its bytes + <c>schema_id</c> — the pre-split
/// single-codec behaviour, so engine-internal/test wiring that hands one codec is unchanged. The
/// bus-encode runs ONLY where a catalogued event builds an outbox row, so an uncatalogued (store-only)
/// event never triggers it. This is an <see cref="IEventSerializer"/> like the store codec — the
/// concrete Avro/SR codec is injected by the host (Babelstone.Engine.Avro), so the kernel names neither
/// Avro nor the Schema Registry (ENGINE_FAMILY_AGNOSTIC + EVENT_STORE_PAYLOAD_SELF_DESCRIBING hold).
/// </param>
/// <param name="snapshotPolicy">
/// The §8.1 trigger that decides, AFTER a commit, whether to write a snapshot of the new head state.
/// Snapshots are a pure optimisation (loading from a snapshot + folding the tail is observationally
/// identical to a cold fold from zero — <c>SnapshotEquivalenceProperties</c>), so this gates only WHEN
/// to cache, never correctness. <c>null</c> means "never snapshot" — the pre-v1 posture, kept so
/// engine-internal/test wiring that hands no policy is unchanged. The PRODUCTION host injects the per-N
/// <see cref="CountBasedSnapshotPolicy"/> alongside a non-null <paramref name="snapshots"/> store, which
/// is what flips snapshots ON for v1 (ADR-PC-003 §P2). Has no effect without a <paramref name="snapshots"/>
/// store (there is nowhere to write).
/// </param>
/// <param name="onSnapshotError">
/// Fail-soft sink for a post-commit snapshot-write failure. Per ADR-PC-003 §P2 / event-store §8.1 the
/// snapshot write is EVENTUALLY-CONSISTENT with the log and NOT transactional with the append: "if it
/// fails the engine continues; the next rebuild is merely slower, never wrong." So a snapshot-write
/// exception is swallowed (the commit already succeeded and IS the book of record) and handed here for
/// the host to log. <c>null</c> drops it silently — acceptable because the snapshot is a rebuildable
/// cache, but the host should wire a logger so snapshotter health is observable (§P6 lag alarm). The
/// kernel takes a callback rather than an ILogger to stay logging-library-agnostic (ENGINE_FAMILY_AGNOSTIC).
/// </param>
public sealed class AggregateRuntime<TState>(
    IEventStore store,
    IEventSink sink,
    HandlerRegistry handlers,
    IEventSerializer serializer,
    IPiiProtector protector,
    TimeProvider clock,
    Func<TState> seedState,
    SnapshotStore<TState>? snapshots = null,
    IPostCommitProjector? postCommitProjector = null,
    IIntegrationEventCatalog? integrationEventCatalog = null,
    IEventSerializer? busSerializer = null,
    ISnapshotPolicy? snapshotPolicy = null,
    Action<Exception>? onSnapshotError = null,
    ICalendarBoundaryPolicy? calendarBoundaryPolicy = null)
{
    // The bus (outbox) encoder. Defaults to the STORE codec so existing single-codec wiring is
    // unchanged; the production host injects the real Avro+SR codec to make the outbox carry real Avro
    // bytes + a registered schema_id while the store keeps self-describing JSON (ADR-PC-028 §Decision /
    // STORE_BUS_ENCODING_EQUIVALENCE). It is an IEventSerializer (the kernel's family-agnostic,
    // Avro-library-agnostic seam) — the kernel never names Avro or the Schema Registry.
    private readonly IEventSerializer _busSerializer = busSerializer ?? serializer;

    // The catalog-gated-relay membership test (ADR-IC-017 §P1 / INTEGRATION_EVENT_CATALOG_GATED). The
    // append always writes the events-envelope row (so EVERY event is appended, folded, replayable), but
    // an OUTBOX row — the only thing the relay can ever publish — is written ONLY for a catalogued
    // integration event. An uncatalogued event is store-only by construction. The default is
    // publish-everything (the pre-ADR-IC-017 behaviour) so existing engine-internal/test wiring is
    // unchanged; the PRODUCTION host injects the real AvroSchemaCatalog. The seam is FAMILY-AGNOSTIC
    // (keyed by event_type string only), so the spine names no family — ENGINE_FAMILY_AGNOSTIC holds.
    private readonly IIntegrationEventCatalog _integrationEventCatalog =
        integrationEventCatalog ?? PublishAllIntegrationEventCatalog.Instance;

    // The calendar-boundary trigger of the composing snapshot policy (ADR-PC-003 §P2 / event-store
    // §8.1). The runtime owns the transaction-time clock (ADR-PC-010 §P5), so IT — never a handler —
    // decides whether an append crossed a reporting-period boundary, by comparing the previous head's
    // transaction_time to this append's. Defaults to OFF (CalendarGranularity.None) so existing wiring
    // that hands no policy keeps the pre-A.12 behaviour (per-N + lifecycle only); the production host
    // injects a Month/Year policy from config. Only consulted when a snapshot store + policy are wired.
    private readonly ICalendarBoundaryPolicy _calendarBoundaryPolicy =
        calendarBoundaryPolicy ?? new CalendarBoundaryPolicy(CalendarGranularity.None);

    /// <summary>Rehydrates from the latest verified snapshot, then folds the tail of events on top.</summary>
    public async Task<Hydrated<TState>> LoadAsync(Guid streamId, CancellationToken ct = default)
    {
        var snapshot = snapshots is null ? null : await snapshots.TryGetAsync(streamId, ct);
        var state = snapshot is null ? seedState() : snapshot.State;
        var version = snapshot?.AtSequence ?? -1;
        var lastEventId = snapshot?.LastEventId;
        // Event-derived, never the wall clock (ADR-PC-010 §P5): the transaction_time of the last folded
        // event, so a fold-backed read reports the same last_updated the read-model row would.
        // KNOWN GAP (no v1 snapshots, snapshots is null here): a stream fully covered by a snapshot with
        // no tail events would leave this null though Version >= 0 — when snapshotting lands, the snapshot
        // must carry the transaction_time it was taken at and seed this. Not reachable in v1.
        DateTimeOffset? lastTransactionTime = null;
        var fromSequence = version + 1;

        await foreach (var envelope in store.LoadAsync(streamId, fromSequence, ct))
        {
            state = await FoldAsync(state, envelope, unprotect: true, ct);
            version = envelope.SequenceNumber;
            lastEventId = envelope.EventId;
            lastTransactionTime = envelope.TransactionTime;
        }

        return new Hydrated<TState>(state, version, lastEventId, lastTransactionTime);
    }

    /// <summary>
    /// Rehydrates the stream's state AS OF a given per-stream <paramref name="asOfSequence"/> — the
    /// transaction-time / point-in-time read (the I.2 Query API as-of axis, bd babelstone-b4wp).
    /// Honours snapshots (ADR-PC-003 §P3): it seeds from the latest VALID snapshot at or below
    /// <paramref name="asOfSequence"/> and folds only the tail up to and INCLUDING that point — a
    /// snapshot taken PAST the point is the future relative to the read and is excluded. With no
    /// qualifying snapshot (asOf below the earliest snapshot, or no snapshots wired) it folds cold from
    /// sequence 0 — the §P3 correctness fallback. Either way the returned <see cref="Hydrated{TState}"/>
    /// is the historical projection at that point, not the current head, and is BYTE-IDENTICAL to a cold
    /// fold to the point (the snapshot is hash-verified equivalent to a cold fold at its sequence —
    /// <c>SnapshotEquivalenceProperties</c>). The fold is the SAME pure mechanism as
    /// <see cref="LoadAsync"/> (no clock, no randomness, ADR-PC-010 §P5), so a repeated as-of read at a
    /// given sequence returns identical state — deterministic by construction. The axis is the per-stream <c>sequence_number</c>
    /// (commit_sequence), the only point identifier the event log carries a deterministic total order
    /// for; a wall-clock <c>valid_time</c> axis waits on the bitemporal projection runtime (Epic D /
    /// ADR-PC-002), which the read model does not yet carry.
    /// </summary>
    /// <param name="streamId">The stream to read.</param>
    /// <param name="asOfSequence">
    /// The inclusive upper bound on <c>sequence_number</c>. MUST be &gt;= 0 (the caller validates a
    /// malformed/negative value at the boundary). A value at or below the stream head yields the
    /// historical state at that point; a value past the head returns a fold whose
    /// <see cref="Hydrated{TState}.Version"/> is the actual head (&lt; <paramref name="asOfSequence"/>),
    /// which the caller detects to reject a "point that does not exist yet" as a clean 4xx — this
    /// method never throws on an out-of-range point (it stays a pure fold; the boundary decides the
    /// HTTP verdict). A non-existent stream folds to <see cref="Hydrated{TState}.Version"/> = -1.
    /// </param>
    /// <param name="ct">Cancels the enumeration between events.</param>
    public async Task<Hydrated<TState>> LoadAsOfSequenceAsync(
        Guid streamId, long asOfSequence, CancellationToken ct = default)
    {
        // Snapshot-then-tail for the as-of read (ADR-PC-003 §P3): seed from the latest VALID snapshot
        // at or below the requested point and fold only the tail up to it. A snapshot taken PAST the
        // point is in the future relative to the read, so TryGetAtOrBeforeAsync excludes it (the §P1
        // readLatestSnapshot bound) — never the live-head TryGetAsync, which could sit ahead of the
        // point. When no snapshot qualifies (asOf < the earliest snapshot, or no snapshots at all) the
        // fold runs cold from sequence 0 — the §P3 correctness fallback. Because LoadAsync's snapshot is
        // hash-verified to be byte-identical to a cold fold at its AtSequence (SnapshotEquivalenceProperties),
        // seeding from it and folding the tail yields the SAME state a from-zero fold to the point would.
        var snapshot = snapshots is null
            ? null
            : await snapshots.TryGetAtOrBeforeAsync(streamId, asOfSequence, ct);
        var state = snapshot is null ? seedState() : snapshot.State;
        var version = snapshot?.AtSequence ?? -1L;
        Guid? lastEventId = snapshot?.LastEventId;
        // Event-derived transaction_time of the last folded event (ADR-PC-010 §P5). A snapshot seed
        // carries its CreatedAt — the append-stamped transaction_time it was taken at — so a stream
        // fully covered by the snapshot (no tail before the point) still reports a real last_updated.
        DateTimeOffset? lastTransactionTime = snapshot is null ? null : snapshot.CreatedAt;
        // Read only the un-snapshotted tail (snapshot.AtSequence + 1 ..), the same tail LoadAsync reads;
        // a cold read (no snapshot) starts at sequence 0.
        var fromSequence = version + 1;

        await foreach (var envelope in store.LoadAsync(streamId, fromSequence, ct))
        {
            // The store streams in sequence_number order; stop once we pass the requested point so the
            // fold reflects exactly the as-of state (events after the point are the "future" we exclude).
            if (envelope.SequenceNumber > asOfSequence)
            {
                break;
            }

            state = await FoldAsync(state, envelope, unprotect: true, ct);
            version = envelope.SequenceNumber;
            lastEventId = envelope.EventId;
            lastTransactionTime = envelope.TransactionTime;
        }

        return new Hydrated<TState>(state, version, lastEventId, lastTransactionTime);
    }

    /// <summary>
    /// Commits new domain events and their outbox rows in one transaction (via the sink).
    /// PII is encrypted here (the only OpenBao seam, §5.3); storage sees ciphertext-in-payload.
    /// </summary>
    /// <remarks>
    /// This impure runtime shell is the only correct home for a product-semantic span
    /// (ADR-PC-010 §P5 / ADR-IC-007): the pure decider/fold never touches telemetry. When
    /// <paramref name="spanName"/> is non-null a manual span is opened on
    /// <see cref="BabelstoneTelemetry.ActivitySource"/> around the commit, tagged with the
    /// caller-supplied <paramref name="spanAttributes"/> — the runtime stays domain-agnostic
    /// (it never names a span or invents an attribute value; the host, which knows the command,
    /// does). With no tracer listening, <see cref="ActivitySource.StartActivity(string,ActivityKind)"/>
    /// returns <c>null</c> and the path is a no-op.
    /// </remarks>
    /// <returns>
    /// The stream's new head version after the commit — <c>expectedVersion + events.Count</c>, i.e. the
    /// per-stream <c>sequence_number</c> of the last appended event. A command hands this back as the
    /// read-your-writes token (<c>commit_sequence</c>): it is the SAME number a read-model row carries as
    /// <c>last_sequence</c>, so a follow-up <c>GET /v1/deposits/{id}</c> with <c>If-Min-Sequence</c> set
    /// to it compares like-for-like (ADR-IC-005 §P3) and folds the stream only while the projector lags.
    /// </returns>
    public async Task<long> AppendAsync(
        Guid streamId,
        long expectedVersion,
        IReadOnlyList<DomainEvent> events,
        AppendContext context,
        CancellationToken ct = default,
        string? spanName = null,
        IReadOnlyList<KeyValuePair<string, object?>>? spanAttributes = null)
    {
        using var activity = spanName is null
            ? null
            : BabelstoneTelemetry.ActivitySource.StartActivity(spanName, ActivityKind.Internal);
        if (activity is not null && spanAttributes is not null)
        {
            foreach (var attribute in spanAttributes)
            {
                activity.SetTag(attribute.Key, attribute.Value);
            }
        }

        var envelopes = new List<EventEnvelope>(events.Count);
        var outboxRows = new List<OutboxRow>(events.Count);
        var transactionTime = clock.GetUtcNow();

        for (var i = 0; i < events.Count; i++)
        {
            if (!handlers.TryResolveByPayloadType(events[i].GetType(), out var registration))
            {
                throw new InvalidOperationException(
                    $"No handler registered for event payload type '{events[i].GetType()}'.");
            }

            var protectedEvent = await protector.ProtectAsync(events[i], ct);
            // STORE encode (ADR-PC-028 §Decision): the events.payload book of record is self-describing
            // JSON, decodable with no Schema Registry (EVENT_STORE_PAYLOAD_SELF_DESCRIBING). This is also
            // the SOLE decode path — FoldAsync reads events.payload back through `serializer` — so the bus
            // codec never touches the store / replay path.
            var storeEncoded = serializer.Encode(protectedEvent);
            var eventId = Guid.NewGuid();
            var sequence = expectedVersion + 1 + i;

            envelopes.Add(new EventEnvelope(
                EventId: eventId,
                StreamId: streamId,
                SequenceNumber: sequence,
                EventType: registration.EventType,
                EventSchemaVersion: registration.EventSchemaVersion,
                Family: context.Family,
                PartitionKey: streamId,                 // v1: partition_key = stream_id
                PackVersion: context.PackVersion,
                SchemaVersion: context.SchemaVersion,
                ValidTime: context.ValidTime,
                TransactionTime: transactionTime,
                CausationId: context.CausationId,
                CorrelationId: context.CorrelationId,
                Actor: context.Actor,
                Payload: storeEncoded.Bytes,
                PayloadSchemaId: storeEncoded.SchemaId));

            // Catalog-gated relay (ADR-IC-017 §P1): the envelope above is ALWAYS written, but the outbox
            // row — the relay's only publishable artefact — is written ONLY for a catalogued integration
            // event. An uncatalogued event is store-only by construction (appended/folded/replayable,
            // never on the bus). This is the append-side gate; it preserves append+outbox atomicity
            // (ES_ATOMIC_APPEND_OUTBOX) because the envelope and any outbox rows still commit in the one
            // sink transaction below. Fail-closed: a not-catalogued event_type simply gets no outbox row.
            if (_integrationEventCatalog.IsCataloguedIntegrationEvent(registration.EventType))
            {
                // BUS encode (ADR-PC-028 §Decision dual-encode / STORE_BUS_ENCODING_EQUIVALENCE): the
                // outbox row carries the BUS codec's bytes + its registered schema_id (real Avro +
                // schema_id from the host's Avro+SR codec; ADR-IC-002 §P3 / ADR-IC-004 §P3), NOT the
                // store's JSON. The encode runs HERE — only for a catalogued event, the exact place an
                // outbox row exists — so an uncatalogued (store-only) event never bus-encodes (and a
                // catalogued event by definition HAS an .avsc, so the Avro encode always succeeds). When
                // no separate bus codec was injected, _busSerializer == `serializer`, reproducing the
                // pre-split single-encoding. Both encodes feed the ONE sink transaction below
                // (ES_ATOMIC_APPEND_OUTBOX preserved).
                var busEncoded = _busSerializer.Encode(protectedEvent);
                outboxRows.Add(new OutboxRow(
                    EventId: eventId,
                    AggregateType: context.Family,
                    AggregateId: streamId,
                    SequenceNumber: sequence,
                    EventType: registration.EventType,
                    Payload: busEncoded.Bytes,
                    SchemaId: busEncoded.SchemaId,
                    Status: OutboxStatus.Pending,
                    CreatedAt: transactionTime,
                    PublishedAt: null,
                    // The CloudEvents extension attributes this event declares (family-agnostic seam,
                    // ADR-IC-018 §P5). Null for events that declare none — the common case. Carried onto
                    // the outbox row so the relay can promote each to a ce_<key> header.
                    IntegrationHeaders: events[i].IntegrationHeaders));
            }
        }

        await sink.AppendAsync(streamId, expectedVersion, envelopes, outboxRows, context.CommandId, ct);

        // Sync-mode projections (two-modes §5.4): once the event has committed, drive them within
        // a bounded budget. The hook NEVER rolls back the commit — "the event is true regardless
        // of whether a projection consumed it"; it surfaces its own failure/lag. v1 injects a
        // no-op (every projection is async); the budgeted hook is the v4 template.
        if (postCommitProjector is not null)
        {
            await postCommitProjector.NotifyAppendedAsync(context.Family, ct);
        }

        // The new head version (== the last appended event's per-stream sequence_number). The sink
        // rejects an empty batch, so events.Count >= 1 and this is always a real, committed sequence.
        var newHead = expectedVersion + events.Count;

        // Snapshot write (ADR-PC-003 §P2 / event-store §8.1): AFTER the commit succeeds, evaluate the
        // §8.1 COMPOSING trigger (per-N OR lifecycle-boundary OR calendar-boundary) and cache the new
        // head state if any fires. This is the write side LoadAsync's snapshot-then-tail read side was
        // always waiting for. Three invariants the call below upholds:
        //
        //  • EVENTUALLY-CONSISTENT, NOT TRANSACTIONAL with the append (§P2). The snapshot is written on
        //    its OWN connection after the append transaction has committed — never inside it — so a
        //    snapshot write can never roll back or delay a committed event.
        //  • FAIL-SOFT. A snapshot-write failure is swallowed and handed to onSnapshotError: "if it
        //    fails the engine continues; the next rebuild is merely slower, never wrong" (§P2). The
        //    committed event IS the book of record; the snapshot is a rebuildable cache.
        //  • PURE OPTIMISATION. The snapshotted state is what LoadAsync folds (snapshot-then-tail),
        //    which SnapshotEquivalenceProperties proves byte-identical to a cold fold — so writing one
        //    can never change a read's answer, only its speed.
        if (snapshots is not null && snapshotPolicy is not null)
        {
            // LIFECYCLE boundary: a pure structural property of the appended event TYPES (the family
            // marks its own lifecycle events via DomainEvent.IsLifecycleBoundary — no clock, no I/O), so
            // the engine stays family-agnostic. ANY lifecycle event in the batch makes the append a
            // boundary (e.g. a constitution + an upfront-interest triple is still a constitution boundary).
            var isLifecycleBoundary = false;
            for (var i = 0; i < events.Count; i++)
            {
                if (events[i].IsLifecycleBoundary)
                {
                    isLifecycleBoundary = true;
                    break;
                }
            }

            await TrySnapshotAsync(
                streamId, expectedVersion, newHead, transactionTime, isLifecycleBoundary, context, ct);
        }

        return newHead;
    }

    /// <summary>
    /// Post-commit, eventually-consistent snapshot write (ADR-PC-003 §P2 / event-store §8.1). Evaluates
    /// the §8.1 trigger and, if it fires, caches the new head state. Fail-soft by construction: any
    /// exception (a transient snapshot-store write failure) is swallowed and surfaced via
    /// <c>onSnapshotError</c> — the append already committed, and a missing snapshot only makes the next
    /// rebuild slower, never wrong (the cold fold is the correctness fallback, §8.2).
    /// </summary>
    /// <remarks>
    /// Composes ALL THREE triggers of ADR-PC-003 §P2 (event-store §8.1): the per-N count
    /// (<see cref="SnapshotContext.EventsSinceSnapshot"/>), the lifecycle boundary
    /// (<paramref name="isLifecycleBoundary"/>, OR'd from the appended events' <c>IsLifecycleBoundary</c>
    /// by the caller), and the calendar boundary (computed HERE by comparing the previous head's
    /// transaction_time to this append's via <see cref="_calendarBoundaryPolicy"/>). A snapshot is taken
    /// if ANY fires. The boundary signals are family-supplied (lifecycle) or runtime-derived (calendar,
    /// over the event-stamped transaction_time — never a fresh clock read), so the engine stays
    /// family-agnostic and the trigger stays a deterministic function of the log. The state snapshotted
    /// is taken from <see cref="LoadAsync"/> (snapshot-then-tail), so the snapshot the policy caches is by
    /// construction the same state a fold would reconstruct — keeping the cache a pure optimisation
    /// (<c>SnapshotEquivalenceProperties</c>).
    /// </remarks>
    private async Task TrySnapshotAsync(
        Guid streamId, long expectedVersion, long newHead, DateTimeOffset transactionTime,
        bool isLifecycleBoundary, AppendContext context, CancellationToken ct)
    {
        try
        {
            // Events since the last snapshot drive the per-N trigger. A stream with no snapshot yet has
            // newHead + 1 events folded (sequences are 0-indexed); with a snapshot at sequence s, the
            // un-snapshotted tail is (newHead - s) events. The store boundary owns the snapshots table,
            // so the latest-snapshot lookup is the only authority on what is already cached.
            var existing = await snapshots!.TryGetAsync(streamId, ct);
            var eventsSinceSnapshot = existing is null ? newHead + 1 : newHead - existing.AtSequence;

            // CALENDAR boundary (§P2): did this append land in a later reporting period than the previous
            // head? The runtime owns the transaction-time clock (ADR-PC-010 §P5), so it compares the
            // PREVIOUS head's event-derived transaction_time against this append's — a deterministic
            // function of the log, not a wall-clock read. The previous-head read runs ONLY when the
            // calendar policy is active and this is not the first append (expectedVersion == -1 ⇒ no
            // prior event, and a first append is a lifecycle boundary anyway) — so the common per-N-only
            // wiring (a None calendar policy) pays no extra read.
            var isCalendarBoundary =
                _calendarBoundaryPolicy.IsActive
                && expectedVersion >= 0
                && _calendarBoundaryPolicy.CrossedBoundary(
                    await PreviousHeadTransactionTimeAsync(streamId, expectedVersion, ct), transactionTime);

            // The COMPOSING context (ADR-PC-003 §P2): per-N OR lifecycle OR calendar. CountBasedSnapshotPolicy
            // ORs the three, so handing all three live signals is what turns the lifecycle/calendar triggers
            // ON. The signals are family-supplied (lifecycle) or runtime-derived (calendar) — the engine
            // names no family (ENGINE_FAMILY_AGNOSTIC).
            var snapshotContext = new SnapshotContext(
                EventsSinceSnapshot: eventsSinceSnapshot,
                IsLifecycleBoundary: isLifecycleBoundary,
                IsCalendarBoundary: isCalendarBoundary);

            if (!snapshotPolicy!.ShouldSnapshot(snapshotContext))
            {
                return;
            }

            // Re-load the head via snapshot-then-tail — the SAME pure fold a read serves — so the cached
            // state is exactly what a cold fold would produce. Deposit streams are short, so this re-read
            // is cheap; it keeps the write side from re-deriving state by a separate (drift-prone) path.
            var head = await LoadAsync(streamId, ct);

            // A snapshot needs a covered event to hash against last_event_id (§8.3); the empty-stream case
            // (Version -1, no LastEventId) is unreachable here because the sink rejects an empty append,
            // but guard it so the contract is explicit rather than relying on that invariant.
            if (head.LastEventId is null)
            {
                return;
            }

            // PutAsync writes on its own connection (PostgresSnapshotStore), AFTER the append committed —
            // never in the append transaction (§P2 eventually-consistent). created_at is the append's
            // event-derived transaction_time, not a fresh wall-clock read: the runtime owns the clock,
            // and reusing the committed stamp keeps the snapshot's timeline event-aligned (ADR-PC-010 §P5).
            await snapshots.PutAsync(streamId, head.Version, head.LastEventId.Value, head.State, transactionTime, ct);
        }
        catch (Exception ex)
        {
            // Fail-soft (§P2): the committed event is unaffected by a snapshot-write failure. Surface it
            // (the host wires a logger / §P6 lag alarm) rather than letting it propagate and look like an
            // append failure to the caller.
            onSnapshotError?.Invoke(ex);
        }
    }

    /// <summary>
    /// The event-derived transaction_time of the PREVIOUS head (the event at <paramref name="atSequence"/>),
    /// used by the calendar-boundary trigger to decide whether THIS append crossed a reporting period.
    /// Reads only that one event (the store streams in sequence order; we take the first at-or-after the
    /// previous head and read no further), so it is a cheap point lookup — no fold, no decode. Returns null
    /// if the event is somehow absent (defensive; the previous head must exist on a non-first append).
    /// </summary>
    private async Task<DateTimeOffset?> PreviousHeadTransactionTimeAsync(
        Guid streamId, long atSequence, CancellationToken ct)
    {
        await foreach (var envelope in store.LoadAsync(streamId, atSequence, ct))
        {
            // The first streamed event is the previous head (sequence == atSequence); its transaction_time
            // is all the calendar trigger needs. Stop immediately — no need to read the rest of the stream.
            return envelope.TransactionTime;
        }

        return null;
    }

    private async Task<TState> FoldAsync(TState state, EventEnvelope envelope, bool unprotect, CancellationToken ct)
    {
        if (!handlers.TryResolveByEventType(envelope.EventType, out var registration))
        {
            throw new InvalidOperationException($"No handler registered for event type '{envelope.EventType}'.");
        }

        var @event = serializer.Decode(envelope.Payload, registration.PayloadType);
        if (unprotect)
        {
            @event = await protector.UnprotectAsync(@event, ct);
        }

        return (TState)registration.Handler.ApplyBoxed(state!, @event).NewState;
    }
}
