using Babelstone.Orchestrator.Dispatch;
using Babelstone.Orchestrator.Saga;
using Microsoft.Extensions.DependencyInjection;

namespace Babelstone.Families.PersonalLoan.Orchestration;

/// <summary>
/// The personal-loan family's saga module (ADR-IC-018 §D1/§D4/§P4; bd babelstone-9z9w) — the plug-in by
/// which the loan family joins the orchestrator host's assembly-scan discovery. In plain English: until
/// this module existed, the orchestrator had never heard of the <c>personal_loan</c> Kafka topic, so a
/// loan's money-moving events (a disbursement's credit, an installment collection's debit) never reached
/// the settlement saga at all — the LCD-2 settlement-health gate was armed but its trigger was
/// unreachable. This module's LOAD-BEARING contribution is one declaration: the catalogue-generated
/// <see cref="FamilyIntegrationTopics"/>, which the host unions into the substrate-owned settlement
/// saga's Movement-bearing subscribe set (ADR-PC-040 §D3; ADR-IC-018 Revised 2026-07-02) — zero host
/// composition edits, exactly the open/closed property the discovery model promises.
/// </summary>
/// <remarks>
/// <para>
/// <b>The loan family owns NO saga of its own (yet).</b> Loan cash legs are effected by the
/// SUBSTRATE-owned, family-agnostic <c>SettlementProcess</c> (ADR-PC-032), auto-started per Originated
/// Movement occurrence off the <c>ce_movementorigin</c>/<c>ce_movementdirections</c> headers — the loan
/// family neither hand-codes a settlement leg nor contributes a state machine for it. There is no loan
/// analogue of the term-deposit constitution (loan origination is engine-direct at v1) or renewal saga.
/// So this module's <see cref="StateMachine"/>/<see cref="ResultEventBridge"/>/<see cref="CommandRouter"/>
/// are INERT, FAIL-CLOSED stubs that satisfy the <see cref="ISagaModule"/> contract: the machine has no
/// transitions (nothing can ever start it — <see cref="StartMode"/> is edge-started with no edge route
/// and <see cref="AutoStartRule"/> is null), the bridge maps no outcome, the router routes no command.
/// The first REAL loan saga replaces these stubs with its machine — a module-local change, zero
/// substrate/host diff (ADR-IC-018 §Consequences).
/// </para>
/// <para>
/// <b>No consume loop of its own.</b> <see cref="ConsumeTopics"/> is EMPTY: the module's saga estate is
/// empty, so there is nothing for a per-module loop to drive — the loan topic is consumed by the
/// settlement saga's OWN loop (its own consumer group, ADR-IC-018 §P4), fed by this module's
/// <see cref="FamilyIntegrationTopics"/> declaration. The substrate's <c>SagaConsumeLoop</c> treats an
/// empty topic set as a subscribe-to-nothing idle loop, so the host's unconditional per-module loop
/// registration stays untouched.
/// </para>
/// </remarks>
public sealed class PersonalLoanSagaModule : ISagaModule
{
    /// <summary>The persisted <c>saga_type</c> discriminator this module governs. No row ever carries it
    /// today (the machine is inert and nothing starts it); it exists because the substrate's registries
    /// (machine, bridge, router — all keyed by saga type) require every module to name one.</summary>
    public const string Type = "PersonalLoanProcess";

    private readonly InertStateMachine _machine = new();
    private readonly InertResultEventBridge _bridge = new();
    private readonly InertCommandRouter _router = new();

    /// <summary>
    /// Construct the module from the host-supplied context — the <c>(SagaModuleContext)</c> activation
    /// contract <c>SagaModuleLoader</c> requires (ADR-IC-018 §P4). The context is validated but unused:
    /// this module wires no command router endpoints and no store (its saga estate is empty).
    /// </summary>
    public PersonalLoanSagaModule(SagaModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
    }

    /// <inheritdoc />
    public string SagaType => Type;

    /// <inheritdoc />
    public ISagaStateMachine StateMachine => _machine;

    /// <inheritdoc />
    public IResultEventBridge ResultEventBridge => _bridge;

    /// <inheritdoc />
    public ISagaCommandRouter CommandRouter => _router;

    /// <summary>EMPTY — this module runs no saga, so its consume loop subscribes to nothing (see the
    /// class remarks). The loan integration topic is consumed by the settlement saga's own loop.</summary>
    public IReadOnlyList<string> ConsumeTopics => [];

    /// <summary>
    /// The personal-loan family-INTEGRATION topics (the catalogue-generated constants, CI-gated against
    /// the AsyncAPI catalogue by <c>gen-saga-topics-check</c>) — THE reason this module exists: the HOST
    /// derives the substrate settlement saga's Movement-bearing subscribe set from the DISCOVERED
    /// modules' declarations without naming this family (ADR-PC-040 §D3; ADR-IC-018 Revised 2026-07-02),
    /// so loan Originated Movements drive <c>SettlementProcess</c> exactly as term-deposit's do.
    /// Namespace-qualified: the property shares the generated class's name.
    /// </summary>
    public IReadOnlyList<string> FamilyIntegrationTopics =>
        global::Babelstone.Families.PersonalLoan.Orchestration.FamilyIntegrationTopics.All;

    /// <summary>A named group for the module's (empty-subscription, idle) loop — never contended, since
    /// the loop subscribes to nothing; distinct from every real group per ADR-IC-018 §P4.</summary>
    public string ConsumerGroupId => "babelstone-orchestrator-personal-loan";

    /// <inheritdoc />
    public SagaStartMode StartMode => SagaStartMode.EdgeStarted;

    /// <inheritdoc />
    public AutoStartRule? AutoStartRule => null;

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, SagaModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Nothing to register: no typed command sink (the inert machine emits no command), no business
        // store, no agent status map (no edge read serves this saga type). The settlement machinery that
        // consumes this family's topics is the substrate's own module, registered by the host.
    }

    /// <summary>
    /// The inert loan state machine: no transitions, so ANY (state, event) pair is rejected fail-closed
    /// (ADR-IC-003 §P2 — the table is the specification, and this table is empty). Nothing can start an
    /// instance (no edge route, no auto-start rule), so no <c>saga_state</c> row ever carries
    /// <see cref="Type"/>; if one ever did (a wiring defect), every event at it would be a NoTransition
    /// reject, never a silent advance.
    /// </summary>
    private sealed class InertStateMachine : ISagaStateMachine
    {
        public string SagaType => Type;

        /// <summary>Never persisted — nothing starts this saga; named for the contract.</summary>
        public string InitialState => "UNUSED";

        public bool TryAdvance(string current, string eventType, out TransitionOutcome outcome)
        {
            outcome = default!;
            return false;
        }

        public bool IsTerminal(string state) => false;
    }

    /// <summary>The inert bridge: no command outcome maps to a result event (an unmapped pair is the
    /// substrate's graceful no-op — and this module emits no command to have an outcome).</summary>
    private sealed class InertResultEventBridge : IResultEventBridge
    {
        public string SagaType => Type;

        public string? ForOutcome(string commandType, CommandDeliveryKind kind) => null;
    }

    /// <summary>The inert router: no command routes anywhere (the inert machine decides none; an
    /// unroutable command is the dispatcher's fail-closed terminal FAILED, never a silent drop).</summary>
    private sealed class InertCommandRouter : ISagaCommandRouter
    {
        public string SagaType => Type;

        public CommandRoute? Resolve(string commandType) => null;

        public CommandRoute? Resolve(string commandType, string sagaType) => null;
    }
}
