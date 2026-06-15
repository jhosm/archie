using System.Text.Json;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Outbox;
using Babelstone.Orchestrator.Saga;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Babelstone.Orchestrator.Edge;

/// <summary>
/// Composes the edge HTTP surface's services (I.1) into a service collection. Factored out of
/// <c>Program.cs</c> so the production host and the integration test compose the SAME edge: the
/// saga state machine + persistence stores, the <see cref="EdgeSagaStarter"/> that starts the saga,
/// the <see cref="SagaStateReader"/> the SSE stream reads, and the snake_case JSON discipline the
/// 202 body uses.
/// </summary>
/// <remarks>
/// This registers only the edge's OWN dependencies. The production <c>Program.cs</c> ALSO registers
/// the hosted consume loop (#167) + dispatcher (#170) + migration service alongside these, sharing
/// the saga stores; the test composes only the edge over a migrated PG container. Either way the
/// orchestrator stays extraction-ready (ADR-PC-019 §P2): nothing here is an engine-kernel reference.
/// </remarks>
public static class EdgeServices
{
    /// <summary>Register the edge services against <paramref name="connectionString"/> (the
    /// orchestrator runtime-role credential, resolved at the composition root).</summary>
    public static void Register(IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // snake_case on the wire (deposit_id, process_id, stream_url) — the SAME discipline the
        // engine APIs and the deposit configuration surface use.
        services.Configure<JsonOptions>(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            o.SerializerOptions.PropertyNameCaseInsensitive = false;
        });

        // The saga state machine + persistence stores the edge starts the saga through. Singletons,
        // matching the production composition. If the host already registered an ISagaStateMachine /
        // store (it composes them for the consume loop), TryAdd keeps a single shared instance.
        services.TryAddSingleton<ISagaStateMachine, ConstitutionProcess>();
        services.TryAddSingleton<SagaStateStore>();
        services.TryAddSingleton<SagaTransitionLog>();
        // The per-saga business-reference store (bd babelstone-t7o3.1): the edge writes the pinned
        // references at start, the fork + the command-payload assembly read them. Registered before
        // the sink so the sink resolves the SAME instance.
        services.TryAddSingleton<SagaBusinessReferenceStore>();
        services.TryAddSingleton<ISagaCommandSink>(sp =>
            new SagaCommandOutboxSink(sp.GetRequiredService<SagaBusinessReferenceStore>()));

        services.TryAddSingleton(new EdgeOptions { ConnectionString = connectionString });

        // No clock is registered at the edge (Fork B rework, bd babelstone-t7o3.11): the engine is now
        // the constitution authority, so it derives start_date from the constitution instant it stamps
        // (ADR-PC-010 §P5) — the edge no longer pins start_date and the saga's command bytes carry no
        // clock (matching the ProcessApiEndpoints "No clock is needed at the edge" note). The earlier
        // edge-side TimeProvider registration was the rejected v1 stand-in's and is removed.

        services.TryAddSingleton(sp => new EdgeSagaStarter(
            sp.GetRequiredService<ISagaStateMachine>(),
            sp.GetRequiredService<SagaStateStore>(),
            sp.GetRequiredService<SagaTransitionLog>(),
            sp.GetRequiredService<ISagaCommandSink>(),
            sp.GetRequiredService<SagaBusinessReferenceStore>())
        {
            StartEventType = ConstitutionProcess.ConstitutionRequested,
        });

        services.TryAddSingleton(new SagaStateReader(connectionString));
    }
}
