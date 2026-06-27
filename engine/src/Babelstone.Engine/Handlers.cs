namespace Babelstone.Engine;

/// <summary>
/// Base for cleartext domain events handlers produce and fold over. PII fields inside
/// a concrete event are plaintext here; the runtime encrypts them at the boundary
/// (ADR-PC-004) before they reach storage. Handlers never see ciphertext.
/// </summary>
public abstract record DomainEvent
{
    /// <summary>
    /// CloudEvents extension attributes this event declares for the outbox relay to promote to
    /// <c>ce_&lt;key&gt;</c> headers (ADR-IC-018). Key = the attribute name WITHOUT the <c>ce_</c>
    /// prefix, lowercase; value = the string-encoded attribute value. The relay emits one
    /// <c>ce_&lt;key&gt;</c> header per entry and names no key itself — this seam is generic and
    /// family-agnostic: the relay copies whatever an event declares. Null (the base default) means the
    /// event declares no extension headers, which is the case for every event that needs no header-only
    /// routing discriminator. Override in a concrete family event to declare routing attributes; NEVER
    /// put PII here — these values ride the durable bus as cleartext headers (ADR-PC-004).
    /// </summary>
    public virtual IReadOnlyDictionary<string, string>? IntegrationHeaders => null;

    /// <summary>
    /// Whether this event is a LIFECYCLE BOUNDARY for the snapshot policy (ADR-PC-003 / event-store
    /// §8.1): constitution, renewal, partial withdrawal, maturity, termination — the natural points where
    /// an instance's state is interpretable on its own. The runtime reads this off the just-appended
    /// events (a pure structural property of the event TYPE, never a clock read — the family marks its own
    /// lifecycle events, so the engine stays family-agnostic) and ORs it into the per-append
    /// <see cref="SnapshotContext.IsLifecycleBoundary"/>. Base default <c>false</c>: an ordinary event
    /// (e.g. an interest accrual) is no boundary. Override and return <c>true</c> on a concrete family
    /// lifecycle event to make a snapshot fire there regardless of the per-N count.
    /// </summary>
    public virtual bool IsLifecycleBoundary => false;
}

/// <summary>
/// A side effect a handler wants to happen, returned as data rather than performed.
/// The runtime turns each into an outbox row; the handler stays pure (no I/O).
/// </summary>
public sealed record ScheduledEffect(string EventType, object Payload);

/// <summary>The result of applying one event: the next state plus any scheduled effects.</summary>
public sealed record HandlerResult<TState>(TState NewState, IReadOnlyList<ScheduledEffect> PendingEffects)
{
    public static HandlerResult<TState> From(TState state) => new(state, []);
}

/// <summary>
/// A pure fold step: <c>(state, event) → state</c> (feature-design event-store §5.1).
/// No clock, no I/O, no randomness — enforced by the A.7 analysers (BENG001/002/003).
/// </summary>
public interface IEventHandler<TState, in TEvent>
    where TEvent : DomainEvent
{
    HandlerResult<TState> Apply(TState state, TEvent @event);
}

/// <summary>The dispatch-path view of a handler that does not need the closed generic type.</summary>
public interface IDispatchableHandler
{
    HandlerResult<object> ApplyBoxed(object state, DomainEvent @event);
}

/// <summary>Adapts a typed <see cref="IEventHandler{TState,TEvent}"/> to <see cref="IDispatchableHandler"/>.</summary>
public sealed class DispatchableHandler<TState, TEvent>(IEventHandler<TState, TEvent> handler) : IDispatchableHandler
    where TEvent : DomainEvent
{
    public HandlerResult<object> ApplyBoxed(object state, DomainEvent @event)
    {
        var result = handler.Apply((TState)state, (TEvent)@event);
        return new HandlerResult<object>(result.NewState!, result.PendingEffects);
    }
}

/// <summary>Resolves the handler for an event type. Built once at startup from family modules.</summary>
public interface IHandlerRegistry
{
    bool TryResolve(string eventType, out IDispatchableHandler handler);
}
