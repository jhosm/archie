using Babelstone.Engine;
using Babelstone.Families.CurrentAccount;
using Babelstone.FinancialTypes;

namespace Babelstone.Families.CurrentAccount.Application;

/// <summary>
/// The pure decision core of the settlement CAPTURE (ADR-PC-043 §2 ConfirmDebit → HoldCaptured + Debit
/// Movement / ADR-PC-037 §D4): given the account's active-hold set and a capture command, it enforces the
/// hold match, classifies a partial / over-capture, and produces the ONE atomic append batch (the spine
/// <see cref="HoldCaptured"/> + the family <see cref="AccountDebited"/>). In plain English: this is the
/// "which reservation does this capture settle, and does the amount match?" brain — it reads the holds and
/// answers, but does no I/O and touches no clock, so it is unit-tested Docker-free. The impure
/// <see cref="CurrentAccountCaptureService"/> does the reads (load the account, drain, read the holds) and
/// the append; this decider owns the match rule, the reconciliation classification, and the event shape.
/// </summary>
/// <remarks>
/// <b>Enforce authorize.hold_id == capture.target_hold_id for one intent (ADR-PC-043,
/// SETTLEMENT_CA_CAPTURE_HOLD_MATCH).</b> The target MUST name an ACTIVE authorization hold on this account —
/// GetActiveHoldsAsync returns only ACTIVE rows, so a hold already captured/expired (or never placed) is
/// absent → a <see cref="DomainRejectedException"/>, never a silent no-op or a phantom debit. This is the
/// command-time half of the ADR-PC-043 §4 double guard; the projection-time half is the store's
/// <c>UPDATE … WHERE state='ACTIVE'</c> (so a reissue lands EXACTLY ONE Debit).
/// </remarks>
public static class CurrentAccountCaptureDecider
{
    /// <summary>
    /// Decide a capture: match the target hold against <paramref name="activeHolds"/>, classify the
    /// capture amount (a full capture, a partial capture releasing the remainder, or an over-capture —
    /// ADR-PC-037 §D4), and return the atomic <see cref="HoldCaptured"/> + <see cref="AccountDebited"/>
    /// batch plus the reconciliation signal (null on a clean full capture). Rejects a non-positive amount or a
    /// target that names no active authorization hold with <see cref="DomainRejectedException"/> before any
    /// append.
    /// </summary>
    /// <param name="accountRef">The account's opaque spine key the movements post against.</param>
    /// <param name="activeHolds">The account's currently-ACTIVE holds (the drained read-model set).</param>
    /// <param name="command">The capture attempt (target hold id, positive amount, value-date, intent ref).</param>
    public static CaptureDecision Decide(
        string accountRef, IReadOnlyList<Hold> activeHolds, CaptureAccountCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountRef);
        ArgumentNullException.ThrowIfNull(activeHolds);
        ArgumentNullException.ThrowIfNull(command);

        // A non-positive capture is not a settlement — reject before deciding, so it never becomes a phantom
        // zero-value Debit (the endpoint surfaces it as a 400).
        if (command.AmountCents <= 0)
        {
            throw new DomainRejectedException(
                "capture requires a positive amount in integer cents (ADR-PC-010).");
        }

        // Hold-match (SETTLEMENT_CA_CAPTURE_HOLD_MATCH): the target must name an ACTIVE authorization hold on
        // this account. Absent ⇒ reject (a 4xx / HIR park), never a phantom debit on an unmatched hold.
        var target = activeHolds.FirstOrDefault(
            h => h.HoldId == command.TargetHoldId && h.Kind == HoldKind.Authorization);
        if (target is null)
        {
            throw new DomainRejectedException(
                $"capture target hold '{command.TargetHoldId}' is not an active authorization hold on account "
                + $"{command.AccountId} — the capture's target_hold_id must match the authorize's hold "
                + "(ADR-PC-043 §Idempotency).");
        }

        // Classify the capture amount against the held amount (ADR-PC-037 §D4): under releases the remainder,
        // over is a surfaced reconciliation signal, equal is a clean full capture (null).
        var reconciliation =
            command.AmountCents > target.Amount.Cents ? SettlementReconciliation.OverCaptured
            : command.AmountCents < target.Amount.Cents ? SettlementReconciliation.PartialCapture
            : (string?)null;

        // ONE atomic batch: the spine-owned HoldCaptured (the earmark release, REUSED — ADR-PC-033) THEN the
        // family AccountDebited (the Debit Movement). Both carry the same hold id so the capture and its Debit
        // name one earmark. The HoldCaptured's InstanceId is the account stream (the projector's own-stream
        // precondition).
        var captured = new HoldCaptured(
            InstanceId: command.AccountId,
            HoldId: command.TargetHoldId,
            AccountRef: accountRef,
            CapturedAmount: new Money(command.AmountCents),
            ValueDate: command.ValueDate);

        var debited = new AccountDebited(
            AccountId: command.AccountId,
            AccountRef: accountRef,
            Amount: new Money(command.AmountCents),
            HoldId: command.TargetHoldId,
            IntentReference: command.IntentReference,
            ValueDate: command.ValueDate,
            Movements: [DebitMovement(accountRef, command)]);

        return new CaptureDecision([captured, debited], reconciliation);
    }

    // The captured Debit as ONE Observed Movement against the account_ref: a Debit lowers the accounting
    // balance. Observed in the ADR-PC-043 engine-internal-already-effected sense — the settlement saga already
    // drove the cash leg, so MovementHeaders emits no Originated header and no SECOND saga starts on the
    // account's own event (the loop-breaker).
    private static Movement DebitMovement(string accountRef, CaptureAccountCommand command) => new(
        AccountRef: accountRef,
        Direction: SettlementDirection.Debit,
        Amount: new Money(command.AmountCents),
        ValueDate: command.ValueDate,
        // The generic money-OUT verb the CA capture debit lands under (ADR-PC-032 §1 — a confirmed debit is
        // deposit funding / an installment collection). The CA is family-agnostic and does not learn the
        // source's specific occurrence, so it reuses the closest existing MovementOperation rather than
        // widening the closed enum (a dedicated CA settle-debit verb + its governed Avro carrier symbol is bd
        // babelstone-98mj.8). The operation is a fold LABEL; the Debit DIRECTION is what moves the balance.
        Operation: MovementOperation.CollectInstallment,
        Origin: MovementOrigin.Observed,
        CommandId: command.CommandId);
}

/// <summary>The pure capture decision: the ONE atomic append batch (the spine <see cref="HoldCaptured"/>
/// followed by the family <see cref="AccountDebited"/>) and the ADR-PC-037 §D4 reconciliation signal (a
/// partial / over-capture, or null on a clean full capture).</summary>
public sealed record CaptureDecision(IReadOnlyList<DomainEvent> Events, string? Reconciliation);
