using Babelstone.EventStore;

namespace Babelstone.Engine;

/// <summary>
/// Runs the pure engine core (dispatch + fold) for forward projection (capability #4)
/// and counterfactual replay (#3) WITHOUT side effects (A.8). Side-effect-freedom is
/// structural, not a flag: this type has no <see cref="IEventSink"/>, no
/// <see cref="IPiiProtector"/>, no snapshot store as constructor dependencies — so it
/// physically cannot write the log/outbox, mint OpenBao material, or persist a snapshot.
/// </summary>
/// <remarks>
/// Rehydration reads the durable log read-only (A.3) and folds <em>structural</em> state;
/// it deliberately does NOT decrypt PII — state transitions run on structural fields
/// (ADR-PC-004 §P2: PII is off the structural hot path), so a simulation never needs to
/// reach OpenBao. Counterfactual inputs (pack version, rate-sheet, clock) flow in per
/// invocation; the forward-lifecycle-by-clock-advance path (A.8 AC#4/#5, ADR-PC-011) is
/// a downstream follow-up that depends on rate-sheet inputs (Epic C).
/// </remarks>
public sealed class SimulationRuntime<TState>(
    IEventStore store,
    HandlerRegistry handlers,
    IEventSerializer serializer,
    Func<TState> seedState)
{
    /// <summary>Folds a stream's committed history (read-only), then the supplied hypothetical events, into projected state.</summary>
    public async Task<TState> ProjectAsync(
        Guid streamId, IReadOnlyList<DomainEvent> hypotheticalEvents, CancellationToken ct = default)
    {
        var state = seedState();

        await foreach (var envelope in store.LoadAsync(streamId, fromSequence: 0, ct))
        {
            if (!handlers.TryResolveByEventType(envelope.EventType, out var registration))
            {
                throw new InvalidOperationException($"No handler registered for event type '{envelope.EventType}'.");
            }

            // Decode but do NOT unprotect: folding is structural; PII stays sealed.
            var @event = serializer.Decode(envelope.Payload, registration.PayloadType);
            state = (TState)registration.Handler.ApplyBoxed(state!, @event).NewState;
        }

        return Fold(state, hypotheticalEvents);
    }

    /// <summary>Pure forward projection from the seed state over a sequence of hypothetical events — no I/O at all.</summary>
    public TState ProjectFromScratch(IReadOnlyList<DomainEvent> events) => Fold(seedState(), events);

    private TState Fold(TState state, IReadOnlyList<DomainEvent> events)
    {
        foreach (var @event in events)
        {
            if (!handlers.TryResolveByPayloadType(@event.GetType(), out var registration))
            {
                throw new InvalidOperationException($"No handler registered for event payload type '{@event.GetType()}'.");
            }

            state = (TState)registration.Handler.ApplyBoxed(state!, @event).NewState;
        }

        return state;
    }
}
