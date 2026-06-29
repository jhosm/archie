using Babelstone.Engine;
using Babelstone.EventStore;
using Microsoft.Extensions.DependencyInjection;

namespace Babelstone.Engine.Hosting;

/// <summary>
/// Composes the family-AGNOSTIC async projection runtime at the host composition root (ADR-PC-021 §D4):
/// the <see cref="ProjectionRegistry"/>, the <see cref="ProjectionDrainer"/>, and the in-process
/// <see cref="ProjectionRelayService"/> (two-modes §5.4 async path, ADR-PC-002 §P4). It owns NO family — the
/// registry is built lazily from EVERY registered <see cref="IProjectionModule"/>, so each family contributes
/// only its modules and the single shared relay drains them all.
/// </summary>
/// <remarks>
/// <para>
/// This used to live inside <c>TermDepositHostModule</c>, which made term_deposit the de-facto owner of shared
/// spine infrastructure: a host running another family alone (e.g. personal_loan) registered no relay, so its
/// projections and read models were never drained. Lifting it here — called ONCE from
/// <c>Program.cs</c> — restores the §D4 shape: families declare projections, the composition root owns the
/// runtime. The same co-hosted, in-process BackgroundService shape as the outbox relay.
/// </para>
/// <para>
/// The registry factory resolves <c>GetServices&lt;IProjectionModule&gt;()</c> only when the
/// relay starts (after the container is built), so registration order does not matter — every family's
/// <c>ConfigureServices</c> has run by then. Each module's <see cref="IProjectionModule.CreateRunners"/>
/// receives the shared <see cref="ProjectionInfra"/> (the family-agnostic byte storage + the store codec); a
/// CQRS read-model module ignores the bitemporal storage and folds over its own host-injected read-model store.
/// The host must already register the spine storage backends (<see cref="IProjectionStorage"/>,
/// <see cref="IProjectionCheckpointStore"/>, <see cref="IEventStore"/>, <see cref="IEventSerializer"/>,
/// <see cref="TimeProvider"/>) — it does, alongside the event store.
/// </para>
/// </remarks>
public static class ProjectionRuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the projection registry, drainer, relay options, and the relay
    /// <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>. Idempotent only if called once — call it
    /// exactly once at the composition root.
    /// </summary>
    public static IServiceCollection AddProjectionRuntime(this IServiceCollection services)
    {
        services.AddSingleton(serviceProvider =>
        {
            var infra = new ProjectionInfra(
                serviceProvider.GetRequiredService<IProjectionStorage>(),
                serviceProvider.GetRequiredService<IEventSerializer>());
            var runners = serviceProvider.GetServices<IProjectionModule>()
                .SelectMany(module => module.CreateRunners(infra));
            return new ProjectionRegistry(runners);
        });
        services.AddSingleton(serviceProvider => new ProjectionDrainer(
            serviceProvider.GetRequiredService<IEventStore>(),
            serviceProvider.GetRequiredService<IProjectionCheckpointStore>(),
            serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(new ProjectionRelayOptions());
        services.AddHostedService<ProjectionRelayService>();
        return services;
    }
}
