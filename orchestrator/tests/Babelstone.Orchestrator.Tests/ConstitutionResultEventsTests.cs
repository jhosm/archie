using Babelstone.Orchestrator.Saga;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// Pure tests of the <see cref="ConstitutionResultEvents"/> command-outcome → result-event bridge (bd
/// babelstone-t7o3.8). The bridge is a pure function of <c>(command_type, delivery-kind)</c>; these
/// assert its full mapping — every forward leg, every compensation leg, the two REVIEW-FLAGGED
/// carve-outs (ValidateProductLimits auto-pass, ReserveAccountBalance refusal), and the null cases that
/// drive no advance. No clock, no I/O, no DB: the mapping IS the specification and these prove it.
/// </summary>
public sealed class ConstitutionResultEventsTests
{
    [Theory]
    // Forward / happy-path ACL legs (2xx Applied → the leg's success result).
    [InlineData(ConstitutionProcess.ReserveAccountBalance, CommandDeliveryKind.Applied, ConstitutionProcess.BalanceReserved)]
    [InlineData(ConstitutionProcess.ConfirmDebit, CommandDeliveryKind.Applied, ConstitutionProcess.DebitConfirmed)]
    // NB: (ActivateDeposit, Applied) is DELIBERATELY absent — it drives no advance (ADR-PC-029 slot 2:
    // the engine 2xx is not the advance signal; ProcessConstituted arrives off the bus). Asserted in
    // Drives_no_advance_for_unmapped_outcomes below.
    // Post-debit compensation trigger (the headline): activation refused after the debit confirmed.
    [InlineData(ConstitutionProcess.ActivateDeposit, CommandDeliveryKind.Refused, ConstitutionProcess.ActivationFailed)]
    // Compensation legs accepted (2xx Applied → the reversal completed).
    [InlineData(ConstitutionProcess.ReleaseBalanceReservation, CommandDeliveryKind.Applied, ConstitutionProcess.ReservationReleased)]
    [InlineData(ConstitutionProcess.ReverseCoreDebit, CommandDeliveryKind.Applied, ConstitutionProcess.DebitReversed)]
    // A compensation that itself FAILED (4xx Refused) escalates.
    [InlineData(ConstitutionProcess.ReleaseBalanceReservation, CommandDeliveryKind.Refused, ConstitutionProcess.CompensationFailed)]
    [InlineData(ConstitutionProcess.ReverseCoreDebit, CommandDeliveryKind.Refused, ConstitutionProcess.CompensationFailed)]
    // [REVIEW-FLAG A] ValidateProductLimits auto-pass (synthetic Applied for the no-route carve-out).
    [InlineData(ConstitutionProcess.ValidateProductLimits, CommandDeliveryKind.Applied, ConstitutionProcess.LimitsValidated)]
    // [REVIEW-FLAG B] ReserveAccountBalance refusal (422 InsufficientBalance) → fail-closed precondition.
    [InlineData(ConstitutionProcess.ReserveAccountBalance, CommandDeliveryKind.Refused, ConstitutionProcess.PreconditionRefused)]
    // Scenario C (bd babelstone-t7o3.10): the ConfirmDebit returned INDETERMINATE (HTTP 202 — the ACL
    // accepted the debit but cannot yet confirm whether the Core executed it) → enter AwaitCoreClearance.
    [InlineData(ConstitutionProcess.ConfirmDebit, CommandDeliveryKind.Indeterminate, ConstitutionProcess.CoreDebitIndeterminate)]
    // Clearance resolved EXECUTED (2xx) → the late DebitConfirmed that resumes the happy path.
    [InlineData(ConstitutionProcess.QueryCoreDebitStatus, CommandDeliveryKind.Applied, ConstitutionProcess.DebitConfirmed)]
    // [REVIEW-FLAG C] Clearance resolved NOT-EXECUTED (4xx) → DebitNotExecuted, which drives the
    // RETRY_PERMITTED reissue (conforming to ADR-IC-012 §D5/§P5). The 4xx=not-executed encoding is still
    // a v1 stub convention; the real ACL/DEF-1 emits typed clearance events.
    [InlineData(ConstitutionProcess.QueryCoreDebitStatus, CommandDeliveryKind.Refused, ConstitutionProcess.DebitNotExecuted)]
    public void Maps_each_command_outcome_to_its_result_event(string commandType, CommandDeliveryKind kind, string expected)
    {
        Assert.Equal(expected, ConstitutionResultEvents.ForOutcome(commandType, kind));
    }

    [Theory]
    // The engine-leg activation 2xx drives NO advance (ADR-PC-029 slot 2): the HTTP 2xx confirms the
    // command was applied but is NOT the saga's signal to advance. The saga reaches COMPLETED only when
    // the engine's real ProcessConstituted (DepositConstituted) event arrives off deposits.process.events
    // via the consume loop — the bridge must not be a second producer for that transition.
    [InlineData(ConstitutionProcess.ActivateDeposit, CommandDeliveryKind.Applied)]
    // ConfirmDebit refusal is NOT a mapped pair: a refused irreversible debit is the engine/ACL's
    // own concern, not a saga result event the bridge synthesizes (no transition is derived here).
    [InlineData(ConstitutionProcess.ConfirmDebit, CommandDeliveryKind.Refused)]
    // An Indeterminate kind is ONLY meaningful for ConfirmDebit (the ACL's INDETERMINATE settlement
    // signal); for any OTHER command it drives no advance — the kind is carved out, not a wildcard.
    [InlineData(ConstitutionProcess.ReserveAccountBalance, CommandDeliveryKind.Indeterminate)]
    [InlineData(ConstitutionProcess.QueryCoreDebitStatus, CommandDeliveryKind.Indeterminate)]
    // ConfirmDebit APPLIED still maps (to DebitConfirmed, asserted above); but ConfirmDebit
    // Indeterminate is the new Scenario-C branch. The clearance query has no Indeterminate mapping.
    // ValidateProductLimits never reaches a real Refused through the bridge (it has no route); a
    // Refused for it drives no advance (LimitsRejected is H.2's real verdict, not synthesized here).
    [InlineData(ConstitutionProcess.ValidateProductLimits, CommandDeliveryKind.Refused)]
    // An unknown / unmapped command type drives no advance under either kind.
    [InlineData("SomeUnknownCommand", CommandDeliveryKind.Applied)]
    [InlineData("SomeUnknownCommand", CommandDeliveryKind.Refused)]
    public void Drives_no_advance_for_unmapped_outcomes(string commandType, CommandDeliveryKind kind)
    {
        Assert.Null(ConstitutionResultEvents.ForOutcome(commandType, kind));
    }

    [Fact]
    public void The_deterministic_result_id_is_stable_and_distinct_per_command_and_event()
    {
        var commandId = Guid.NewGuid();

        // Same (command id, result type) → same id (so a re-POST re-derives it; the inbox dedups).
        Assert.Equal(
            SagaSettlementResultEmit.MessageId(commandId, ConstitutionProcess.BalanceReserved),
            SagaSettlementResultEmit.MessageId(commandId, ConstitutionProcess.BalanceReserved));

        // A different result type off the same command → a different id.
        Assert.NotEqual(
            SagaSettlementResultEmit.MessageId(commandId, ConstitutionProcess.BalanceReserved),
            SagaSettlementResultEmit.MessageId(commandId, ConstitutionProcess.DebitConfirmed));

        // A different command → a different id for the same result type.
        Assert.NotEqual(
            SagaSettlementResultEmit.MessageId(commandId, ConstitutionProcess.BalanceReserved),
            SagaSettlementResultEmit.MessageId(Guid.NewGuid(), ConstitutionProcess.BalanceReserved));
    }

    [Fact]
    public void Scenario_C_synthesized_ids_are_stable_per_command_so_a_re_drive_dedups()
    {
        // Scenario C re-drive idempotency (bd babelstone-t7o3.10): a crash between the ConfirmDebit's 202
        // and the saga_outbox flip re-POSTs the SAME PENDING ConfirmDebit row, so the dispatcher
        // re-synthesizes CoreDebitIndeterminate off the SAME command message_id. The id must be
        // byte-stable so the second AdvanceAsync is an inbox dedup no-op (the saga lands in
        // AWAIT_CORE_CLEARANCE exactly once, no double-advance, no second QueryCoreDebitStatus row).
        var confirmDebitId = Guid.NewGuid();
        Assert.Equal(
            SagaSettlementResultEmit.MessageId(confirmDebitId, ConstitutionProcess.CoreDebitIndeterminate),
            SagaSettlementResultEmit.MessageId(confirmDebitId, ConstitutionProcess.CoreDebitIndeterminate));

        // The TIMELY DebitConfirmed (off the original ConfirmDebit) and the LATE clearance-driven
        // DebitConfirmed (off the QueryCoreDebitStatus command) carry DISTINCT command message_ids, so
        // their synthesized ids cannot collide in the inbox dedup — the late confirm is never absorbed as
        // a replay of the timely one.
        var clearanceQueryId = Guid.NewGuid();
        Assert.NotEqual(
            SagaSettlementResultEmit.MessageId(confirmDebitId, ConstitutionProcess.DebitConfirmed),
            SagaSettlementResultEmit.MessageId(clearanceQueryId, ConstitutionProcess.DebitConfirmed));

        // The clearance query's two verdicts (DebitConfirmed vs DebitNotExecuted) off the SAME query
        // command get DISTINCT ids, and the not-executed id is stable for its own re-drive dedup.
        Assert.NotEqual(
            SagaSettlementResultEmit.MessageId(clearanceQueryId, ConstitutionProcess.DebitConfirmed),
            SagaSettlementResultEmit.MessageId(clearanceQueryId, ConstitutionProcess.DebitNotExecuted));
        Assert.Equal(
            SagaSettlementResultEmit.MessageId(clearanceQueryId, ConstitutionProcess.DebitNotExecuted),
            SagaSettlementResultEmit.MessageId(clearanceQueryId, ConstitutionProcess.DebitNotExecuted));
    }

    [Fact]
    public void The_settlement_result_id_never_collides_with_an_approval_fork_self_emit_id()
    {
        // The two id spaces are disjoint by construction (distinct namespace GUIDs), so even if a
        // command id and a process id happened to share bytes the synthesized ids could not collide.
        var shared = Guid.NewGuid();
        Assert.NotEqual(
            SagaSettlementResultEmit.MessageId(shared, ConstitutionProcess.BalanceReserved),
            SagaSelfEmit.MessageId(shared, ConstitutionProcess.BalanceReserved));
    }
}
