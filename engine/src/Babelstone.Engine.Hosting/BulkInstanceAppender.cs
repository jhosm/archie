using Babelstone.EventStore;

namespace Babelstone.Engine.Hosting;

/// <summary>
/// The bulk runner's per-instance append — the engine's native op (ADR-PC-035 §P4 step 3): read
/// the instance's head, append ONE store-only cross-cutting event at it, idempotent on the §P3
/// deterministic command id. In plain English: this is how one item of a bulk job actually lands —
/// the same head-read + atomic append every command uses, with the envelope pins (family, pack,
/// schema) carried forward from the stream's own head so the spine never has to know which family
/// the instance belongs to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hosting library, not the engine kernel and not a family decider</b> — the same placement
/// rationale as <see cref="PackMigrationService{TState}"/>: no family domain logic runs here (no
/// money math, no product rules), only structural plumbing for an engine-declared event, and it
/// CONSUMES the append spine rather than being a port. Unlike the pack-migration write-path — which
/// closes a per-family <c>AggregateRuntime&lt;TState&gt;</c> and dispatches by family — the bulk
/// runner is CROSS-family by construction (one frozen set may span families), so this appender
/// resolves the event's binding through the instance's own family fold-module (the same
/// per-family-registry discipline as the spine projection drive) and appends via
/// <see cref="IEventStore"/> directly. The bound handler registry is the same fail-closed fold
/// authority the runtime uses: an event type the family does not bind cannot be appended.
/// </para>
/// <para>
/// <b>Store-only, enforced fail-loud (ADR-PC-035 §P4 / ADR-IC-017).</b> The per-instance event is
/// a store-only cross-cutting fact — appended, folded, replayable, never on the durable bus — so
/// this appender writes NO outbox rows and REFUSES a catalogued integration event rather than
/// silently dropping its bus leg. An operation whose event must reach the bus is a deliberate
/// later extension (dual-encode via the bus codec), not a silent behaviour here.
/// </para>
/// <para>
/// <b>Idempotent per (job, instance) (§P3).</b> The caller passes the deterministic
/// <see cref="BulkOperationCommandId"/>; a re-claimed/retried step replays into
/// <see cref="DuplicateCommandException"/>, which is returned as the ORIGINAL commit sequence —
/// a benign no-op, never a second append.
/// </para>
/// </remarks>
public sealed class BulkInstanceAppender
{
    private readonly IEventStore _store;
    private readonly IEventSerializer _serializer;
    private readonly IIntegrationEventCatalog _catalog;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyDictionary<string, HandlerRegistry> _registriesByFamily;

    public BulkInstanceAppender(
        IEventStore store,
        IEventSerializer serializer,
        IIntegrationEventCatalog catalog,
        IReadOnlyList<IFamilyModule> familyModules,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(familyModules);
        _store = store;
        _serializer = serializer;
        _catalog = catalog;
        _clock = clock;
        _registriesByFamily = familyModules.ToDictionary(
            module => module.FamilyName,
            module => new HandlerRegistry(module.Handlers),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Append <paramref name="event"/> at the instance's current head, store-only, idempotent on
    /// <paramref name="commandId"/>. Returns the commit sequence — the original one when the
    /// command id replays (the §P3 no-op path).
    /// </summary>
    /// <param name="instanceId">The target instance (stream) from the frozen set.</param>
    /// <param name="event">The adapter-built store-only cross-cutting event.</param>
    /// <param name="commandId">The deterministic <c>(job_id, instance_id)</c> command id (§P3).</param>
    /// <param name="actor">The job's registering operator — stamped on the envelope for audit.</param>
    /// <param name="validTime">The event's economic time — the caller supplies the job's
    /// registration instant, so a retried/restarted step re-derives the identical envelope stamp
    /// rather than a fresh clock read.</param>
    public async Task<long> AppendAsync(
        Guid instanceId,
        DomainEvent @event,
        Guid commandId,
        string actor,
        DateTimeOffset validTime,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var head = await HeadEnvelopeAsync(instanceId, ct)
            ?? throw new InvalidOperationException(
                $"Instance '{instanceId}' has no event stream — a frozen target must reference a live instance.");

        if (!_registriesByFamily.TryGetValue(head.Family, out var registry))
        {
            throw new InvalidOperationException(
                $"No family fold-module is loaded for family '{head.Family}' (instance '{instanceId}').");
        }

        if (!registry.TryResolveByPayloadType(@event.GetType(), out var registration))
        {
            // The same fail-closed stance as the aggregate fold: an event the instance's own
            // family does not bind can neither fold nor replay, so it must not append.
            throw new InvalidOperationException(
                $"Family '{head.Family}' has no handler binding for event payload type "
                + $"'{@event.GetType().Name}' — the bulk per-instance event must be foldable.");
        }

        if (_catalog.IsCataloguedIntegrationEvent(registration.EventType))
        {
            // Store-only by decision (ADR-PC-035 §P4 / ADR-IC-017): this appender writes no outbox
            // rows, so silently accepting a catalogued event would drop its bus leg — refuse loud.
            throw new InvalidOperationException(
                $"Event type '{registration.EventType}' is a catalogued integration event; the bulk "
                + "runner appends STORE-ONLY cross-cutting events (ADR-PC-035 / ADR-IC-017).");
        }

        var encoded = _serializer.Encode(@event);
        var envelope = new EventEnvelope(
            EventId: Guid.NewGuid(),
            StreamId: instanceId,
            SequenceNumber: head.SequenceNumber + 1,
            EventType: registration.EventType,
            EventSchemaVersion: registration.EventSchemaVersion,
            Family: head.Family,
            PartitionKey: instanceId, // v1: partition_key = stream_id (the AggregateRuntime convention)
            // The envelope pins ride forward from the head: a bulk step never re-pins anything —
            // re-pinning is the pack/schema-migration operations' OWN payload semantics
            // (ADR-PC-009), applied by their adapters' events, not by this plumbing.
            PackVersion: head.PackVersion,
            SchemaVersion: head.SchemaVersion,
            ValidTime: validTime,
            TransactionTime: _clock.GetUtcNow(),
            CausationId: null,
            CorrelationId: null,
            Actor: actor,
            Payload: encoded.Bytes,
            PayloadSchemaId: encoded.SchemaId);

        try
        {
            await _store.AppendAsync(instanceId, head.SequenceNumber, [envelope], outboxRows: [], commandId, ct);
            return head.SequenceNumber + 1;
        }
        catch (DuplicateCommandException replay)
        {
            // The §P3 no-op path: this exact (job, instance) already applied — return the original
            // receipt so the target records the true commit sequence, with no second append.
            return replay.CommitSequence;
        }
    }

    /// <summary>The head (latest) envelope of a stream, or null when the stream has no events —
    /// the same sequential head-read as the pack-migration write-path.</summary>
    private async Task<EventEnvelope?> HeadEnvelopeAsync(Guid streamId, CancellationToken ct)
    {
        EventEnvelope? head = null;
        await foreach (var envelope in _store.LoadAsync(streamId, fromSequence: 0, ct))
        {
            head = envelope; // the store streams in sequence order; the last one is the head
        }

        return head;
    }
}
