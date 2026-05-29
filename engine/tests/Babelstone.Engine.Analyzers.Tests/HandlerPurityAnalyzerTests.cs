using Xunit;

namespace Babelstone.Engine.Analyzers.Tests;

/// <summary>
/// BENG001/002/003 over real <c>IEventHandler.Apply</c> bodies. The build-time half of
/// the DETERMINISM_GATE.
/// </summary>
public sealed class HandlerPurityAnalyzerTests
{
    // Wraps a statement into a real handler implementing Babelstone.Engine.IEventHandler.
    private static string Handler(string body) => $$"""
        using Babelstone.Engine;

        public sealed record S(int X);
        public sealed record E : DomainEvent;

        public sealed class H : IEventHandler<S, E>
        {
            public HandlerResult<S> Apply(S state, E @event)
            {
                {{body}}
                return HandlerResult<S>.From(state);
            }
        }
        """;

    private static Task<string[]> Ids(string source) => AnalyzerHarness.DiagnosticIdsAsync(source, new HandlerPurityAnalyzer());

    [Theory]
    [InlineData("var t = System.DateTime.UtcNow;")]
    [InlineData("var t = System.DateTimeOffset.Now;")]
    [InlineData("var t = System.Environment.TickCount;")]
    public async Task Clock_in_a_handler_is_BENG001(string body)
        => Assert.Equal([EngineDiagnostics.ClockId], await Ids(Handler(body)));

    [Theory]
    [InlineData("var s = System.IO.File.ReadAllText(\"/x\");")]
    [InlineData("((System.Data.Common.DbConnection)null!).Open();")]
    public async Task Io_in_a_handler_is_BENG002(string body)
        => Assert.Equal([EngineDiagnostics.IoId], await Ids(Handler(body)));

    [Theory]
    [InlineData("var g = System.Guid.NewGuid();")]
    [InlineData("var r = new System.Random();")]
    public async Task Randomness_in_a_handler_is_BENG003(string body)
        => Assert.Equal([EngineDiagnostics.RandomnessId], await Ids(Handler(body)));

    [Fact]
    public async Task A_pure_handler_is_clean()
        => Assert.Empty(await Ids(Handler("var y = state.X + @event.GetHashCode();")));

    [Fact]
    public async Task A_clock_read_routed_through_a_private_helper_is_still_BENG001()
    {
        // The impurity is one call deep, not inline in Apply. The analyser must follow the
        // call graph within the assembly to catch it — the headline gap in review finding S2.
        const string source = """
            using Babelstone.Engine;

            public sealed record S(int X);
            public sealed record E : DomainEvent;

            public sealed class H : IEventHandler<S, E>
            {
                public HandlerResult<S> Apply(S state, E @event)
                {
                    var stamped = Stamp(state);
                    return HandlerResult<S>.From(stamped);
                }

                private static S Stamp(S state) => state with { X = (int)System.DateTime.UtcNow.Ticks };
            }
            """;
        Assert.Equal([EngineDiagnostics.ClockId], await Ids(source));
    }

    [Fact]
    public async Task A_pure_private_helper_does_not_trip_the_analyser()
    {
        // The call-graph walk must not flag a handler that calls a genuinely pure helper.
        const string source = """
            using Babelstone.Engine;

            public sealed record S(int X);
            public sealed record E : DomainEvent;

            public sealed class H : IEventHandler<S, E>
            {
                public HandlerResult<S> Apply(S state, E @event)
                    => HandlerResult<S>.From(state with { X = Double(state.X) });

                private static int Double(int x) => x * 2;
            }
            """;
        Assert.Empty(await Ids(source));
    }

    [Fact]
    public async Task The_clock_outside_a_handler_is_not_flagged()
    {
        // Same forbidden call, but not in an IEventHandler.Apply body — out of scope.
        const string source = """
            public sealed class Hosting
            {
                public long Tick() => System.DateTime.UtcNow.Ticks;
            }
            """;
        Assert.Empty(await Ids(source));
    }
}
