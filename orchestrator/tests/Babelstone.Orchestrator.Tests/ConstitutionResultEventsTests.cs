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
    // Forward / happy-path legs (2xx Applied → the leg's success result).
    [InlineData(ConstitutionProcess.ReserveAccountBalance, CommandDeliveryKind.Applied, ConstitutionProcess.BalanceReserved)]
    [InlineData(ConstitutionProcess.ConfirmDebit, CommandDeliveryKind.Applied, ConstitutionProcess.DebitConfirmed)]
    [InlineData(ConstitutionProcess.ActivateDeposit, CommandDeliveryKind.Applied, ConstitutionProcess.ProcessConstituted)]
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
    public void Maps_each_command_outcome_to_its_result_event(string commandType, CommandDeliveryKind kind, string expected)
    {
        Assert.Equal(expected, ConstitutionResultEvents.ForOutcome(commandType, kind));
    }

    [Theory]
    // ConfirmDebit refusal is NOT a mapped pair: a refused irreversible debit is the engine/ACL's
    // own concern, not a saga result event the bridge synthesizes (no transition is derived here).
    [InlineData(ConstitutionProcess.ConfirmDebit, CommandDeliveryKind.Refused)]
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
