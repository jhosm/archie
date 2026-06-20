using Xunit;

namespace Babelstone.LoadHarness.Runner.Tests;

/// <summary>
/// Testcontainers integration coverage for <see cref="LoadRunner"/> — the composition root that turns the
/// harness primitives into a runnable load test (ADR-PC-011 §G4). It drives <c>RunAsync</c> end to end
/// (smoke-latency and replay) against a Testcontainers PostgreSQL event store with the bus path SKIPPED
/// (<c>--no-bus</c>, i.e. <c>BootstrapServers = null</c>), so the §G2 measured path is exercised without a
/// broker (bd babelstone-2e6q.7). The live-Redpanda §G1 bus path is covered separately by
/// <see cref="LoadRunnerBusIntegrationTests"/>.
/// </summary>
/// <remarks>
/// In plain English: this runs the whole load-test conductor — generate traffic, drive the engine's real
/// append/project code, read the latency from the engine's telemetry, fold a PASS/FAIL — against a real
/// database in Docker, on tiny rates and durations so it finishes in seconds. It replaces the
/// hand-run-only verification those code paths had with an automated, measured one.
///
/// Carries <c>[Trait("Category", "Integration")]</c> so the unit lane skips it; the CI integration lane
/// (<c>--filter "Category=Integration"</c>) runs it.
/// </remarks>
[Trait("Category", "Integration")]
[Collection("RunnerPostgres")]
public sealed class LoadRunnerIntegrationTests
{
    private readonly RunnerPostgresFixture _pg;

    public LoadRunnerIntegrationTests(RunnerPostgresFixture pg) => _pg = pg;

    // A fast, in-process-only run: tiny rate + sub-second drive so the suite stays quick, the bus skipped
    // (BootstrapServers null → BuildBusDriver returns null), a fresh run nonce so repeated runs never
    // collide on stream ids.
    private RunnerOptions FastInProcess(RunProfile profile, MeasureMode measure) => new()
    {
        Profile = profile,
        Measure = measure,
        Seed = 1234,
        RunId = Guid.NewGuid(),
        TargetTps = 20.0,
        Duration = TimeSpan.FromMilliseconds(300),
        WarmupEvents = 2,
        BootstrapServers = null, // --no-bus: in-process append/projection only
    };

    [Fact]
    public async Task Smoke_latency_run_produces_events_and_evaluates_the_sync_bands()
    {
        var runner = new LoadRunner(FastInProcess(RunProfile.Smoke, MeasureMode.Latency), TextWriter.Null);

        var artefact = await runner.RunAsync();

        // The run actually drove the engine append path (events produced) and evaluated the §8.3 sync
        // bands from the engine's OWN span durations — the artefact leads with a verdict and the seed.
        Assert.True(artefact.EventsProduced > 0, "a 300ms drive at 20 TPS should produce at least one event");
        Assert.NotEmpty(artefact.Verdicts);
        Assert.Equal(1234, artefact.Seed);
        // Smoke profile emits no throughput verdict; latency-only artefact has no replay/no-divergence legs.
        Assert.Null(artefact.Throughput);
        Assert.Null(artefact.Replay);
        Assert.Null(artefact.NoDivergence);
        Assert.Contains("seed=1234", artefact.Summary());
    }

    [Fact]
    public async Task Sustained_latency_run_folds_a_throughput_verdict()
    {
        var runner = new LoadRunner(FastInProcess(RunProfile.Sustained, MeasureMode.Latency), TextWriter.Null);

        var artefact = await runner.RunAsync();

        // A non-smoke profile adds the throughput verdict keyed off the sustained phase.
        Assert.True(artefact.EventsProduced > 0);
        Assert.NotNull(artefact.Throughput);
        Assert.Equal("sustained", artefact.Throughput!.Profile);
    }

    [Fact]
    public async Task Replay_run_measures_the_cold_rebuild_budget_and_the_no_divergence_invariant()
    {
        var runner = new LoadRunner(FastInProcess(RunProfile.Smoke, MeasureMode.Replay), TextWriter.Null);

        var artefact = await runner.RunAsync();

        // The replay path populates, drains, times a cold rebuild of one stream, then runs the
        // no-rebuild-divergence drill — both verdicts are present and a clean run diverges on no stream.
        Assert.True(artefact.EventsProduced > 0, "the replay path populates before measuring");
        Assert.NotNull(artefact.Replay);
        Assert.NotNull(artefact.NoDivergence);
        Assert.Equal(0, artefact.NoDivergence!.DivergentStreams);
        Assert.True(artefact.Replay!.ObservedMs < artefact.Replay.BudgetMs,
            $"a small cold rebuild ({artefact.Replay.ObservedMs}ms) should clear the {artefact.Replay.BudgetMs}ms budget");
    }
}
