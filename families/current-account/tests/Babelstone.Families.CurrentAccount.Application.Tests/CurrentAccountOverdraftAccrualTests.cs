using Babelstone.Engine;
using Babelstone.Families.CurrentAccount;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.CurrentAccount.Application.Tests;

/// <summary>
/// The OVERDRAFT_ACCRUAL_POSTS_MOVEMENT commitment (ADR-PC-037 §D5, commitment CA-3): a used overdraft accrues
/// a day's interest at the rate-sheet-resolved TAN, posting the fee as a Debit <see cref="Movement"/> against
/// the drawn balance — the math command-side (never a fold), the fee to the cent, and the whole event a pure,
/// replay-deterministic function of its inputs. In plain English: this pins that when an account sits below
/// zero, the accrual charges exactly one day of overdraft interest on the drawn amount and records it as a
/// debit that deepens the overdraft — and that re-deciding the same day produces the identical fact (so a
/// replay reproduces the accrued fee).
/// </summary>
/// <remarks>
/// This is the command-side accrual DECISION (<see cref="CurrentAccountOverdraftAccrualDecider"/>), the pure
/// half of the accrual path — the impure shell's rate resolution + idempotent append is the Testcontainers
/// integration tier. Docker-free (the CA-3 "unit" lane). The underlying <c>Accrual.DailyBalanceInterest</c>
/// arithmetic over a negative balance is pinned to the cent by <see cref="CurrentAccountOverdraftTests"/>
/// (CA-1); this suite pins the DECIDER wrapping it — the fee sign/magnitude, the Debit direction and
/// <c>AccrueOverdraftInterest</c> operation, the audit stamps, and the no-accrual short-circuits.
/// </remarks>
public sealed class CurrentAccountOverdraftAccrualTests
{
    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly string AccountRef = AccountId.ToString();
    private static readonly DateOnly AccrualDate = new(2026, 3, 5);
    private static readonly Guid CommandId = Guid.NewGuid();

    // A clean, exact case: EUR 3 650 drawn (a −365 000-cent balance) for ONE day at 10.00% (1000 bps), Act/365,
    // is 3650 × 0.10 / 365 = EUR 1.00 = 100 cents of interest owed — no rounding to obscure the shape.
    private const long DrawnBalanceCents = -365_000;
    private const int OverdraftTanBps = 1000;
    private const string SheetVersion = "pt-overdrafts-2026.1";
    private const long ExpectedFeeCents = 100;

    [Fact]
    public void OVERDRAFT_ACCRUAL_POSTS_MOVEMENT_the_accrual_posts_the_resolved_rate_fee_as_a_debit_movement()
    {
        var accrued = CurrentAccountOverdraftAccrualDecider.Decide(
            AccountId, AccountRef, DrawnBalanceCents, OverdraftTanBps, SheetVersion, AccrualDate, CommandId);

        // A drawn balance accrues — the event is produced, carrying the audit stamps for the day's charge.
        Assert.NotNull(accrued);
        Assert.Equal(AccountId, accrued!.AccountId);
        Assert.Equal(AccountRef, accrued.AccountRef);
        Assert.Equal(ExpectedFeeCents, accrued.InterestAmount.Cents); // positive magnitude (the fee owed)
        Assert.Equal(OverdraftTanBps, accrued.TanBasisPoints);
        Assert.Equal(SheetVersion, accrued.RateSheetVersionId); // pinned for audit/replay (ADR-PC-008)
        Assert.Equal(AccrualDate, accrued.AccruedOn);

        // The fee posts as EXACTLY ONE Debit Movement against the account_ref: a Debit SUBTRACTS (Credit adds,
        // Debit subtracts), so it deepens the overdraft — the balance moves further below zero. Its origin is
        // Observed (ADR-PC-043 engine-internal-already-effected): the engine effects the charge directly on the
        // account's own balance, with no external counterparty and no cash leg to settle.
        var movement = Assert.Single(((IMovementBearing)accrued).Movements);
        Assert.Equal(AccountRef, movement.AccountRef);
        Assert.Equal(SettlementDirection.Debit, movement.Direction);
        Assert.Equal(ExpectedFeeCents, movement.Amount.Cents);
        Assert.Equal(AccrualDate, movement.ValueDate);
        Assert.Equal(MovementOperation.AccrueOverdraftInterest, movement.Operation);
        Assert.Equal(MovementOrigin.Observed, movement.Origin);
        Assert.Equal(CommandId, movement.CommandId);
    }

    [Fact]
    public void OVERDRAFT_ACCRUAL_POSTS_MOVEMENT_re_deciding_the_same_day_is_deterministic()
    {
        // The decider is a pure function of its inputs (ADR-PC-010 §P5): the same drawn balance + rate + day
        // decide the identical accrual fact, so a replay of the day's accrual reproduces the accrued fee. (The
        // event's scalar fields + its single Movement are compared field-for-field; a whole-record Assert.Equal
        // would compare the Movements LIST by reference, which differs per call even when the values match.)
        var first = CurrentAccountOverdraftAccrualDecider.Decide(
            AccountId, AccountRef, DrawnBalanceCents, OverdraftTanBps, SheetVersion, AccrualDate, CommandId);
        var second = CurrentAccountOverdraftAccrualDecider.Decide(
            AccountId, AccountRef, DrawnBalanceCents, OverdraftTanBps, SheetVersion, AccrualDate, CommandId);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.InterestAmount, second!.InterestAmount);
        Assert.Equal(first.TanBasisPoints, second.TanBasisPoints);
        Assert.Equal(first.RateSheetVersionId, second.RateSheetVersionId);
        Assert.Equal(first.AccruedOn, second.AccruedOn);
        Assert.Equal(first.AccountId, second.AccountId);
        Assert.Equal(first.AccountRef, second.AccountRef);
        // Movement IS a record over value-type / string fields, so its equality is structural — the single
        // fee Movement re-decides identically.
        Assert.Equal(
            Assert.Single(((IMovementBearing)first).Movements),
            Assert.Single(((IMovementBearing)second).Movements));
    }

    [Fact]
    public void OVERDRAFT_ACCRUAL_POSTS_MOVEMENT_a_non_drawn_balance_accrues_nothing()
    {
        // A non-negative balance owes no overdraft interest — the decider short-circuits to null (no event,
        // no zero-value Movement), so the accrual command is a no-op for an account not in overdraft.
        Assert.Null(CurrentAccountOverdraftAccrualDecider.Decide(
            AccountId, AccountRef, accountingBalanceCents: 0, OverdraftTanBps, SheetVersion, AccrualDate, CommandId));
        Assert.Null(CurrentAccountOverdraftAccrualDecider.Decide(
            AccountId, AccountRef, accountingBalanceCents: 250_000, OverdraftTanBps, SheetVersion, AccrualDate, CommandId));
    }

    [Fact]
    public void OVERDRAFT_ACCRUAL_POSTS_MOVEMENT_a_fee_that_rounds_to_zero_cents_accrues_nothing()
    {
        // A drawn balance so small the day's interest rounds to zero cents accrues nothing (no empty accrual
        // fact on the stream): 1000 bps on −1 cent for one day is 1000 × −1 / (365 × 10 000) ≈ −0.0003 cents,
        // which the single Money.FromCents boundary rounds to 0 → no Movement, no event.
        Assert.Null(CurrentAccountOverdraftAccrualDecider.Decide(
            AccountId, AccountRef, accountingBalanceCents: -1, OverdraftTanBps, SheetVersion, AccrualDate, CommandId));
    }
}
