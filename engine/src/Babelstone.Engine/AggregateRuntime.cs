using System.Diagnostics;
using Babelstone.EventStore;
using Babelstone.Telemetry;

namespace Babelstone.Engine;

/// <summary>Per-append envelope metadata the runtime cannot derive from the events themselves.</summary>
public sealed record AppendContext(
    string         Family,
    string         PackVersion,
    string         SchemaVersion,
    string         Actor,
    DateTimeOffset ValidTime,
    Guid?          CorrelationId = null,
    Guid?          CausationId = null);

/// <summary>The rehydrated state of a stream plus its head sequence (-1 when the stream is empty).</summary>
public sealed record Hydrated<TState>(TState State, long Version, Guid? LastEventId);

/// <summary>
/// The durable aggregate runtime (skeleton §4.5): rehydrates state snapshot-then-tail,
/// and commits new events + their outbox rows through the <see cref="IEventSink"/>.
/// This is where the §5.3 encrypt seam, the codec, and the one-transaction guarantee
/// meet. The injected <see cref="TimeProvider"/> stamps transaction_time — handlers
/// stay pure, the runtime owns the clock (ADR-PC-010 §P5).
/// </summary>
public sealed class AggregateRuntime<TState>(
    IEventStore store,
    IEventSink sink,
    HandlerRegistry handlers,
    IEventSerializer serializer,
    IPiiProtector protector,
    TimeProvider clock,
    Func<TState> seedState,
    SnapshotStore<TState>? snapshots = null)
{
    /// <summary>Rehydrates from the latest verified snapshot, then folds the tail of events on top.</summary>
    public async Task<Hydrated<TState>> LoadAsync(Guid streamId, CancellationToken ct = default)
    {
        var snapshot = snapshots is null ? null : await snapshots.TryGetAsync(streamId, ct);
        var state = snapshot is null ? seedState() : snapshot.State;
        var version = snapshot?.AtSequence ?? -1;
        var lastEventId = snapshot?.LastEventId;
        var fromSequence = version + 1;

        await foreach (var envelope in store.LoadAsync(streamId, fromSequence, ct))
        {
            state = await FoldAsync(state, envelope, unprotect: true, ct);
            version = envelope.SequenceNumber;
            lastEventId = envelope.EventId;
        }

        return new Hydrated<TState>(state, version, lastEventId);
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
    public async Task AppendAsync(
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
            var encoded = serializer.Encode(protectedEvent);
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
                Payload: encoded.Bytes,
                PayloadSchemaId: encoded.SchemaId));

            outboxRows.Add(new OutboxRow(
                EventId: eventId,
                AggregateType: context.Family,
                AggregateId: streamId,
                SequenceNumber: sequence,
                EventType: registration.EventType,
                Payload: encoded.Bytes,
                SchemaId: encoded.SchemaId,
                Status: OutboxStatus.Pending,
                CreatedAt: transactionTime,
                PublishedAt: null));
        }

        await sink.AppendAsync(streamId, expectedVersion, envelopes, outboxRows, ct);
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
