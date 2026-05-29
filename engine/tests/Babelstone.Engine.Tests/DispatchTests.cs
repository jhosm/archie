using System.Reflection;
using Babelstone.Engine;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// Pure dispatch + fold (no I/O) — default lane. These pin the engine core's
/// determinism, the runtime half of the DETERMINISM_GATE that A.7 also guards.
/// </summary>
public sealed class DispatchTests
{
    private static readonly HandlerRegistry Registry = CounterFamilyModule.Registry();

    [Fact]
    public void Registry_resolves_a_registered_handler_and_folds()
    {
        Assert.True(Registry.TryResolve("counter.Incremented", out var handler));
        var result = handler.ApplyBoxed(new CounterState(5), new Incremented(3));
        Assert.Equal(8, ((CounterState)result.NewState).Total);
    }

    [Fact]
    public void Registry_does_not_resolve_an_unknown_event_type()
        => Assert.False(Registry.TryResolve("counter.Unknown", out _));

    [Fact]
    public void Duplicate_event_type_registration_is_rejected()
    {
        var dup = new IncrementedHandler();
        Assert.Throws<InvalidOperationException>(() => new HandlerRegistry(
        [
            new("counter.Incremented", typeof(Incremented), new DispatchableHandler<CounterState, Incremented>(dup)),
            new("counter.Incremented", typeof(Incremented), new DispatchableHandler<CounterState, Incremented>(dup)),
        ]));
    }

    [Fact]
    public void Family_module_loader_discovers_the_module_in_this_assembly()
    {
        var loader = new FamilyModuleLoader();
        var modules = loader.LoadAll([Assembly.GetExecutingAssembly()]);
        Assert.Contains(modules, m => m.FamilyName == "counter");
    }

    [Fact]
    public void Forward_projection_is_a_deterministic_fold()
    {
        var sim = new SimulationRuntime<CounterState>(
            store: null!, // ProjectFromScratch never reads the store
            handlers: Registry,
            serializer: new JsonEventSerializer(),
            seedState: () => new CounterState(0));

        DomainEvent[] events = [new Incremented(10), new Incremented(5), new Reset(), new Incremented(2)];

        var first = sim.ProjectFromScratch(events);
        var second = sim.ProjectFromScratch(events);

        Assert.Equal(2, first.Total);       // 10 + 5 → reset 0 → +2
        Assert.Equal(first, second);        // identical fold across runs (determinism)
    }
}
