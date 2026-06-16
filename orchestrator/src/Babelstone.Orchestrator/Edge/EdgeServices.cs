using System.Text.Json;
using Babelstone.Families.TermDeposit.Orchestration;
using Babelstone.Orchestrator.Inbox;
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

        // The generic substrate persistence stores the edge starts the saga through. Singletons,
        // matching the production composition. If the host already registered a store (it composes them
        // for the consume loop), TryAdd keeps a single shared instance. The state machine itself, the
        // per-saga business-reference store, and the command sink are family-owned — the family's
        // ISagaModule.ConfigureServices registers them (ADR-IC-018 §P4), the host's module loop registers
        // the machines; EdgeServices no longer names the family for those.
        services.TryAddSingleton<SagaStateStore>();
        services.TryAddSingleton<SagaTransitionLog>();

        services.TryAddSingleton(new EdgeOptions { ConnectionString = connectionString });

        // No clock is registered at the edge (Fork B rework, bd babelstone-t7o3.11): the engine is now
        // the constitution authority, so it derives start_date from the constitution instant it stamps
        // (ADR-PC-010 §P5) — the edge no longer pins start_date and the saga's command bytes carry no
        // clock (matching the ProcessApiEndpoints "No clock is needed at the edge" note).

        // The edge starts the CONSTITUTION saga (it is the only SagaStartMode.EdgeStarted saga, Document
        // 05 §Step 0). The host composition root MAY name the family (ADR-IC-018 §D4 / ADR-PC-021 §A2
        // exemption), so the edge resolves the constitution machine from the host's machine registry by
        // its saga_type and pins the constitution start event. The family-owned business-ref store + sink
        // are resolved from DI (registered by the module). A renewal saga is EventAutoStarted, so it is
        // NOT wired here.
        services.TryAddSingleton(sp => new EdgeSagaStarter(
            sp.GetServices<ISagaStateMachine>().Single(m => m.SagaType == ConstitutionProcess.Type),
            sp.GetRequiredService<SagaStateStore>(),
            sp.GetRequiredService<SagaTransitionLog>(),
            // Resolve the CONSTITUTION typed sink directly (bd babelstone-mtto PR2): the edge starts only
            // the constitution saga, so it emits the constitution command bodies — NOT the multi-saga
            // CompositeSagaCommandSink the advance handler uses (whose bare EmitAsync routes by saga_type
            // and has none to route on here). The edge is already constitution-aware (it names the
            // ConstitutionProcess machine + start event), so resolving its typed sink by saga_type is the
            // same family-aware composition-root move, not new family leakage.
            sp.GetServices<ISagaTypedCommandSink>().Single(s => s.SagaType == ConstitutionProcess.Type),
            sp.GetRequiredService<SagaBusinessReferenceStore>())
        {
            StartEventType = ConstitutionProcess.ConstitutionRequested,
        });

        services.TryAddSingleton(new SagaStateReader(connectionString));
    }
}
