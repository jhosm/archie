using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Saga;
using Babelstone.Orchestrator.Saga.Settlement;
using Npgsql;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// End-to-end proof that a real Movement-bearing event LIVE-AUTO-STARTS the substrate-owned settlement saga
/// through the consume→advance path (bd babelstone-t7o3.20 AC#3; ADR-PC-032 §A7/§A8; ADR-IC-018 §P5/§D5
/// family-agnostic-saga amendment). In plain English: this drives a `LoanDisbursed`-shaped inbox event —
/// whose `ce_type` record name is `LoanDisbursed`, carrying the producer's `ce_movementorigin=Originated` /
/// `ce_movementdirection` extension headers — straight through <see cref="SagaAdvanceHandler.AdvanceAsync"/>
/// (the real consume-loop dispatch), and asserts a <see cref="SettlementProcess"/> instance IS BORN and the
/// correct debit/credit branch is selected. It closes the gap the contract test
/// (<see cref="MovementHeaderAutoStartContractTests"/>) deliberately could not reach: that test exercises the
/// rule's predicate + substitutor in isolation; THIS exercises the record-name-agnostic auto-start DISPATCH
/// (the registry lookup keyed by `message.EventType`), where a `LoanDisbursed` record name does NOT equal the
/// saga's `MovementOriginated` start marker — the very mismatch the <see cref="AutoStartMatch.ByHeaderPredicate"/>
/// model fixes.
/// </summary>
/// <remarks>
/// The substrate never rewrites `ce_type` (it would break the engine inbox's `ce_type`↔`schema_id` decode);
/// instead the record-name-agnostic rule matches on the `movementorigin` header and the substrate drives the
/// advance with the saga's GENERIC `MovementOriginated` marker, which the table /
/// <see cref="SettlementProcess.SubstituteAsync"/> resolve to the direction branch from `ce_movementdirection`.
/// A bare <see cref="RecordingCommandSink"/> absorbs the saga's first emitted command (no typed route needed).
/// </remarks>
[Trait("Category", "Integration")]
[Collection(nameof(OrchestratorPostgresCollection))]
public sealed class MovementSagaAutoStartIntegrationTests(OrchestratorPostgresFixture fixture)
{
    private readonly SagaStateStore _stateStore = new();
    private readonly SagaTransitionLog _transitionLog = new();

    // The exact ce_type record name a REAL personal-loan disbursement carries — NOT "MovementOriginated".
    // This is the crux: the settlement saga must auto-start even though this record name does not equal its
    // start marker. (The consume loop derives message.EventType from RecordName(ce_type), so this stands in
    // for the projected LoanDisbursed event type.)
    private const string LoanDisbursedEventType = "LoanDisbursed";

    private static IReadOnlyDictionary<string, string> MovementHeaders(string direction) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SettlementSagaModule.OriginHeader] = "Originated",       // ce_movementorigin (producer value)
            [SettlementProcess.DirectionHeader] = direction,          // ce_movementdirection (Debit | Credit)
        };

    [Fact]
    public async Task A_credit_movement_bearing_event_auto_starts_the_settlement_saga_into_the_credit_branch()
    {
        // A disbursement is an Originated CREDIT (the lump sum enters the borrower's account). The event's
        // ce_type record name is LoanDisbursed, NOT MovementOriginated — the record-name-agnostic rule must
        // still auto-start the settlement saga and resolve the CREDIT branch from ce_movementdirection.
        var sink = new RecordingCommandSink();
        var handler = NewSettlementAutoStartHandler(sink);
        var loanId = Guid.NewGuid();

        var outcome = await RunAsync(handler, new SagaInboxEvent(
            MessageId: Guid.NewGuid(), ProcessId: loanId, EventType: LoanDisbursedEventType,
            SourceTopic: "personal_loan", CorrelationId: null,
            ExtensionHeaders: MovementHeaders("Credit")));

        // The saga was BORN off the header alone (record-name-agnostic) and took the credit branch's first
        // edge: SETTLEMENT_STARTED -> CONFIRMING_CREDIT, emitting ConfirmCredit (no reserve leg for a credit).
        Assert.Equal(AdvanceOutcome.Advanced, outcome);
        Assert.Equal(SettlementProcess.States.ConfirmingCredit, await StateOrNullAsync(loanId));
        Assert.Equal(SettlementProcess.ConfirmCredit, Assert.Single(sink.Emitted).CommandType);
    }

    [Fact]
    public async Task A_debit_movement_bearing_event_auto_starts_the_settlement_saga_into_the_debit_branch()
    {
        // The debit branch (e.g. a deposit constitution / installment collection on another family): an
        // Originated DEBIT auto-starts the saga into the funds-gated reserve leg, again off a record name
        // that is not MovementOriginated.
        var sink = new RecordingCommandSink();
        var handler = NewSettlementAutoStartHandler(sink);
        var processId = Guid.NewGuid();

        var outcome = await RunAsync(handler, new SagaInboxEvent(
            MessageId: Guid.NewGuid(), ProcessId: processId, EventType: "DepositMaturedOnSomeFamily",
            SourceTopic: "term_deposit", CorrelationId: null,
            ExtensionHeaders: MovementHeaders("Debit")));

        Assert.Equal(AdvanceOutcome.Advanced, outcome);
        Assert.Equal(SettlementProcess.States.Reserving, await StateOrNullAsync(processId));
        Assert.Equal(SettlementProcess.ReserveAccountBalance, Assert.Single(sink.Emitted).CommandType);
    }

    [Fact]
    public async Task An_observed_movement_bearing_event_does_NOT_auto_start_the_settlement_saga()
    {
        // An Observed movement arrived already cleared (no cash leg to drive, ADR-PC-032 slot 2): the
        // predicate fails, so the same record name auto-starts NOTHING — fail-closed, no saga row.
        var handler = NewSettlementAutoStartHandler(new RecordingCommandSink());
        var processId = Guid.NewGuid();

        var observed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SettlementSagaModule.OriginHeader] = "Observed",
            [SettlementProcess.DirectionHeader] = "Credit",
        };
        var outcome = await RunAsync(handler, new SagaInboxEvent(
            MessageId: Guid.NewGuid(), ProcessId: processId, EventType: LoanDisbursedEventType,
            SourceTopic: "personal_loan", CorrelationId: null, ExtensionHeaders: observed));

        Assert.Equal(AdvanceOutcome.UnknownSaga, outcome);
        Assert.Null(await StateOrNullAsync(processId));
    }

    [Fact]
    public async Task A_non_movement_event_does_NOT_auto_start_the_settlement_saga()
    {
        // An event carrying no movementorigin header (a non-money-moving fact) starts nothing — the
        // record-name-agnostic rule never fires on unrelated traffic.
        var handler = NewSettlementAutoStartHandler(new RecordingCommandSink());
        var processId = Guid.NewGuid();

        var outcome = await RunAsync(handler, new SagaInboxEvent(
            MessageId: Guid.NewGuid(), ProcessId: processId, EventType: "SomeUnrelatedEvent",
            SourceTopic: "term_deposit", CorrelationId: null, ExtensionHeaders: null));

        Assert.Equal(AdvanceOutcome.UnknownSaga, outcome);
        Assert.Null(await StateOrNullAsync(processId));
    }

    // ---- helpers (modelled on RenewalAutoStartEmptySubjectGuardIntegrationTests) --------------------

    private SagaAdvanceHandler NewSettlementAutoStartHandler(RecordingCommandSink sink)
    {
        // The REAL SettlementSagaModule (so the substrate's auto-start registry is built from its declared
        // record-name-agnostic AutoStartRule + header predicate) and its machine. A bare recording sink
        // absorbs the saga's first emitted command without a typed route.
        var context = new SagaModuleContext(
            RuntimeConnectionString: fixture.ConnectionString,
            EngineBaseUrl: "http://engine.invalid",
            SettlementBaseUrl: "http://settlement.invalid");
        var module = new SettlementSagaModule(context, consumeTopics: ["personal_loan"]);
        return new SagaAdvanceHandler(
            new ISagaStateMachine[] { module.StateMachine },
            _stateStore, _transitionLog, sink,
            new ISagaModule[] { module });
    }

    private async Task<AdvanceOutcome> RunAsync(SagaAdvanceHandler handler, SagaInboxEvent message)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var outcome = await handler.AdvanceAsync(connection, tx, message);
        await tx.CommitAsync();
        return outcome;
    }

    private async Task<string?> StateOrNullAsync(Guid processId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var saga = await _stateStore.LoadAsync(connection, tx, processId);
        await tx.RollbackAsync();
        return saga?.State;
    }
}
