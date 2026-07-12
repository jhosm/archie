using Babelstone.Orchestrator.Inbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Babelstone.Orchestrator;

/// <summary>
/// Registers the orchestrator's per-module saga consume-loop <see cref="IHostedService"/>s at the
/// composition root. In plain terms: the orchestrator runs ONE Redpanda consumer per saga module, each
/// on its own consumer group; this is the one place that decides HOW those N hosted services are added
/// to DI — and that decision is load-bearing, so it lives in a single tested method rather than inline
/// in the host script.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why AddSingleton&lt;IHostedService&gt; and never AddHostedService.</b>
/// The natural call — <c>services.AddHostedService(factory)</c> — registers the hosted service through
/// <c>TryAddEnumerable</c>, which DEDUPES <see cref="IHostedService"/> descriptors by their
/// implementation TYPE. Every per-module loop has the SAME implementation type
/// (<see cref="SagaInboxConsumerService"/>), so <c>TryAddEnumerable</c> keeps only the FIRST registration
/// and silently drops the rest. In the discovered-module order the survivor was the edge-started
/// personal-loan loop, whose consume-topic set is empty (an edge-started saga auto-starts from no topic) —
/// so the one surviving consumer subscribed to NOTHING: no saga consumer group was ever joined, every
/// group sat <c>Dead</c> with <c>MEMBERS=0</c>, and no LIVE·saga could reach <c>COMPLETED</c>. It even
/// masked the consumer's own error/Subscribe logging, which lives on the very loops that were never
/// constructed. <c>AddSingleton&lt;IHostedService&gt;</c> APPENDS each descriptor with no type-dedupe, so
/// all N loops are hosted; the host still starts them in registration order (after the saga-schema
/// migration service, registered earlier).
/// </para>
/// <para>
/// <b>Per-loop isolation.</b> The loop is built inside the registration factory closure (not a shared DI
/// singleton) so each hosted service gets its OWN loop + consumer group; the <see cref="SagaAdvanceHandler"/>
/// is shared and resolved from DI. Both the service and the loop get their loggers wired so a dead consumer
/// surfaces in logs rather than stalling silently.
/// </para>
/// </remarks>
internal static class SagaConsumeLoopHosting
{
    /// <summary>
    /// Register exactly one <see cref="SagaInboxConsumerService"/> hosted service for a single saga
    /// module's <paramref name="options"/>. Called once per module; calling it N times MUST yield N
    /// hosted services (see the class remarks — this is what <c>AddHostedService</c> silently would not do).
    /// </summary>
    public static IServiceCollection AddPerModuleSagaConsumeLoop(
        this IServiceCollection services,
        SagaInboxConsumerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        return services.AddSingleton<IHostedService>(sp =>
            new SagaInboxConsumerService(
                new SagaConsumeLoop(
                    options,
                    sp.GetRequiredService<SagaAdvanceHandler>(),
                    logger: sp.GetRequiredService<ILogger<SagaConsumeLoop>>()),
                sp.GetRequiredService<ILogger<SagaInboxConsumerService>>()));
    }
}
