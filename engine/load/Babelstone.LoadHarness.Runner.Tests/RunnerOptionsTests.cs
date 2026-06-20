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
        // A non-positive --tps is caught in the PARSE layer (ParsePositiveDouble throws a plain
        // ArgumentException) before Validate() runs — hence ArgumentException here, whereas the
        // analogous zero-duration case below is caught by Validate() and throws ArgumentOutOfRangeException.
        Assert.Throws<ArgumentException>(() => RunnerOptions.Parse(["--tps", "0"]));
    }

    [Fact]
    public void Validation_rejects_a_tolerance_outside_the_unit_interval()
    {
        Assert.Throws<ArgumentException>(() => RunnerOptions.Parse(["--tolerance", "1.0"]));
    }

    [Fact]
    public void A_tolerance_inside_the_unit_interval_is_accepted()
    {
        // The complement of the rejection above: the ParseFraction happy path + the Validate tolerance
        // guard's pass branch (0.25 ∈ [0, 1)).
        var o = RunnerOptions.Parse(["--tolerance", "0.25"]);
        Assert.Equal(0.25, o.Tolerance);
    }

    [Fact]
    public void Explicit_latency_measure_parses()
    {
        // The default is latency, but parsing "--measure latency" must still hit the ParseMeasure arm.
        var o = RunnerOptions.Parse(["--measure", "latency"]);
        Assert.Equal(MeasureMode.Latency, o.Measure);
    }

    [Fact]
    public void Explicit_smoke_profile_parses()
    {
        var o = RunnerOptions.Parse(["--profile", "smoke"]);
        Assert.Equal(RunProfile.Smoke, o.Profile);
    }

    [Fact]
    public void An_unknown_profile_fails_loud()
    {
        Assert.Throws<ArgumentException>(() => RunnerOptions.Parse(["--profile", "banana"]));
    }

    [Fact]
    public void An_unknown_measure_fails_loud()
    {
        Assert.Throws<ArgumentException>(() => RunnerOptions.Parse(["--measure", "telepathy"]));
    }

    [Fact]
    public void Seed_run_id_and_warmup_round_trip()
    {
        // §8.5 reproducibility: (seed, run-id) are the reproduction key, and warmup tunes the steady-state
        // measurement window — all three must parse off the command line.
        var runId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var o = RunnerOptions.Parse(["--seed", "42", "--run-id", runId.ToString(), "--warmup", "3"]);

        Assert.Equal(42, o.Seed);
        Assert.Equal(runId, o.RunId);
        Assert.Equal(3, o.WarmupEvents);
    }

    [Fact]
    public void Warmup_zero_disables_the_warmup()
    {
        var o = RunnerOptions.Parse(["--warmup", "0"]);
        Assert.Equal(0, o.WarmupEvents);
    }

    [Fact]
    public void Endpoint_flags_override_the_defaults()
    {
        var o = RunnerOptions.Parse(
        [
            "--pg", "Host=db;Database=load",
            "--bootstrap", "redpanda:9092",
            "--schema-registry", "http://sr:8081",
        ]);

        Assert.Equal("Host=db;Database=load", o.PostgresConnectionString);
        Assert.Equal("redpanda:9092", o.BootstrapServers);
        Assert.Equal("http://sr:8081", o.SchemaRegistryUrl);
    }

    [Fact]
    public void A_non_positive_burst_tps_fails_loud()
    {
        Assert.Throws<ArgumentException>(() => RunnerOptions.Parse(["--burst-tps", "0"]));
    }

    [Fact]
    public void Validation_rejects_a_zero_duration()
    {
        // A bare "0" parses to TimeSpan.Zero (ParseDuration accepts it — it is not negative), so the
        // non-positive-duration guard lives in Validate, not the duration parser. ArgumentOutOfRangeException
        // is the exact type Validate throws.
        Assert.Throws<ArgumentOutOfRangeException>(() => RunnerOptions.Parse(["--duration", "0"]));
    }

    [Fact]
    public void Duration_rejects_a_negative_value()
    {
        Assert.Throws<ArgumentException>(() => RunnerOptions.ParseDuration("-5s"));
    }

    [Fact]
    public void Duration_rejects_blank_input()
    {
        Assert.Throws<ArgumentException>(() => RunnerOptions.ParseDuration("   "));
    }

    // --- L.5 / L.3e / L.6 measure modes and their flags (bd 0uau.1, 2e6q.5, 0uau.2) ---

    [Fact]
    public void Parses_the_snapshot_replay_measure_and_depth()
    {
        // bd babelstone-0uau.1 acceptance: --measure snapshot-replay over a deep stream.
        var o = RunnerOptions.Parse(["--measure", "snapshot-replay", "--depth", "128", "--irregular"]);

        Assert.Equal(MeasureMode.SnapshotReplay, o.Measure);
        Assert.Equal(128, o.SnapshotStreamDepth);
        Assert.True(o.IrregularReplayClass);
    }

    [Fact]
    public void Parses_the_repl_latency_measure_samples_and_standby_flag()
    {
        // bd babelstone-2e6q.5 acceptance: --measure repl-latency, GATING only with --standby-confirmed.
        var o = RunnerOptions.Parse(["--measure", "repl-latency", "--repl-samples", "100", "--standby-confirmed"]);

        Assert.Equal(MeasureMode.ReplLatency, o.Measure);
        Assert.Equal(100, o.ReplLatencySamples);
        Assert.True(o.StandbyConfirmed);
    }

    [Fact]
    public void Repl_latency_is_advisory_by_default_without_the_standby_flag()
    {
        var o = RunnerOptions.Parse(["--measure", "repl-latency"]);
        Assert.False(o.StandbyConfirmed);
    }

    [Fact]
    public void Parses_the_discard_rebuild_measure()
    {
        // bd babelstone-0uau.2 acceptance: --measure discard-rebuild on populated snapshots.
        var o = RunnerOptions.Parse(["--measure", "discard-rebuild"]);
        Assert.Equal(MeasureMode.DiscardRebuild, o.Measure);
    }

    [Fact]
    public void Default_depth_and_repl_samples_are_sane()
    {
        var o = RunnerOptions.Parse([]);
        Assert.Equal(64, o.SnapshotStreamDepth);
        Assert.Equal(50, o.ReplLatencySamples);
        Assert.False(o.StandbyConfirmed);
    }

    [Fact]
    public void Validation_rejects_a_depth_below_two()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RunnerOptions.Parse(["--depth", "1"]));
    }

    [Fact]
    public void A_non_positive_depth_fails_loud_in_the_parser()
    {
        Assert.Throws<ArgumentException>(() => RunnerOptions.Parse(["--depth", "0"]));
    }

    [Fact]
    public void A_non_positive_repl_samples_fails_loud()
    {
        Assert.Throws<ArgumentException>(() => RunnerOptions.Parse(["--repl-samples", "0"]));
    }
}
