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
