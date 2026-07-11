using Babelstone.Orchestrator.Dispatch;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Saga;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Babelstone.Families.TermDeposit.Orchestration;

/// <summary>
/// The term-deposit family's saga module (ADR-IC-018 §D1/§D4/§P4) — the plug-in by which the
/// <see cref="ConstitutionProcess"/> saga joins the family-agnostic substrate. It supplies the concrete
/// machine, result-event bridge, and command router; declares the consume topics + consumer group its
/// saga subscribes; and registers the family-owned DI services (the per-saga business-reference store and
/// the outbox command sink). The host composes by looping over the registered modules — the substrate
/// never names this family (ADR-IC-018 §D2/§P2); the host (the §D4 composition root) is the only place
/// that does.
/// </summary>
/// <remarks>
/// The constitution saga is <see cref="SagaStartMode.EdgeStarted"/> (an explicit HTTP call to the edge
/// starts it, Document 05 §Step 0), so <see cref="AutoStartRule"/> is null and the host wires the edge
/// starter for it. The renewal saga (bd babelstone-mtto) will ship as a SECOND module on this same
/// substrate with <see cref="SagaStartMode.EventAutoStarted"/> and a header-keyed
/// <c>AutoStartRule</c> — a new module, zero substrate diff (ADR-IC-018 §Consequences).
/// </remarks>
public sealed class TermDepositSagaModule : ISagaModule
{
    private readonly ConstitutionProcess _machine = new();
    private readonly ConstitutionResultEvents.Bridge _bridge = new();
    private readonly SagaCommandRouter _router;

    /// <summary>
    /// Construct the module from the host-supplied context (the configured engine + settlement endpoints
    /// the command router resolves command targets against, ADR-PC-029).
    /// </summary>
    public TermDepositSagaModule(SagaModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _router = new SagaCommandRouter(new SagaCommandDispatcherOptions
        {
            ConnectionString = context.RuntimeConnectionString,
            EngineBaseUrl = context.EngineBaseUrl,
            SettlementBaseUrl = context.SettlementBaseUrl,
            // The engine-owned CA settlement base URL (bd babelstone-u79p.3; ADR-PC-043). When configured,
            // an engine-ca funding leg routes here; when null the constitution funding stays on the legacy
            // ACL (SettlementBaseUrl), unchanged — so an estate that has not stood up the engine-CA surface
            // keeps the pre-ADR-PC-043 behaviour with no config change.
            EngineCaSettlementBaseUrl = context.EngineCaSettlementBaseUrl,
        });
    }

    /// <inheritdoc />
    public string SagaType => ConstitutionProcess.Type;

    /// <inheritdoc />
    public ISagaStateMachine StateMachine => _machine;

    /// <inheritdoc />
    public IResultEventBridge ResultEventBridge => _bridge;

    /// <inheritdoc />
    public ISagaCommandRouter CommandRouter => _router;

    /// <inheritdoc />
    public IReadOnlyList<string> ConsumeTopics => SagaConsumeTopics.ConstitutionProcessTopics;

    /// <summary>
    /// The term-deposit family-INTEGRATION topics (the catalogue-generated constants, CI-gated against
    /// the AsyncAPI catalogue) — declared so the HOST can derive the substrate settlement saga's
    /// Movement-bearing subscribe set from the DISCOVERED modules without naming this family
    /// (ADR-PC-040 §D3; ADR-IC-018 Revised 2026-07-02). Namespace-qualified: the property shares the
    /// generated class's name.
    /// </summary>
    public IReadOnlyList<string> FamilyIntegrationTopics =>
        global::Babelstone.Families.TermDeposit.Orchestration.FamilyIntegrationTopics.All;

    /// <summary>
    /// The Kafka consumer-group id the constitution consume loop uses. This MUST equal the value the host
    /// used before the substrate split (<c>"babelstone-orchestrator"</c>, the <c>Kafka:GroupId</c> default
    /// in the old Program.cs) so existing committed offsets are preserved — a different group would
    /// re-read committed offsets from the beginning, a behaviour change (ADR-IC-018 Risk 3 / §P4).
    /// </summary>
    public string ConsumerGroupId => "babelstone-orchestrator";

    /// <inheritdoc />
    public SagaStartMode StartMode => SagaStartMode.EdgeStarted;

    /// <inheritdoc />
    public AutoStartRule? AutoStartRule => null;

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, SagaModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The per-saga business-reference store (bd babelstone-t7o3.1): the edge writes the pinned
        // references at start; the approval-fork hook and the command-payload assembly read them. A shared
        // singleton the edge starter, the sink, and the machine's post-advance hook all use.
        services.TryAddSingleton<SagaBusinessReferenceStore>();

        // The constitution outbox command sink (H.2): each command the saga decides is a saga_outbox row
        // committed in the SAME transaction as the state move. The sink owns ONLY the constitution-specific
        // SagaCommandPayloadFactory assembly (a family service, ADR-IC-018 §D2); the row write itself is the
        // substrate-owned SagaOutboxWriter (ADR-IC-018 §D2 names saga_outbox a substrate store). The
        // substrate keeps the ISagaCommandSink port + the RecordingCommandSink test double + the
        // SagaOutboxWriter. Registered as the TYPED-sink contract (bd babelstone-mtto PR2) so it joins the
        // saga_type → sink registry the multi-saga host's CompositeSagaCommandSink builds; a second saga's
        // typed sink registers alongside without collision.
        services.AddSingleton<ISagaTypedCommandSink>(sp =>
            new SagaCommandOutboxSink(
                sp.GetRequiredService<SagaBusinessReferenceStore>(),
                sp.GetRequiredService<SagaOutboxWriter>()));

        // The agent-facing process-status map (bd babelstone-vjoi / Document 11 Pattern 2): the COARSE
        // saga-state → AgentStatus projection the MCP get_process_status polling tool surfaces. A
        // family-owned artifact (the family owns its state MEANING, ADR-IC-018 §D3) the edge resolves by
        // saga_type — exactly as it resolves the machine for the terminality flag. Registered here next to
        // the typed sink so it joins the saga_type → status-map registry the host builds; an EDGE-only
        // consumer (the advance handler never reads it).
        services.AddSingleton<ISagaAgentStatusMap>(new ConstitutionProcessAgentStatusMap());
    }
}
