using Babelstone.Orchestrator;
using Babelstone.Orchestrator.Inbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// Regression guard for the "one Redpanda consumer per saga module" hosting invariant. In plain terms:
/// the orchestrator runs one consume loop per saga module, each on its own consumer group. A subtle .NET
/// dependency-injection trap once made only the FIRST of those consumers actually start — so no saga
/// consumer group was ever joined and no LIVE·saga could complete. These tests lock in the fix: every
/// per-module loop must be hosted, and they pin the framework behaviour that made the naive registration
/// wrong. The mechanism is documented on <see cref="SagaConsumeLoopHosting.AddPerModuleSagaConsumeLoop"/>.
/// </summary>
public sealed class SagaConsumeLoopHostingTests
{
    private static SagaInboxConsumerOptions OptionsFor(string group, params string[] topics) =>
        new()
        {
            ConnectionString = "Host=unused;Database=unused;Username=unused;Password=unused",
            BootstrapServers = "unused:9092",
            GroupId = group,
            Topics = topics,
        };

    [Fact]
    public void AddPerModuleSagaConsumeLoop_hosts_one_consumer_per_module_despite_the_shared_type()
    {
        // Four modules mirror the real host: three edge/event loops plus one whose consume-topic set is
        // empty (the edge-started personal-loan case that was the sole survivor of the bug). All share the
        // SagaInboxConsumerService implementation type — the exact condition AddHostedService dedupes on.
        var services = new ServiceCollection();

        services.AddPerModuleSagaConsumeLoop(OptionsFor("babelstone-orchestrator-personal-loan"));
        services.AddPerModuleSagaConsumeLoop(OptionsFor("babelstone-orchestrator-renewal", "term_deposit"));
        services.AddPerModuleSagaConsumeLoop(
            OptionsFor("babelstone-orchestrator", "deposits.process.events", "term_deposit"));
        services.AddPerModuleSagaConsumeLoop(
            OptionsFor("babelstone-orchestrator-settlement", "personal_loan", "term_deposit"));

        var hostedRegistrations = services.Count(d => d.ServiceType == typeof(IHostedService));

        // All FOUR must be registered. With the old AddHostedService this would collapse to 1 (see below).
        Assert.Equal(4, hostedRegistrations);
    }

    [Fact]
    public void AddHostedService_collapses_same_typed_services_while_AddSingleton_does_not()
    {
        // Pins the framework behaviour the fix depends on, so a future "simplification" back to
        // AddHostedService is caught here with a self-explaining failure rather than a silent dead consumer.
        var viaAddHostedService = new ServiceCollection();
        viaAddHostedService.AddHostedService(_ => new NoopHostedService());
        viaAddHostedService.AddHostedService(_ => new NoopHostedService());
        Assert.Equal(1, viaAddHostedService.Count(d => d.ServiceType == typeof(IHostedService)));

        var viaAddSingleton = new ServiceCollection();
        viaAddSingleton.AddSingleton<IHostedService>(_ => new NoopHostedService());
        viaAddSingleton.AddSingleton<IHostedService>(_ => new NoopHostedService());
        Assert.Equal(2, viaAddSingleton.Count(d => d.ServiceType == typeof(IHostedService)));
    }

    private sealed class NoopHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
