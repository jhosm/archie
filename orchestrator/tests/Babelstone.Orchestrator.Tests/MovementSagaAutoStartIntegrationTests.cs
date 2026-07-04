using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Saga;
using Babelstone.Orchestrator.Saga.Settlement;
using Npgsql;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// End-to-end proof that a real Movement-bearing event LIVE-AUTO-STARTS the substrate-owned settlement saga
/// through the consume→advance path (bd babelstone-t7o3.20 AC#3; ADR-PC-032 §A7/§A8 + §A9/§A10 Revised
/// 2026-07-04; ADR-IC-018 §P5/§D5 family-agnostic-saga amendment). In plain English: this drives a
/// `LoanDisbursed`-shaped inbox event — whose `ce_type` record name is `LoanDisbursed`, carrying the
/// producer's `ce_movementorigin=Originated` / `ce_movementdirections` extension headers — straight through
/// <see cref="SagaAdvanceHandler.AdvanceAsync"/> (the real consume-loop dispatch), and asserts a
/// <see cref="SettlementProcess"/> instance IS BORN at its PER-OCCURRENCE derived id (keyed back to the
/// account/instrument by <c>saga_state.subject_id</c>) and the correct debit/credit branch is selected. It
/// closes the gap the contract test (<see cref="MovementHeaderAutoStartContractTests"/>) deliberately could
/// not reach: that test exercises the rule's predicate + substitutor in isolation; THIS exercises the
/// record-name-agnostic auto-start DISPATCH (the registry lookup keyed by `message.EventType`), where a
/// `LoanDisbursed` record name does NOT equal the saga's `MovementOriginated` start marker — the very
/// mismatch the <see cref="AutoStartMatch.ByHeaderPredicate"/> model fixes.
/// </summary>
/// <remarks>
/// The substrate never rewrites `ce_type` (it would break the engine inbox's `ce_type`↔`schema_id` decode);
/// instead the record-name-agnostic rule matches on the `movementorigin` header and the substrate drives the
/// advance with the saga's GENERIC `MovementOriginated` marker, which the table /
/// <see cref="SettlementProcess.SubstituteAsync"/> resolve to the direction branch from the leg's single-entry
/// `ce_movementdirections` list. Since the per-occurrence-identity revision (bd babelstone-3o6m / Q-BH), each
/// instance's `process_id` derives from (ce_subject, ce_id, movement index) — so a SECOND occurrence on the
/// SAME subject gets its OWN saga even after the first completed, the recurring-schedule case LCD-2 gates.
/// A bare <see cref="RecordingCommandSink"/> absorbs the saga's emitted commands (no typed route needed).
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
            [SettlementSagaModule.OriginHeader] = "Originated",          // ce_movementorigin (producer value)
            [SettlementMovementFanout.DirectionsHeader] = direction,     // ce_movementdirections (one entry per Movement)
        };

    [Fact]
    public async Task A_credit_movement_bearing_event_auto_starts_the_settlement_saga_into_the_credit_branch()
    {
        // A disbursement is an Originated CREDIT (the lump sum enters the borrower's account). The event's
        // ce_type record name is LoanDisbursed, NOT MovementOriginated — the record-name-agnostic rule must
        // still auto-start the settlement saga and resolve the CREDIT branch from ce_movementdirections.
        var sink = new RecordingCommandSink();
        var handler = NewSettlementAutoStartHandler(sink);
        var loanId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        var outcome = await RunAsync(handler, new SagaInboxEvent(
            MessageId: eventId, ProcessId: loanId, EventType: LoanDisbursedEventType,
            SourceTopic: "personal_loan", CorrelationId: null,
            ExtensionHeaders: MovementHeaders("Credit")));

        // The saga was BORN off the header alone (record-name-agnostic) at its PER-OCCURRENCE derived id —
        // NOT the bare ce_subject (ADR-PC-032 §A9/§A10 Revised 2026-07-04) — carrying subject_id = the loan,
        // and took the credit branch's first edge: SETTLEMENT_STARTED -> CONFIRMING_CREDIT, emitting
        // ConfirmCredit (no reserve leg for a credit).
        Assert.Equal(AdvanceOutcome.Advanced, outcome);
        var occurrenceId = SettlementMovementFanout.OccurrenceProcessId(loanId, eventId, 0);
        Assert.Null(await StateOrNullAsync(loanId));
        Assert.Equal(SettlementProcess.States.ConfirmingCredit, await StateOrNullAsync(occurrenceId));
        Assert.Equal(
            SettlementProcess.States.ConfirmingCredit,
            Assert.Single(await StatesBySubjectAsync(loanId)));
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
        var eventId = Guid.NewGuid();

        var outcome = await RunAsync(handler, new SagaInboxEvent(
            MessageId: eventId, ProcessId: processId, EventType: "DepositMaturedOnSomeFamily",
            SourceTopic: "term_deposit", CorrelationId: null,
            ExtensionHeaders: MovementHeaders("Debit")));

        Assert.Equal(AdvanceOutcome.Advanced, outcome);
        Assert.Equal(
            SettlementProcess.States.Reserving,
            await StateOrNullAsync(SettlementMovementFanout.OccurrenceProcessId(processId, eventId, 0)));
        Assert.Equal(
            SettlementProcess.States.Reserving,
            Assert.Single(await StatesBySubjectAsync(processId)));
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
            [SettlementMovementFanout.DirectionsHeader] = "Credit",
        };
        var outcome = await RunAsync(handler, new SagaInboxEvent(
            MessageId: Guid.NewGuid(), ProcessId: processId, EventType: LoanDisbursedEventType,
            SourceTopic: "personal_loan", CorrelationId: null, ExtensionHeaders: observed));

        Assert.Equal(AdvanceOutcome.UnknownSaga, outcome);
        Assert.Empty(await StatesBySubjectAsync(processId));
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
        Assert.Empty(await StatesBySubjectAsync(processId));
    }

    [Fact]
    public async Task A_multi_direction_event_fans_out_into_one_settlement_instance_per_movement()
    {
        // A renewal carries a rollover-DEBIT + an interest-CREDIT on ONE event (ADR-PC-032 §A9/§A10, option
        // b). The producer emits the ordered movementdirections list; the substrate fans the ONE event into
        // TWO settlement instances — each at its own per-occurrence derived id, both carrying
        // subject_id = the renewal's subject — born atomically.
        var sink = new RecordingCommandSink();
        var handler = NewSettlementAutoStartHandler(sink);
        var renewalId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SettlementSagaModule.OriginHeader] = "Originated",
            [SettlementMovementFanout.DirectionsHeader] = "Debit,Credit", // the ordered list (one entry per Movement)
        };
        var outcome = await RunAsync(handler, new SagaInboxEvent(
            MessageId: eventId, ProcessId: renewalId, EventType: "DepositRenewed",
            SourceTopic: "term_deposit", CorrelationId: null, ExtensionHeaders: headers));

        Assert.Equal(AdvanceOutcome.Advanced, outcome);

        // The FIRST Movement's instance (index 0) took the DEBIT branch — the funds-gated reserve leg.
        var debitSubject = SettlementMovementFanout.OccurrenceProcessId(renewalId, eventId, 0);
        Assert.Equal(SettlementProcess.States.Reserving, await StateOrNullAsync(debitSubject));

        // The SECOND Movement's instance (index 1) took the CREDIT branch — the confirmation-gated confirm
        // leg. It is a DISTINCT saga row, born in the SAME transaction.
        var creditSubject = SettlementMovementFanout.OccurrenceProcessId(renewalId, eventId, 1);
        Assert.NotEqual(debitSubject, creditSubject);
        Assert.Equal(SettlementProcess.States.ConfirmingCredit, await StateOrNullAsync(creditSubject));

        // Both rows carry the SUBJECT linkage the LCD-2 probe keys on (saga_state.subject_id).
        Assert.Equal(2, (await StatesBySubjectAsync(renewalId)).Count);

        // Both legs emitted their first command — the debit's ReserveAccountBalance and the credit's
        // ConfirmCredit — so no Movement was silently lost.
        Assert.Contains(sink.Emitted, e => e.CommandType == SettlementProcess.ReserveAccountBalance);
        Assert.Contains(sink.Emitted, e => e.CommandType == SettlementProcess.ConfirmCredit);
    }

    [Fact]
    public async Task A_later_occurrence_on_the_same_subject_starts_its_own_saga_even_after_the_first_completed()
    {
        // THE recurring-schedule case (bd babelstone-3o6m / Q-BH; the LCD-2 write-side gap): installment 1's
        // settlement saga runs to its terminal SETTLEMENT_COMPLETED — and installment 2's Movement-bearing
        // event (same loan ce_subject, its OWN ce_id) must still get a FRESH settlement instance, not no-op
        // at the completed saga. Before per-occurrence identity, this second event LOADED occurrence 1's
        // terminal row (process_id = ce_subject) and returned Terminal — the exact defect under test.
        var sink = new RecordingCommandSink();
        var handler = NewSettlementAutoStartHandler(sink);
        var loanId = Guid.NewGuid();
        var installment1EventId = Guid.NewGuid();
        var installment2EventId = Guid.NewGuid();

        // Installment 1 collects (an Originated DEBIT on the borrower's account) and its saga is driven to
        // the debit path's happy terminal.
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handler, new SagaInboxEvent(
            MessageId: installment1EventId, ProcessId: loanId, EventType: "LoanInstallmentPaid",
            SourceTopic: "personal_loan", CorrelationId: null,
            ExtensionHeaders: MovementHeaders("Debit"))));

        var occurrence1 = SettlementMovementFanout.OccurrenceProcessId(loanId, installment1EventId, 0);
        await DriveToAsync(occurrence1, SettlementProcess.States.SettlementCompleted);

        // Installment 2 collects: a NEW occurrence id derives from the new event id, so a NEW saga is born
        // and takes the debit branch — the completed occurrence-1 saga is untouched.
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handler, new SagaInboxEvent(
            MessageId: installment2EventId, ProcessId: loanId, EventType: "LoanInstallmentPaid",
            SourceTopic: "personal_loan", CorrelationId: null,
            ExtensionHeaders: MovementHeaders("Debit"))));

        var occurrence2 = SettlementMovementFanout.OccurrenceProcessId(loanId, installment2EventId, 0);
        Assert.NotEqual(occurrence1, occurrence2);
        Assert.Equal(SettlementProcess.States.SettlementCompleted, await StateOrNullAsync(occurrence1));
        Assert.Equal(SettlementProcess.States.Reserving, await StateOrNullAsync(occurrence2));

        // Both occurrences hang off the SAME subject — the linkage the LCD-2 probe scans for a park.
        Assert.Equal(2, (await StatesBySubjectAsync(loanId)).Count);

        // And a REDELIVERY of installment 2's event re-derives the SAME ids and dedups — effectively-once
        // per occurrence, exactly as before.
        Assert.Equal(AdvanceOutcome.Duplicate, await RunAsync(handler, new SagaInboxEvent(
            MessageId: installment2EventId, ProcessId: loanId, EventType: "LoanInstallmentPaid",
            SourceTopic: "personal_loan", CorrelationId: null,
            ExtensionHeaders: MovementHeaders("Debit"))));
        Assert.Equal(2, (await StatesBySubjectAsync(loanId)).Count);
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

    /// <summary>Every settlement occurrence's state for the given SUBJECT (the saga_state.subject_id
    /// linkage, migration 0009) — the same shape the LCD-2 probe's parked-EXISTS reads.</summary>
    private async Task<IReadOnlyList<string>> StatesBySubjectAsync(Guid subjectId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT state FROM saga_state WHERE subject_id = @s ORDER BY created_at, process_id;", connection);
        command.Parameters.AddWithValue("s", subjectId);
        var states = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            states.Add(reader.GetString(0));
        }

        return states;
    }

    /// <summary>Force a saga row to a state (the operator/dispatcher edge in miniature) so the next
    /// occurrence's arrival is exercised against a genuinely TERMINAL prior occurrence.</summary>
    private async Task DriveToAsync(Guid processId, string state)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE saga_state SET state = @state, version = version + 1 WHERE process_id = @p;", connection);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("p", processId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}
