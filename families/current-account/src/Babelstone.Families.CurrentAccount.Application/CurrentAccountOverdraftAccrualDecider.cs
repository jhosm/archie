using Babelstone.Engine;
using Babelstone.Families.CurrentAccount;
using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;

namespace Babelstone.Families.CurrentAccount.Application;

/// <summary>
/// The PURE overdraft-interest accrual decision (ADR-PC-037 §D5 / §P3): given a drawn balance, a resolved TAN,
/// and the accrual's value-date, compute the day's fee and shape the <see cref="OverdraftInterestAccrued"/>
/// fact that posts it — no clock, no I/O, no randomness, so the accrual is a deterministic function of its
/// inputs (ADR-PC-010 §P5) and unit-testable Docker-free. The impure
/// <see cref="CurrentAccountOverdraftAccrualService"/> does the reads (load the account, read the balance,
/// resolve the rate) and hands the resolved inputs here; this decider owns only the money math and the event
/// shape (the same pure-decider / impure-shell split the authorize path takes).
/// </summary>
public static class CurrentAccountOverdraftAccrualDecider
{
    /// <summary>
    /// The v1 day-count basis for the overdraft accrual — Act/365, the basis
    /// <c>CurrentAccountOverdraftTests</c> pins. A richer overdraft product could carry the day-count as a pack
    /// field (tiered rates, a grace corridor); v1 fixes it as the coarse-start (ADR-PC-037 §Residual-risks).
    /// </summary>
    public const int DayCountBasis = 365;

    /// <summary>
    /// Decide the accrual: one day's overdraft interest on the drawn balance at <paramref name="tanBasisPoints"/>,
    /// or <see langword="null"/> when nothing accrues (the balance is not drawn, or the day's interest rounds
    /// to zero cents). The fee is <c>Accrual.DailyBalanceInterest</c> over the NEGATIVE balance — which yields
    /// the negative interest (the fee owed) through the single <c>Money.FromCents</c> HALF_EVEN boundary
    /// (ADR-PC-010 §P1–§P2, rounded once, never mid-calculation) — posted as its magnitude in a Debit
    /// <see cref="Movement"/> so the accounting balance moves further below zero. All inputs are supplied
    /// (the value-date is the caller's, never a clock read), so the result is replay-deterministic.
    /// </summary>
    public static OverdraftInterestAccrued? Decide(
        Guid accountId,
        string accountRef,
        long accountingBalanceCents,
        int tanBasisPoints,
        string rateSheetVersionId,
        DateOnly accrualDate,
        Guid commandId)
    {
        // Only a drawn (negative) balance accrues overdraft interest — a non-negative balance is nothing owed.
        if (accountingBalanceCents >= 0)
        {
            return null;
        }

        // One day of interest on the drawn balance, Act/365. The demand-account primitive guards only the time
        // dimension, so a negative balance yields the negative interest (the fee owed); the magnitude is the
        // Debit posted. The whole numerator accumulates in decimal and crosses to Money exactly once.
        var fee = Accrual.DailyBalanceInterest(
            [(new Money(accountingBalanceCents), 1)], tanBasisPoints, DayCountBasis);
        var feeMagnitude = -fee; // fee.Cents ≤ 0 for a drawn balance; the magnitude is what the Debit moves.

        // A drawn balance so small the day's interest rounds to zero cents accrues nothing — no zero-value
        // Movement, no empty accrual fact on the stream.
        if (feeMagnitude.Cents <= 0)
        {
            return null;
        }

        var movement = new Movement(
            AccountRef: accountRef,
            Direction: SettlementDirection.Debit,
            Amount: feeMagnitude,
            ValueDate: accrualDate,
            Operation: MovementOperation.AccrueOverdraftInterest,
            // Observed, in the ADR-PC-043 "engine-internal already-effected" sense: the engine charges the fee
            // directly onto its own account's balance in one append — there is no external counterparty and no
            // cash leg to drive, so the movement is already-effected, not an Originated move awaiting settlement.
            // Observed makes MovementHeaders emit no Originated header, so the settlement predicate starts no
            // saga on the account's own event (the ADR-PC-043 loop-breaker). current_account is an Observed-mode
            // family (commitment XC-3).
            Origin: MovementOrigin.Observed,
            CommandId: commandId);

        return new OverdraftInterestAccrued(
            AccountId: accountId,
            AccountRef: accountRef,
            InterestAmount: feeMagnitude,
            TanBasisPoints: tanBasisPoints,
            RateSheetVersionId: rateSheetVersionId,
            AccruedOn: accrualDate,
            Movements: [movement]);
    }
}
