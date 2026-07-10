using Babelstone.Engine;
using Babelstone.Families.CurrentAccount;
using Babelstone.FinancialTypes;

namespace Babelstone.Families.CurrentAccount.Application;

/// <summary>
/// The pure credit-ADMISSION decision core (ADR-PC-043 §The credit-admission gate): given the account's
/// folded <see cref="AccountPosition"/> lifecycle and a credit-receive command, it decides whether the
/// credit may LAND — ADMIT (produce the events to append) or REJECT (throw
/// <see cref="DomainRejectedException"/>) — BEFORE anything is recorded. In plain English: a current
/// account can only receive money if it is open (or dormant, which reactivates); a closed or erased
/// account refuses the credit by construction, so the generic movement-ledger fold never folds a credit
/// into an account that cannot receive it. No clock, no I/O, no randomness — every date is supplied on the
/// command, so this is unit-tested Docker-free.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate is strictly UPSTREAM of the fold (ADR-PC-043 §The credit-admission gate).</b> The generic
/// <c>movement_ledger</c> fold is lifecycle-BLIND — it would fold a credit into a Closed/Erased account —
/// so admission is decided HERE, in the family, at ingestion, exactly as <c>authorize</c> gates debits
/// upstream. The impure shell (<see cref="CurrentAccountCreditReceiveService"/>) reads the account's own
/// stream, calls this decider, and appends the produced events at the LOADED expectedVersion under
/// per-stream OCC — so the admitted-or-rejected decision is serialized against a concurrent Close/Erase by
/// the same stale-head check that serializes authorize against concurrent debits (CREDIT_ADMISSION_OWN_STREAM_OCC).
/// </para>
/// <para>
/// <b>Dormant admits atomically (ADR-PC-043 §The credit-admission gate, load-bearing invariant).</b> A
/// Dormant account may fire <see cref="AccountReactivated"/> per pack policy, and the reactivation +
/// the credit MUST be ONE atomic append batch — the decider returns BOTH events in a single list so the
/// shell appends them together; a Close cannot wedge between them (CREDIT_REACTIVATE_CREDIT_ATOMIC_BATCH).
/// Resurrection is impossible: the lifecycle legality table has no Closed→Active / Erased→Active edge, so
/// only a genuinely-live account is ever admitted.
/// </para>
/// </remarks>
public static class CurrentAccountCreditAdmissionDecider
{
    /// <summary>
    /// Decide a credit-receive attempt. On an Active account, returns a single
    /// <see cref="AccountCredited"/>; on a Dormant account, returns <see cref="AccountReactivated"/> THEN
    /// <see cref="AccountCredited"/> as ONE atomic batch (reactivate-then-credit). A Closed account is
    /// rejected <c>ACCOUNT_CLOSED</c> and an Erased one <c>ACCOUNT_ERASED</c> (a Pending / Failed account,
    /// having never opened, is rejected as ACCOUNT_NOT_OPEN) — every rejection is a
    /// <see cref="DomainRejectedException"/> before any append, so no credit ever folds into an account
    /// that cannot receive it.
    /// </summary>
    /// <param name="current">The account's folded lifecycle position (the source of the admission gate).</param>
    /// <param name="command">The credit-receive attempt (opaque account_ref, positive amount, value-date, intent ref).</param>
    /// <exception cref="DomainRejectedException">If the amount is non-positive, or the account is not admissible.</exception>
    public static IReadOnlyList<DomainEvent> Decide(AccountPosition current, ReceiveCreditCommand command)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(command);

        // Structural gate: a non-positive credit is not a credit at all — reject before deciding admission,
        // so it never becomes an admission code (the endpoint surfaces it as a 400).
        if (command.AmountCents <= 0)
        {
            throw new DomainRejectedException(
                "credit-receive requires a positive amount in integer cents (ADR-PC-010).");
        }

        var accountRef = current.AccountRef;
        var movement = new Movement(
            AccountRef: accountRef,
            Direction: SettlementDirection.Credit,
            Amount: new Money(command.AmountCents),
            ValueDate: command.ValueDate,
            // The generic money-IN verb the CA credit lands under (ADR-PC-032 §1 — a maturity/coupon/
            // disbursement payout are all credit-in). The CA is family-agnostic and does not learn the
            // source's specific occurrence, so it reuses the closest existing MovementOperation rather than
            // widening the closed enum (a dedicated CA credit verb + its governed Avro carrier symbol is bd
            // babelstone-98mj.8). The operation is a fold LABEL; the Credit DIRECTION is what moves the balance.
            Operation: MovementOperation.PayMaturity,
            // Observed, in the ADR-PC-043 "engine-internal already-effected" sense: the settlement saga
            // already drove the cash leg and the CA records the landing — no Originated header, so the
            // settlement predicate starts no SECOND saga on the account's own event (the loop-breaker).
            Origin: MovementOrigin.Observed,
            CommandId: command.CommandId);

        var credited = new AccountCredited(
            AccountId: command.AccountId,
            AccountRef: accountRef,
            Amount: new Money(command.AmountCents),
            IntentReference: command.IntentReference,
            ValueDate: command.ValueDate,
            Movements: [movement]);

        return current.Lifecycle switch
        {
            // Active admits directly — one Credit landing.
            AccountLifecycle.Active => [credited],

            // Dormant admits with a reactivation FIRST, in ONE atomic batch (the load-bearing invariant): a
            // dormant account is used again by this credit, so it reactivates and lands the credit together —
            // a Close cannot wedge between them (CREDIT_REACTIVATE_CREDIT_ATOMIC_BATCH).
            AccountLifecycle.Dormant =>
            [
                new AccountReactivated(command.AccountId, command.ValueDate),
                credited,
            ],

            // Closed / Erased are genuinely-unreceivable terminals — refused by construction (no resurrection
            // edge exists), each with its own machine code so the caller / reconciler can attribute the
            // undeliverable credit (ADR-PC-043 §Undeliverable credit — the source holds the funds).
            AccountLifecycle.Closed => throw Reject(command.AccountId, CreditRejectedReason.AccountClosed, current.Lifecycle),
            AccountLifecycle.Erased => throw Reject(command.AccountId, CreditRejectedReason.AccountErased, current.Lifecycle),

            // Pending (never opened) / Failed (open rejected): there is no account to credit — reject as
            // ACCOUNT_NOT_OPEN rather than silently opening one.
            _ => throw Reject(command.AccountId, CreditRejectedReason.AccountNotOpen, current.Lifecycle),
        };
    }

    private static DomainRejectedException Reject(Guid accountId, string reasonCode, AccountLifecycle lifecycle) =>
        new($"current_account {accountId} cannot receive a credit: {reasonCode} (lifecycle {lifecycle}).");
}
