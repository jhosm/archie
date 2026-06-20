using System.Text.Json;
using Babelstone.Engine;

namespace Babelstone.Engine.Tests;

// A minimal non-PII test family standing in for the Epic E term-deposit family: a
// counter whose state folds two event types. Exercises the engine spine without
// pulling in CUE schemas or real domain math.

public sealed record CounterState(int Total);

public sealed record Incremented(int By) : DomainEvent;

public sealed record Reset : DomainEvent;

/// <summary>
/// An increment that ALSO marks a lifecycle boundary (ADR-PC-003 §P2) — the family-agnostic stand-in
/// for a term-deposit DepositConstituted/Matured/… so the engine's lifecycle-boundary snapshot trigger
/// can be exercised without pulling the term-deposit family in. Folds exactly like
/// <see cref="Incremented"/>; only its <see cref="DomainEvent.IsLifecycleBoundary"/> override differs.
/// </summary>
public sealed record LifecycleIncremented(int By) : DomainEvent
{
    public override bool IsLifecycleBoundary => true;
}

public sealed class IncrementedHandler : IEventHandler<CounterState, Incremented>
{
    public HandlerResult<CounterState> Apply(CounterState state, Incremented @event)
        => HandlerResult<CounterState>.From(state with { Total = state.Total + @event.By });
}

public sealed class ResetHandler : IEventHandler<CounterState, Reset>
{
    public HandlerResult<CounterState> Apply(CounterState state, Reset @event)
        => HandlerResult<CounterState>.From(state with { Total = 0 });
}

public sealed class LifecycleIncrementedHandler : IEventHandler<CounterState, LifecycleIncremented>
{
    public HandlerResult<CounterState> Apply(CounterState state, LifecycleIncremented @event)
        => HandlerResult<CounterState>.From(state with { Total = state.Total + @event.By });
}

public sealed class CounterFamilyModule : IFamilyModule
{
    public string FamilyName => "counter";

    public string SchemaVersion => "counter@2026.1";

    public IReadOnlyList<HandlerRegistration> Handlers =>
    [
        new("counter.Incremented", typeof(Incremented), new DispatchableHandler<CounterState, Incremented>(new IncrementedHandler())),
        new("counter.Reset", typeof(Reset), new DispatchableHandler<CounterState, Reset>(new ResetHandler())),
        new("counter.LifecycleIncremented", typeof(LifecycleIncremented), new DispatchableHandler<CounterState, LifecycleIncremented>(new LifecycleIncrementedHandler())),
    ];

    public static HandlerRegistry Registry() => new(new CounterFamilyModule().Handlers);
}

/// <summary>A plain JSON codec standing in for the deferred Avro codec (skeleton §8).</summary>
public sealed class JsonEventSerializer : IEventSerializer
{
    public EncodedPayload Encode(DomainEvent @event)
        => new(JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType()), SchemaId: 1);

    public DomainEvent Decode(ReadOnlyMemory<byte> payload, Type payloadType)
        => (DomainEvent)JsonSerializer.Deserialize(payload.Span, payloadType)!;
}

public sealed class JsonStateSerializer<TState> : IStateSerializer<TState>
{
    public byte[] Serialize(TState state) => JsonSerializer.SerializeToUtf8Bytes(state);

    public TState Deserialize(ReadOnlyMemory<byte> bytes) => JsonSerializer.Deserialize<TState>(bytes.Span)!;
}

/// <summary>A fixed clock so transaction_time is deterministic in tests (the runtime owns the clock, not handlers).</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>
/// A test clock whose "now" can be advanced between appends — so a test can place two appends in
/// different calendar months/years and exercise the runtime's calendar-boundary snapshot trigger
/// (the runtime owns the transaction_time clock; this stands in for wall-clock progression).
/// </summary>
public sealed class SettableTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}
