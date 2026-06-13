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
    IEventSerializer? busSerializer = null)
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
    /// Folds the stream from the start up to and INCLUDING <paramref name="asOfSequence"/> and stops,
    /// so the returned <see cref="Hydrated{TState}"/> is the historical projection at that point, not
    /// the current head. The fold is the SAME pure mechanism as <see cref="LoadAsync"/> (no clock, no
    /// randomness, ADR-PC-010 §P5), so a repeated as-of read at a given sequence returns identical
    /// state — deterministic by construction. The axis is the per-stream <c>sequence_number</c>
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
        // No snapshots in v1 (snapshots is null on the term-deposit runtime), so a clean cold fold
        // from sequence 0 is correct and cheap (deposit streams are short). When snapshotting lands,
        // an as-of read must only use a snapshot whose AtSequence <= asOfSequence (a snapshot past the
        // point is in the future relative to the read); a snapshot at-or-before the point seeds the
        // fold, then the tail folds up to the point — tracked with the snapshot work (ADR-PC-003).
        var state = seedState();
        var version = -1L;
        Guid? lastEventId = null;
        DateTimeOffset? lastTransactionTime = null;

        await foreach (var envelope in store.LoadAsync(streamId, 0, ct))
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
                    PublishedAt: null));
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
        return expectedVersion + events.Count;
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
