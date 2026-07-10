using Babelstone.Orchestrator.Saga;
using Babelstone.Orchestrator.Saga.Settlement;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// The engine-CA settlement ERROR MODEL (ADR-PC-043 §5 slot 5). In plain English: when the saga tries to move
/// cash against an engine-owned current account and the receiver says "no", there are two very different "no"s.
/// One means "the account genuinely declined this debit" (closed / frozen / not enough money) — a real failure,
/// so the saga must park LOUDLY for an operator, never quietly report success with no money moved. The other
/// means "you need to re-prove strong customer authentication" (SCA) — NOT a failure: the money simply has not
/// moved yet, so the saga must RETRY the SAME cash leg under the SAME identity once a fresh proof arrives,
/// never dropping the payout and never starting a brand-new attempt (which could move the money twice).
/// </summary>
/// <remarks>
/// <para>
/// These pin the two ADR-PC-043 §5 fitness IDs against the PURE settlement classifier
/// (<see cref="SettlementProcess.ClassifySettlementDelivery"/>) and the pure transition table — no clock, no
/// I/O, no DB, the same "the state machine is the specification" posture as
/// <see cref="SettlementProcessSagaTests"/>. The classifier is the one place that can tell a
/// <c>422 SCA_REQUIRED</c> apart from a business decline (both are 422s — the status alone cannot), because it
/// reads the receiver's error <c>code</c>; the table then proves a decline lands in
/// HUMAN_INTERVENTION_REQUIRED with no compensation, never SETTLEMENT_COMPLETED.
/// </para>
/// <para>
/// The settlement-facing CA surface's own 4xx-on-decline shape (a <c>DECLINED</c>/closed/erased/hold-miss
/// verdict throws <c>DomainRejectedException</c> → HTTP 422, never a 200-with-<c>Declined</c> body the
/// dispatcher would mis-read as <c>Applied</c>) is pinned family-side by the CurrentAccount.Application decline
/// tests (CurrentAccountCreditAdmissionTests / CurrentAccountCaptureTests); here we pin the SAGA half — that a
/// 4xx decline drives ReserveRefused → HIR, and that the SCA re-challenge is held retriable instead.
/// </para>
/// </remarks>
public sealed class SettlementCaErrorModelTests
{
    private readonly SettlementProcess _machine = new();

    // ==== SETTLEMENT_CA_DECLINE_IS_4XX =================================================================
    // A DECLINED reserve on the debit path is shaped as a 4xx (never a 200-with-Declined) → classified a
    // Decline → Refused → ReserveRefused → a LOUD park in HIR, never a silent march to SETTLEMENT_COMPLETED.

    [Theory]
    [InlineData(422, "ACCOUNT_FROZEN")]       // a frozen destination
    [InlineData(422, "INSUFFICIENT_BALANCE")] // not enough money to reserve
    [InlineData(422, "ACCOUNT_CLOSED")]       // a closed destination
    [InlineData(409, "CONFLICT")]             // any other 4xx decline shape
    [InlineData(400, "BAD_REQUEST")]
    public void SETTLEMENT_CA_DECLINE_IS_4XX_a_4xx_decline_classifies_as_a_decline_not_a_silent_success(
        int httpStatusCode, string errorCode)
    {
        // The reserve leg is the funds-gated debit leg (ADR-PC-016 slot 5). A 4xx on it is a genuine decline.
        var disposition = SettlementProcess.ClassifySettlementDelivery(
            SettlementProcess.ReserveAccountBalance, httpStatusCode, errorCode);

        Assert.Equal(SettlementProcess.SettlementDeliveryDisposition.Decline, disposition);
    }

    [Fact]
    public void SETTLEMENT_CA_DECLINE_IS_4XX_a_declined_reserve_maps_to_ReserveRefused_and_parks_in_HIR()
    {
        // The bridge turns a Refused reserve into ReserveRefused (the §P6 "no hold to release" signal)...
        var resultEvent = SettlementResultEvents.ForOutcome(
            SettlementProcess.ReserveAccountBalance, CommandDeliveryKind.Refused);
        Assert.Equal(SettlementProcess.ReserveRefused, resultEvent);

        // ...and the table drives ReserveRefused from RESERVING → HUMAN_INTERVENTION_REQUIRED with NO
        // compensation command (nothing was held; the money did not move — ADR-IC-003 §P6).
        Assert.True(_machine.TryAdvance(
            SettlementProcess.States.Reserving, SettlementProcess.ReserveRefused, out var outcome));
        Assert.Equal(SettlementProcess.States.HumanInterventionRequired, outcome.Next);
        Assert.Empty(outcome.Commands);

        // HIR is NON-terminal (an operator resolves it out) — the park is LOUD, not a dead end...
        Assert.False(_machine.IsTerminal(SettlementProcess.States.HumanInterventionRequired));
        // ...and it is emphatically NOT the completed terminal: a declined reserve NEVER marches to
        // SETTLEMENT_COMPLETED with zero landing.
        Assert.NotEqual(SettlementProcess.States.SettlementCompleted, outcome.Next);
    }

    // ==== SETTLEMENT_CA_SCA_STALE_IS_RETRIABLE =========================================================
    // A 422 SCA_REQUIRED at dispatch is NOT a failure: it is retriable-PENDING under the SAME process_id
    // after SCA refresh — never terminal-FAILED (a drop), never re-driven as a fresh occurrence (a double).

    [Theory]
    [InlineData("SCA_REQUIRED")]
    [InlineData("sca_required")] // the code is matched case-insensitively
    public void SETTLEMENT_CA_SCA_STALE_IS_RETRIABLE_a_422_sca_required_confirm_is_retriable_pending(string code)
    {
        // The irreversible cash confirms are the SCA-gated money-mover legs (ADR-IC-006 §P2). A stale/absent
        // proof at dispatch surfaces 422 SCA_REQUIRED — retriable, not refused.
        Assert.Equal(
            SettlementProcess.SettlementDeliveryDisposition.RetriablePending,
            SettlementProcess.ClassifySettlementDelivery(SettlementProcess.ConfirmDebit, 422, code));
        Assert.Equal(
            SettlementProcess.SettlementDeliveryDisposition.RetriablePending,
            SettlementProcess.ClassifySettlementDelivery(SettlementProcess.ConfirmCredit, 422, code));
    }

    [Fact]
    public void SETTLEMENT_CA_SCA_STALE_IS_RETRIABLE_retriable_is_neither_a_terminal_failed_nor_a_fresh_occurrence()
    {
        // Retriable-PENDING is NOT a terminal Refused disposition — so the dispatcher leaves the row PENDING to
        // re-drive under the SAME process_id, never flips it FAILED (a dropped payout).
        var disposition = SettlementProcess.ClassifySettlementDelivery(
            SettlementProcess.ConfirmDebit, 422, SettlementProcess.ScaRequiredErrorCode);
        Assert.NotEqual(SettlementProcess.SettlementDeliveryDisposition.Decline, disposition);
        Assert.NotEqual(SettlementProcess.SettlementDeliveryDisposition.DefaultByStatus, disposition);

        // A retriable SCA re-challenge drives NO forward saga transition — the SCA-refresh re-drive re-emits the
        // SAME confirm command under the SAME process_id, so there is no "SCA-refused" result event that would
        // park the saga or spawn a fresh occurrence. The confirm states have no such edge.
        Assert.False(_machine.TryAdvance(
            SettlementProcess.States.ConfirmingDebit, SettlementProcess.ScaRequiredErrorCode, out _));
        Assert.False(_machine.TryAdvance(
            SettlementProcess.States.ConfirmingCredit, SettlementProcess.ScaRequiredErrorCode, out _));
    }

    [Fact]
    public void SCA_REQUIRED_on_a_NON_confirm_leg_is_a_plain_decline_never_a_silent_retry()
    {
        // The SCA carve-out is scoped to the irreversible confirms — the money-mover legs the receiver SCA-gates.
        // A 422 SCA_REQUIRED on a reserve or a clearance query is treated as a plain Decline (→ HIR), never
        // silently retried, so a mis-attributed SCA code on a non-money-mover cannot spin forever.
        Assert.Equal(
            SettlementProcess.SettlementDeliveryDisposition.Decline,
            SettlementProcess.ClassifySettlementDelivery(
                SettlementProcess.ReserveAccountBalance, 422, SettlementProcess.ScaRequiredErrorCode));
        Assert.Equal(
            SettlementProcess.SettlementDeliveryDisposition.Decline,
            SettlementProcess.ClassifySettlementDelivery(
                SettlementProcess.QueryCoreDebitStatus, 422, SettlementProcess.ScaRequiredErrorCode));
    }

    [Fact]
    public void A_2xx_or_5xx_falls_through_to_the_default_status_based_classification()
    {
        // The refinement only speaks to 4xx refusals: a 2xx (applied / idempotent replay) and a 5xx (transient)
        // both defer to the substrate's default status-based classification, unchanged.
        Assert.Equal(
            SettlementProcess.SettlementDeliveryDisposition.DefaultByStatus,
            SettlementProcess.ClassifySettlementDelivery(SettlementProcess.ConfirmDebit, 200, errorCode: null));
        Assert.Equal(
            SettlementProcess.SettlementDeliveryDisposition.DefaultByStatus,
            SettlementProcess.ClassifySettlementDelivery(SettlementProcess.ConfirmCredit, 503, errorCode: null));
        // A 202 (the INDETERMINATE clearance signal) is NOT a 4xx — it defers to the default too.
        Assert.Equal(
            SettlementProcess.SettlementDeliveryDisposition.DefaultByStatus,
            SettlementProcess.ClassifySettlementDelivery(SettlementProcess.ConfirmDebit, 202, errorCode: null));
    }
}
