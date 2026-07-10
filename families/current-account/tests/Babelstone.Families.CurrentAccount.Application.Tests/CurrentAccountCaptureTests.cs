using Babelstone.Engine;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.CurrentAccount.Application.Tests;

/// <summary>
/// The settlement CAPTURE commitments (ADR-PC-043 / ADR-PC-037): a capture turns an authorize reservation into a real debit — releasing the placed hold and landing a
/// Debit Movement — but only against the reservation the authorize placed, and exactly once. In plain English:
/// these pin that a capture must name the SAME hold the authorize placed (the hold-match rule, ADR-PC-043),
/// that it produces the spine HoldCaptured + the family AccountDebited as ONE atomic batch carrying that hold
/// id, and that a redelivered / reissued capture lands EXACTLY ONE Debit Movement.
/// </summary>
/// <remarks>
/// Docker-free (the pure-decider lane): the capture DECISION (<see cref="CurrentAccountCaptureDecider"/>) is a
/// pure function of (account_ref, active holds, command), so the hold match, the reconciliation
/// classification, and the event batch are exercised deterministically with no event store. The impure shell's
/// drain + append + command_dedup is the Testcontainers integration tier; the exactly-once axis it rests on —
/// the intent-derived append command_id — is pinned by <see cref="SettlementIntentKeyTests"/>, so a reissue
/// with a byte-identical body collapses to ONE append.
/// </remarks>
public sealed class CurrentAccountCaptureTests
{
    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly string AccountRef = AccountId.ToString();
    private static readonly DateOnly ValueDate = new(2026, 3, 5);
    private static readonly Guid CommandId = Guid.NewGuid();
    private const string HoldId = "hold-authorize-abc";

    private static CaptureAccountCommand Command(string targetHoldId = HoldId, long amountCents = 50_000) =>
        new(AccountId, targetHoldId, amountCents, ValueDate, "INTENT-abc|installment-1", "svc:settlement-dispatch", CommandId);

    private static Hold ActiveAuthorizationHold(string holdId = HoldId, long amountCents = 50_000) => new(
        HoldId: holdId,
        AccountRef: AccountRef,
        Amount: new Money(amountCents),
        ValueDate: ValueDate,
        State: HoldState.Active,
        Kind: HoldKind.Authorization);

    // --- SETTLEMENT_CA_CAPTURE_HOLD_MATCH ---

    [Fact]
    public void SETTLEMENT_CA_CAPTURE_HOLD_MATCH_a_capture_matching_the_authorize_hold_yields_HoldCaptured_plus_one_Debit_Movement()
    {
        // The active-hold set holds exactly the authorize's reservation; the capture names it.
        var decision = CurrentAccountCaptureDecider.Decide(AccountRef, [ActiveAuthorizationHold()], Command());

        // ONE atomic batch: the spine HoldCaptured (the earmark release, REUSED) THEN the family AccountDebited.
        Assert.Equal(2, decision.Events.Count);

        var captured = Assert.IsType<HoldCaptured>(decision.Events[0]);
        Assert.Equal(AccountId, captured.InstanceId); // the account stream (the projector's own-stream precondition)
        Assert.Equal(HoldId, captured.HoldId);         // authorize.hold_id == capture.target_hold_id
        Assert.Equal(AccountRef, captured.AccountRef);
        Assert.Equal(50_000, captured.CapturedAmount.Cents);

        var debited = Assert.IsType<AccountDebited>(decision.Events[1]);
        Assert.Equal(AccountId, debited.AccountId);
        Assert.Equal(HoldId, debited.HoldId);          // the Debit names the SAME earmark as the capture
        Assert.Equal(50_000, debited.Amount.Cents);
        Assert.Equal("INTENT-abc|installment-1", debited.IntentReference);

        // EXACTLY ONE Debit Movement (a Debit SUBTRACTS from the accounting balance), Observed
        // (engine-internal-already-effected loop-breaker), carrying the append command id.
        var movement = Assert.Single(((IMovementBearing)debited).Movements);
        Assert.Equal(AccountRef, movement.AccountRef);
        Assert.Equal(SettlementDirection.Debit, movement.Direction);
        Assert.Equal(50_000, movement.Amount.Cents);
        Assert.Equal(MovementOperation.CollectInstallment, movement.Operation); // generic money-OUT verb (a dedicated CA verb is a later change)
        Assert.Equal(MovementOrigin.Observed, movement.Origin);
        Assert.Equal(CommandId, movement.CommandId);

        // A full capture is clean — no reconciliation signal.
        Assert.Null(decision.Reconciliation);
    }

    [Fact]
    public void SETTLEMENT_CA_CAPTURE_HOLD_MATCH_a_capture_naming_no_active_hold_is_rejected()
    {
        // The authorize placed hold-authorize-abc; a capture naming a DIFFERENT hold matches nothing in the
        // active set → rejected (a 4xx / HIR park), never a phantom debit on an unmatched hold.
        var ex = Assert.Throws<DomainRejectedException>(
            () => CurrentAccountCaptureDecider.Decide(
                AccountRef, [ActiveAuthorizationHold()], Command(targetHoldId: "hold-someone-else")));
        Assert.Contains("hold-someone-else", ex.Message);
    }

    [Fact]
    public void SETTLEMENT_CA_CAPTURE_HOLD_MATCH_a_capture_against_an_empty_hold_set_is_rejected()
    {
        // No active hold at all (the reservation was already captured/expired, or never placed): the target is
        // absent → rejected. This is the command-time half of the double guard; a reissue after the hold left
        // the ACTIVE set lands NO second Debit (the projection-time WHERE state='ACTIVE' is the other half).
        Assert.Throws<DomainRejectedException>(
            () => CurrentAccountCaptureDecider.Decide(AccountRef, [], Command()));
    }

    [Fact]
    public void SETTLEMENT_CA_CAPTURE_HOLD_MATCH_a_legal_hold_with_the_target_id_does_not_match_an_authorization_capture()
    {
        // A LEGAL hold (a court order) is not an authorization reservation — even sharing the target id it is
        // not a capture target, so the capture is rejected (the kind guard mirrors the store's kind='LEGAL' split).
        var legalHold = ActiveAuthorizationHold() with { Kind = HoldKind.Legal, LegalReference = "case-123" };
        Assert.Throws<DomainRejectedException>(
            () => CurrentAccountCaptureDecider.Decide(AccountRef, [legalHold], Command()));
    }

    // --- ADR-PC-037 partial / over-capture reconciliation ---

    [Fact]
    public void A_partial_capture_surfaces_PARTIAL_CAPTURE_and_still_lands_one_Debit()
    {
        // Captured 300.00 against a 500.00 hold: the remainder is released, and the partial is surfaced as a
        // reconciliation signal (ADR-PC-037), never silently absorbed. One Debit still lands (the captured amount).
        var decision = CurrentAccountCaptureDecider.Decide(
            AccountRef, [ActiveAuthorizationHold(amountCents: 50_000)], Command(amountCents: 30_000));

        Assert.Equal(SettlementReconciliation.PartialCapture, decision.Reconciliation);
        var debited = Assert.IsType<AccountDebited>(decision.Events[1]);
        Assert.Equal(30_000, Assert.Single(((IMovementBearing)debited).Movements).Amount.Cents);
    }

    [Fact]
    public void An_over_capture_surfaces_OVER_CAPTURED_and_still_lands_one_Debit()
    {
        // Captured 600.00 against a 500.00 hold: the money moves (the transition still happens) but the
        // over-capture is surfaced (ADR-PC-037 / HoldReleaseResult.TransitionedOverCaptured), never absorbed.
        var decision = CurrentAccountCaptureDecider.Decide(
            AccountRef, [ActiveAuthorizationHold(amountCents: 50_000)], Command(amountCents: 60_000));

        Assert.Equal(SettlementReconciliation.OverCaptured, decision.Reconciliation);
        var debited = Assert.IsType<AccountDebited>(decision.Events[1]);
        Assert.Equal(60_000, Assert.Single(((IMovementBearing)debited).Movements).Amount.Cents);
    }

    [Fact]
    public void A_non_positive_capture_is_rejected_before_matching()
    {
        Assert.Throws<DomainRejectedException>(
            () => CurrentAccountCaptureDecider.Decide(AccountRef, [ActiveAuthorizationHold()], Command(amountCents: 0)));
    }
}
