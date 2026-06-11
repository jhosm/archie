using Xunit;

namespace Babelstone.Engine.Analyzers.Tests;

/// <summary>
/// BENG004 — the STRUCTURAL/semantic half of <c>NO_CLOCK_DRIVEN_ENGINE_SIGNAL</c>
/// (ADR-PC-023 §D1). An engine signal/event emission caused by the clock is flagged; one
/// caused by a command/domain fact (time carried in as a value) is clean. The cases below
/// pivot on the headline gap the lexical name-scan (PR #122) cannot close: an off-list
/// clock-driven type name (<c>DepositMaturityForecast</c>) is still caught here because the
/// proof is that a clock value FLOWS into the emit, not what the emit is NAMED.
/// </summary>
public sealed class NoClockDrivenEngineSignalAnalyzerTests
{
    private static Task<string[]> Ids(string source) =>
        AnalyzerHarness.DiagnosticIdsAsync(source, new NoClockDrivenEngineSignalAnalyzer());

    // A real DomainEvent type plus a decider-ish emit site, parameterised on the body.
    private static string Emit(string emit) => $$"""
        using System;
        using Babelstone.Engine;

        public sealed record DepositMaturityForecast(DateOnly AsOf) : DomainEvent;
        public sealed record DepositMatured(DateOnly MaturityDate) : DomainEvent;

        public static class Decider
        {
            public static DomainEvent Decide(DateOnly maturityDate)
            {
                {{emit}}
            }
        }
        """;

    [Fact]
    public async Task A_clock_driven_event_emit_is_BENG004()
    {
        // The emitted event's date comes from reading the clock — its ONLY cause is "today
        // arrived". This is the forbidden clock-driven signal (ADR-PC-023 §D1).
        var src = Emit("return new DepositMaturityForecast(DateOnly.FromDateTime(DateTime.UtcNow));");
        Assert.Equal([EngineDiagnostics.ClockDrivenSignalId], await Ids(src));
    }

    [Fact]
    public async Task A_clock_value_routed_through_a_local_is_still_BENG004()
    {
        // The `var now = …` shape: the clock read lands in a local first, then flows into the
        // emit. The within-method data-flow walk must trace it back to the clock.
        var src = Emit("""
            var now = DateTime.UtcNow;
            var asOf = DateOnly.FromDateTime(now);
            return new DepositMaturityForecast(asOf);
            """);
        Assert.Equal([EngineDiagnostics.ClockDrivenSignalId], await Ids(src));
    }

    [Fact]
    public async Task A_clock_driven_scheduled_effect_is_BENG004()
    {
        // The emit need not be a DomainEvent — a ScheduledEffect minted from the clock is the
        // same forbidden shape (the side-effects-as-scheduled-events emit path).
        const string src = """
            using System;
            using Babelstone.Engine;

            public static class Scheduler
            {
                public static ScheduledEffect Fire()
                    => new ScheduledEffect("MaturityApproaching", DateTime.UtcNow);
            }
            """;
        Assert.Equal([EngineDiagnostics.ClockDrivenSignalId], await Ids(src));
    }

    [Fact]
    public async Task A_command_driven_emit_is_clean()
    {
        // The emitted event's date is the maturity date the COMMAND/decision carried in — a
        // fact about the deposit, not the passage of time. No clock flows into the emit.
        var src = Emit("return new DepositMatured(maturityDate);");
        Assert.Empty(await Ids(src));
    }

    [Fact]
    public async Task A_clock_read_that_does_not_reach_the_emit_is_clean()
    {
        // The clock is read but ONLY used for an unrelated decision (the branch), never stamped
        // onto the emitted event. The analyser proves causation by data-flow, not co-location,
        // so this is not flagged — it is the command's date that reaches the event.
        var src = Emit("""
            var trace = DateTime.UtcNow;
            _ = trace;
            return new DepositMatured(maturityDate);
            """);
        Assert.Empty(await Ids(src));
    }

    [Fact]
    public async Task A_non_signal_construction_from_the_clock_is_not_BENG004()
    {
        // Constructing an ordinary (non-DomainEvent, non-ScheduledEffect) object from the clock
        // is out of scope — this analyser governs the EMIT surface, not every clock read (BENG001
        // governs the fold). Here nothing implements the engine signal types.
        const string src = """
            using System;

            public sealed record AuditLine(DateOnly AsOf);

            public static class Logger
            {
                public static AuditLine Stamp() => new AuditLine(DateOnly.FromDateTime(DateTime.UtcNow));
            }
            """;
        Assert.Empty(await Ids(src));
    }
}
