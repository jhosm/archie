using Babelstone.EventStore;

namespace Babelstone.Engine;

/// <summary>
/// Folds one family's events into a single projection of state type <typeparamref name="TState"/>,
/// writing bitemporal rows through <see cref="ProjectionStore{TState}"/>. The fold reuses the
/// SAME pure dispatch as the aggregate runtime (<see cref="HandlerRegistry"/> +
/// <see cref="IDispatchableHandler.ApplyBoxed"/>), so a projection handler is subject to the same
/// determinism guarantees (ADR-PC-010) and the batch-vs-inline equivalence holds.
/// </summary>
/// <remarks>
/// Decode is structural-only: the payload is decoded but NOT PII-unprotected (a projection's
/// structural state carries no PII; ADR-PC-004). The runner is idempotent under at-least-once
/// delivery via the <c>source_sequence</c> guard — re-applying an already-folded event is a no-op,
/// which is what makes the accumulating folds (state.X + event.Y) safe to replay.
/// </remarks>
public sealed class ProjectionRunner<TState>(
    string kind,
    string family,
    ProjectionMode mode,
    HandlerRegistry handlers,
    IEventSerializer serializer,
    Func<TState> seed,
    ProjectionStore<TState> store) : IProjectionRunner
    where TState : class
{
    public string Kind => kind;
    public string Family => family;
    public ProjectionMode Mode => mode;

    public async Task ApplyAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        // This projection may not fold every family event type (F.6: an accrual-schedule
        // projection ignores non-accrual events). An unhandled type leaves the belief unchanged.
        if (!handlers.TryResolveByEventType(envelope.EventType, out var registration))
        {
            return;
        }

        var current = await store.TryReadCurrentAsync(envelope.StreamId, kind, ct);

        // Idempotency (at-least-once): the drainer may re-deliver an event after a crash between
        // the projection write and the checkpoint advance. Skip anything already folded.
        if (current is not null && current.SourceSequence >= envelope.SequenceNumber)
        {
            return;
        }

        var state = current?.State ?? seed();
        var @event = serializer.Decode(envelope.Payload, registration.PayloadType);
        var next = (TState)registration.Handler.ApplyBoxed(state, @event).NewState;

        // Deterministic temporal stamps come from the event, never the clock (ADR-PC-010).
        var temporal = new ProjectionTemporalContext(
            RecordedAt: envelope.TransactionTime,
            ValidFrom: envelope.ValidTime,
            ValidTo: null);

        await store.UpdateAsync(envelope.StreamId, kind, next, temporal, envelope.SequenceNumber, ct);
    }

    public Task SupersedeAllForRebuildAsync(DateTimeOffset supersededAt, CancellationToken ct = default)
        => store.SupersedeAllAsync(kind, supersededAt, ct);
}
