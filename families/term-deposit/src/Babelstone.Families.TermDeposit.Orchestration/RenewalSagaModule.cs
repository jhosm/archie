using Babelstone.Orchestrator.Dispatch;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Saga;
using Microsoft.Extensions.DependencyInjection;

namespace Babelstone.Families.TermDeposit.Orchestration;

/// <summary>
/// The term-deposit family's RENEWAL saga module (ADR-IC-018 §D1/§D4/§P4; bd babelstone-mtto) — the
/// plug-in by which the <see cref="RenewalProcess"/> saga joins the family-agnostic substrate as the
/// SECOND saga alongside the constitution module. It supplies the concrete machine, result-event bridge,
/// and command router; declares the consume topic + consumer group its saga subscribes; declares its
/// EVENT-AUTO-START rule; and registers the family-owned command sink. A new module, ZERO substrate diff
/// beyond the generic auto-start machinery + the {process_id} URL-templating seam (ADR-IC-018
/// §Consequences) — the substrate never names this family; the host (the §D4 composition root) does.
/// </summary>
/// <remarks>
/// <para>
/// UNLIKE the constitution module (<see cref="SagaStartMode.EdgeStarted"/>), the renewal saga is
/// <see cref="SagaStartMode.EventAutoStarted"/>: it is BORN when the engine's <c>DepositMatured</c> fact
/// arrives on the <c>term_deposit</c> family integration topic with a non-NONE <c>ce_autorenewalpolicy</c>
/// header. The <see cref="AutoStartRule"/> declares that start event + the header predicate; the
/// substrate's auto-start machinery (in <c>SagaAdvanceHandler</c>) evaluates ONLY the declared rule + the
/// record's CloudEvents extension HEADERS — never the Avro payload — so the extraction-ready boundary
/// holds. It runs in its OWN Kafka consumer group (<see cref="ConsumerGroupId"/>) over the SAME family
/// topic the constitution saga reads.
/// </para>
/// <para>
/// <b>No product/role/funding config (ADR-IC-003 §A7).</b> The auto-started saga reads only the
/// <c>DepositMatured</c> headers, and the engine resolves EVERY renewal fact — product code, pricing role,
/// funding account, term — from the Matured closing deposit it loads (ADR-PC-009; bd babelstone-mtto.5). So
/// the module carries NO product-family knowledge: the command body is the minimal <c>{ new_deposit_id }</c>
/// (see <see cref="RenewalCommandPayloadFactory"/>) and the module needs no command-defaults configuration.
/// </para>
/// </remarks>
public sealed class RenewalSagaModule : ISagaModule
{
    private readonly RenewalProcess _machine = new();
    private readonly RenewalResultEvents.Bridge _bridge = new();
    private readonly RenewalCommandRouter _router;

    /// <summary>
    /// Construct the module from the host-supplied context (the configured engine endpoint the command
    /// router resolves targets against, ADR-PC-029). The module carries no product/role/funding config — the
    /// engine resolves every renewal fact from the closing deposit (ADR-PC-009; bd babelstone-mtto.5).
    /// </summary>
    public RenewalSagaModule(SagaModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _router = new RenewalCommandRouter(new SagaCommandDispatcherOptions
        {
            ConnectionString = context.RuntimeConnectionString,
            EngineBaseUrl = context.EngineBaseUrl,
            SettlementBaseUrl = context.SettlementBaseUrl,
        });
    }

    /// <inheritdoc />
    public string SagaType => RenewalProcess.Type;

    /// <inheritdoc />
    public ISagaStateMachine StateMachine => _machine;

    /// <inheritdoc />
    public IResultEventBridge ResultEventBridge => _bridge;

    /// <inheritdoc />
    public ISagaCommandRouter CommandRouter => _router;

    /// <summary>
    /// The renewal saga subscribes to the term-deposit FAMILY INTEGRATION topic ONLY — that is where the
    /// engine's <c>DepositMatured</c> START fact arrives (ADR-IC-018 §P5). It does NOT subscribe the
    /// orchestrator-produced process topic (it produces no process events of its own).
    /// </summary>
    public IReadOnlyList<string> ConsumeTopics => [SagaConsumeTopics.TermDepositIntegrationTopic];

    /// <summary>
    /// The renewal saga's OWN Kafka consumer group (ADR-IC-018 §P4) — distinct from the constitution
    /// group, so the two sagas read the shared <c>term_deposit</c> topic independently with no shared-group
    /// contention.
    /// </summary>
    public string ConsumerGroupId => "babelstone-orchestrator-renewal";

    /// <inheritdoc />
    public SagaStartMode StartMode => SagaStartMode.EventAutoStarted;

    /// <summary>
    /// Start a renewal instance on a <c>DepositMatured</c> fact whose <c>ce_autorenewalpolicy</c> header is
    /// present and NOT <c>NONE</c> (ADR-IC-018 §P5/§D5). A NONE-policy deposit terminates at maturity and
    /// never renews, so its <c>DepositMatured</c> starts NO saga. The substrate evaluates this predicate on
    /// the record's extension-attribute HEADERS only (never the payload).
    /// </summary>
    public AutoStartRule? AutoStartRule => new AutoStartRule(
        StartEventType: RenewalProcess.DepositMatured,
        HeaderPredicate: headers =>
            headers.TryGetValue("autorenewalpolicy", out var policy)
            && !string.Equals(policy, "NONE", StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, SagaModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The renewal saga's command sink — the family typed sink the multi-saga CompositeSagaCommandSink
        // routes RenewalProcess commands to (ADR-IC-018 §D2). It carries NO per-saga state and NO
        // product/role/funding config (UNLIKE the constitution module): the engine resolves EVERY renewal
        // fact from the Matured closing deposit it loads (ADR-PC-009; bd babelstone-mtto.5), so the command
        // body is the minimal { new_deposit_id }. Registered as the typed-sink contract so it joins the
        // saga_type → sink registry the host's composite builds.
        services.AddSingleton<ISagaTypedCommandSink>(sp =>
            new RenewalCommandOutboxSink(sp.GetRequiredService<SagaOutboxWriter>()));
    }
}
