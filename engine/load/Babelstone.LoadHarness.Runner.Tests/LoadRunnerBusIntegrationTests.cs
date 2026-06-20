using Babelstone.TestFixtures;
using Xunit;

namespace Babelstone.LoadHarness.Runner.Tests;

/// <summary>
/// Testcontainers integration coverage for <see cref="LoadRunner"/>'s §G1 bus path — the production
/// producer that puts the engine's OWN Avro bytes onto live Redpanda and registers/resolves schema ids
/// against a real Schema Registry (ADR-PC-011 §G1). It drives <c>RunAsync</c> with a non-null
/// <c>BootstrapServers</c> so <c>BuildBusDriver</c> constructs the real <c>WorkloadDriver</c> +
/// <c>ConfluentSchemaIdResolver</c> and each driven event is produced onto the broker as well as appended
/// in-process — covering the bus branch the <c>--no-bus</c> suite skips (bd babelstone-2e6q.7).
/// </summary>
/// <remarks>
/// In plain English: the same load-test conductor as the no-bus suite, but this time it also pushes the
/// generated deposit events onto a real Kafka-compatible broker (Redpanda) with a real schema registry,
/// proving the production "bytes on the bus" path works end to end — all against throwaway Docker
/// containers.
///
/// Reuses the SHARED <see cref="RedpandaFixture"/> (the single source of the Redpanda broker + built-in
/// Schema Registry dev-container, also used by the outbox/inbox lanes) alongside a Testcontainers
/// PostgreSQL for the measured append path. Carries <c>[Trait("Category", "Integration")]</c>.
/// </remarks>
[Trait("Category", "Integration")]
[Collection("RunnerPostgres")]
public sealed class LoadRunnerBusIntegrationTests : IClassFixture<RedpandaFixture>
{
    private readonly RunnerPostgresFixture _pg;
    private readonly RedpandaFixture _redpanda;

    public LoadRunnerBusIntegrationTests(RunnerPostgresFixture pg, RedpandaFixture redpanda)
    {
        _pg = pg;
        _redpanda = redpanda;
    }

    [Fact]
    public async Task Smoke_run_over_the_live_bus_produces_to_redpanda_and_evaluates_the_bands()
    {
        var options = new RunnerOptions
        {
            Profile = RunProfile.Smoke,
            Measure = MeasureMode.Latency,
            Seed = 2468,
            RunId = Guid.NewGuid(),
            TargetTps = 15.0,
            Duration = TimeSpan.FromMilliseconds(300),
            WarmupEvents = 1,
            // The §G1 bus path: a real broker + Schema Registry, so BuildBusDriver builds the live
            // WorkloadDriver and ConfluentSchemaIdResolver and each event is produced onto Redpanda.
            BootstrapServers = _redpanda.BootstrapServers,
            SchemaRegistryUrl = _redpanda.SchemaRegistryUrl,
        };

        var runner = new LoadRunner(options, TextWriter.Null);

        var artefact = await runner.RunAsync();

        // The run drove both the in-process measured path AND the live producer path: events were
        // produced, the §8.3 sync bands evaluated, and the seed is named for reproduction.
        Assert.True(artefact.EventsProduced > 0, "a 300ms drive at 15 TPS should produce at least one event onto the bus");
        Assert.NotEmpty(artefact.Verdicts);
        Assert.Equal(2468, artefact.Seed);
    }
}
