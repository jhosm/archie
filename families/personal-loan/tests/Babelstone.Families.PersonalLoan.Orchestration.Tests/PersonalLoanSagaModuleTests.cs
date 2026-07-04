using Babelstone.Orchestrator.Saga;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Babelstone.Families.PersonalLoan.Orchestration.Tests;

/// <summary>
/// The personal-loan saga module's declaration contract (ADR-IC-018 §D1/§P4; bd babelstone-9z9w). In plain
/// English: this module exists so the orchestrator learns which Kafka topic carries the loan family's
/// money-moving events — everything else on it is deliberately inert. These pin exactly that shape: the
/// catalogue-generated integration-topic declaration the host unions into the settlement saga's subscribe
/// set, the empty consume set (the module runs no saga of its own), the loader's <c>(SagaModuleContext)</c>
/// activation contract, and the fail-closed inertness of the stub machine/bridge/router.
/// </summary>
public sealed class PersonalLoanSagaModuleTests
{
    private static SagaModuleContext Context => new(
        RuntimeConnectionString: "Host=localhost;Database=test",
        EngineBaseUrl: "http://engine.invalid",
        SettlementBaseUrl: "http://settlement.invalid");

    [Fact]
    public void Declares_the_catalogue_generated_family_integration_topics()
    {
        // THE load-bearing declaration (ADR-PC-040 §D3; ADR-IC-018 Revised 2026-07-02): the host derives
        // the substrate settlement saga's Movement-bearing subscribe set from the discovered modules'
        // FamilyIntegrationTopics — this is what joins LoanDisbursed / LoanInstallmentPaid Originated
        // Movements to SettlementProcess. Asserted against the generated constants (CI-gated against the
        // AsyncAPI catalogue by gen-saga-topics-check), and pinned to the wire topic so a silently emptied
        // manifest cannot pass.
        var module = new PersonalLoanSagaModule(Context);

        Assert.Equal(FamilyIntegrationTopics.All, module.FamilyIntegrationTopics);
        Assert.Contains("personal_loan", module.FamilyIntegrationTopics);
    }

    [Fact]
    public void Runs_no_saga_of_its_own()
    {
        // The loan family's saga estate is empty: no consume loop work (empty topic set — the substrate's
        // SagaConsumeLoop idles on it), no edge start, no auto-start rule. The loan topic is consumed by
        // the settlement saga's OWN loop, in its own consumer group (ADR-IC-018 §P4).
        var module = new PersonalLoanSagaModule(Context);

        Assert.Empty(module.ConsumeTopics);
        Assert.Equal(SagaStartMode.EdgeStarted, module.StartMode);
        Assert.Null(module.AutoStartRule);
    }

    [Fact]
    public void Carries_the_loader_activation_contract_and_a_consistent_saga_type()
    {
        // SagaModuleLoader activates a discovered module through its (SagaModuleContext) constructor
        // (ADR-IC-018 §D6 Revised 2026-07-02) — the shape both shipped term-deposit modules use. And every
        // contributed piece must agree on the saga_type discriminator (the substrate's machine/bridge/router
        // registries are all keyed on it).
        Assert.NotNull(typeof(PersonalLoanSagaModule).GetConstructor([typeof(SagaModuleContext)]));

        var module = new PersonalLoanSagaModule(Context);
        Assert.Equal(module.SagaType, module.StateMachine.SagaType);
        Assert.Equal(module.SagaType, module.ResultEventBridge.SagaType);
        Assert.Equal(module.SagaType, module.CommandRouter.SagaType);

        Assert.Throws<ArgumentNullException>(() => new PersonalLoanSagaModule(null!));
    }

    [Fact]
    public void The_stub_machine_bridge_and_router_are_inert_and_fail_closed()
    {
        // No transition exists (an empty table is the fail-closed spec, ADR-IC-003 §P2), no outcome maps
        // to a result event, and no command routes anywhere — nothing this module contributes can ever
        // move money or advance state. The first REAL loan saga replaces these stubs, module-locally.
        var module = new PersonalLoanSagaModule(Context);

        Assert.False(module.StateMachine.TryAdvance(
            module.StateMachine.InitialState, "AnythingAtAll", out _));
        Assert.False(module.StateMachine.IsTerminal(module.StateMachine.InitialState));
        Assert.Null(module.ResultEventBridge.ForOutcome("AnyCommand", CommandDeliveryKind.Applied));
        Assert.Null(module.CommandRouter.Resolve("AnyCommand"));
        Assert.Null(module.CommandRouter.Resolve("AnyCommand", module.SagaType));
    }

    [Fact]
    public void ConfigureServices_registers_nothing()
    {
        // No typed sink, no store, no status map — the settlement machinery that consumes this family's
        // topics is the substrate's own module, registered by the host.
        var services = new ServiceCollection();
        new PersonalLoanSagaModule(Context).ConfigureServices(services, Context);

        Assert.Empty(services);
    }
}
