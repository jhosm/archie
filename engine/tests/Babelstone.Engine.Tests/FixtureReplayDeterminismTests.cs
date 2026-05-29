using Babelstone.Engine;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// The runtime half of DETERMINISM_GATE (ADR-PC-010 §P5): a fixed fixture event
/// sequence, folded by the registered handlers, produces a BYTE-identical projection
/// across runs. The build-time half is the BENG001/002/003 analysers.
/// </summary>
public sealed class FixtureReplayDeterminismTests
{
    private static readonly DomainEvent[] Fixture =
    [
        new Incremented(10), new Incremented(5), new Reset(), new Incremented(7), new Incremented(3),
    ];

    [Fact]
    public void Folding_the_fixture_twice_yields_a_byte_identical_projection()
    {
        var serializer = new JsonStateSerializer<CounterState>();
        var sim = new SimulationRuntime<CounterState>(
            store: null!, CounterFamilyModule.Registry(), new JsonEventSerializer(), () => new CounterState(0));

        var projection = sim.ProjectFromScratch(Fixture);
        var first = serializer.Serialize(projection);
        var second = serializer.Serialize(sim.ProjectFromScratch(Fixture));

        Assert.Equal(10, projection.Total); // 10 + 5 → reset 0 → +7 +3
        Assert.Equal(first, second);        // byte-identical projection across runs
    }
}
