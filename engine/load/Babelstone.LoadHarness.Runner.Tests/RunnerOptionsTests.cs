using Babelstone.LoadHarness.Runner;
using Xunit;

namespace Babelstone.LoadHarness.Runner.Tests;

/// <summary>
/// Docker-free component tests for the host's pure CLI parsing (bd babelstone-2e6q.1..4). A typo must
/// fail loud (not silently run the default profile — a vacuous-pass risk, §8.3), and every documented
/// flag must round-trip into the validated <see cref="RunnerOptions"/>.
/// </summary>
public sealed class RunnerOptionsTests
{
    [Fact]
    public void Defaults_are_a_low_tps_latency_smoke()
    {
        var o = RunnerOptions.Parse([]);

        Assert.Equal(RunProfile.Smoke, o.Profile);
        Assert.Equal(MeasureMode.Latency, o.Measure);
        Assert.Equal(1234, o.Seed);
        Assert.True(o.TargetTps > 0);
        Assert.NotNull(o.BootstrapServers);
    }

    [Fact]
    public void Parses_the_sustained_250_tps_60s_smoke_from_the_acceptance_criteria()
    {
        // bd babelstone-2e6q.2 acceptance: --tps 250 --duration 60s.
        var o = RunnerOptions.Parse(["--profile", "sustained", "--tps", "250", "--duration", "60s"]);

        Assert.Equal(RunProfile.Sustained, o.Profile);
        Assert.Equal(250, o.TargetTps);
        Assert.Equal(TimeSpan.FromSeconds(60), o.Duration);
    }

    [Fact]
    public void Parses_the_burst_profile_from_the_acceptance_criteria()
    {
        // bd babelstone-2e6q.3 acceptance: --profile burst (1000 TPS / 15 min).
        var o = RunnerOptions.Parse(["--profile", "burst", "--burst-tps", "1000", "--burst-duration", "15m"]);

        Assert.Equal(RunProfile.Burst, o.Profile);
        Assert.Equal(1000, o.BurstTps);
        Assert.Equal(TimeSpan.FromMinutes(15), o.BurstDuration);
    }

    [Fact]
    public void Parses_the_replay_measurement_from_the_acceptance_criteria()
    {
        // bd babelstone-2e6q.4 acceptance: --measure replay.
        var o = RunnerOptions.Parse(["--measure", "replay", "--irregular"]);

        Assert.Equal(MeasureMode.Replay, o.Measure);
        Assert.True(o.IrregularReplayClass);
    }

    [Fact]
    public void No_bus_clears_the_bootstrap()
    {
        var o = RunnerOptions.Parse(["--no-bus"]);
        Assert.Null(o.BootstrapServers);
    }

    [Fact]
    public void Unknown_flag_fails_loud()
    {
        Assert.Throws<ArgumentException>(() => RunnerOptions.Parse(["--frobnicate"]));
    }

    [Fact]
    public void A_flag_without_its_value_fails_loud()
    {
        Assert.Throws<ArgumentException>(() => RunnerOptions.Parse(["--tps"]));
    }

    [Theory]
    [InlineData("90", 90)]
    [InlineData("60s", 60)]
    [InlineData("15m", 15 * 60)]
    [InlineData("24h", 24 * 60 * 60)]
    public void Duration_parses_units(string text, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RunnerOptions.ParseDuration(text));
    }

    [Fact]
    public void Duration_rejects_an_unknown_unit()
    {
        Assert.Throws<ArgumentException>(() => RunnerOptions.ParseDuration("5y"));
    }

    [Fact]
    public void Validation_rejects_a_non_positive_tps()
    {
        Assert.Throws<ArgumentException>(() => RunnerOptions.Parse(["--tps", "0"]));
    }

    [Fact]
    public void Validation_rejects_a_tolerance_outside_the_unit_interval()
    {
        Assert.Throws<ArgumentException>(() => RunnerOptions.Parse(["--tolerance", "1.0"]));
    }
}
