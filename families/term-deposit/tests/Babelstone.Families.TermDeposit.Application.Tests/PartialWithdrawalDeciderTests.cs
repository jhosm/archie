using Babelstone.Engine;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// The pure partial-withdrawal decision core (F.12, bd babelstone-k6r8.5; ADR-PC-021 §P3) — no I/O, no
/// clock, default CI lane. Pins the three F.12 policy gates (minimum withdrawal amount, minimum
/// remaining balance, lock-up) plus the structural rules the decider always applies (positive
/// amount; cannot withdraw the whole balance — that is a termination, F.4) and the F.3 lifecycle gate.
/// A partial withdrawal is a PRINCIPAL reduction only: there is no interest/withholding/settlement flow
/// to assert, only the exact integer-cent <c>remaining = current − withdrawn</c> (ADR-PC-010 §P1).
/// </summary>
public sealed class PartialWithdrawalDeciderTests
{
    private static readonly DateOnly Start = new(2026, 1, 15);
    private const long PrincipalCents = 1_000_000; // EUR 10,000.00

    private static DepositPosition ActivePosition(long remainingCents = PrincipalCents) =>
        DepositPosition.Empty with
        {
            DepositId = Guid.NewGuid(),
            Principal = new Money(PrincipalCents),
            TanBasisPoints = 300,
            TermDays = 365,
            StartDate = Start,
            MaturityDate = Start.AddDays(365),
            InterestVariant = "AT_MATURITY",
            RemainingPrincipal = new Money(remainingCents),
            Lifecycle = DepositLifecycle.Active,
        };

    private static DepositPartiallyWithdrawn DecodeSingle(IReadOnlyList<DomainEvent> events)
    {
        var withdrawn = Assert.IsType<DepositPartiallyWithdrawn>(Assert.Single(events));
        return withdrawn;
    }

    // ---- happy path: a permitted withdrawal reduces the principal --------------------------------

    [Fact]
    public void Permitted_withdrawal_emits_one_event_reducing_the_principal_exactly()
    {
        // min 1,000.00 withdrawal, min 2,000.00 remaining, 30-day lock-up. Withdraw 3,000.00 at day 60.
        var policy = new PartialWithdrawalPolicy(
            MinWithdrawalCents: 100_000, MinRemainingBalanceCents: 200_000, LockupPeriodDays: 30);

        var events = PartialWithdrawalDecider.Decide(
            ActivePosition(), new Money(300_000), Start.AddDays(60), policy);

        var withdrawn = DecodeSingle(events);
        Assert.Equal(new Money(300_000), withdrawn.WithdrawnAmount);
        // Exact integer-cent subtraction: 1,000,000 − 300,000 = 700,000. No rounding boundary.
        Assert.Equal(new Money(700_000), withdrawn.RemainingPrincipal);
        Assert.Equal(Start.AddDays(60), withdrawn.WithdrawnOn);
    }

    [Fact]
    public void Unrestricted_policy_imposes_no_gate_beyond_the_structural_rules()
    {
        // No minimums, no lock-up: any positive amount strictly below the balance passes, even on day 0.
        var events = PartialWithdrawalDecider.Decide(
            ActivePosition(), new Money(1), Start, PartialWithdrawalPolicy.Unrestricted);

        var withdrawn = DecodeSingle(events);
        Assert.Equal(new Money(1), withdrawn.WithdrawnAmount);
        Assert.Equal(new Money(PrincipalCents - 1), withdrawn.RemainingPrincipal);
    }

    // ---- F.12 minimum withdrawal amount ----------------------------------------------------------

    [Fact]
    public void Withdrawal_below_the_minimum_amount_is_refused()
    {
        var policy = new PartialWithdrawalPolicy(
            MinWithdrawalCents: 100_000, MinRemainingBalanceCents: 0, LockupPeriodDays: 0);

        var ex = Assert.Throws<DomainRejectedException>(() => PartialWithdrawalDecider.Decide(
            ActivePosition(), new Money(99_999), Start, policy));
        Assert.Contains("minimum withdrawal amount", ex.Message);
    }

    [Fact]
    public void Withdrawal_exactly_at_the_minimum_amount_passes()
    {
        var policy = new PartialWithdrawalPolicy(
            MinWithdrawalCents: 100_000, MinRemainingBalanceCents: 0, LockupPeriodDays: 0);

        // Boundary value passes (inclusive "at least").
        var events = PartialWithdrawalDecider.Decide(
            ActivePosition(), new Money(100_000), Start, policy);
        Assert.Equal(new Money(900_000), DecodeSingle(events).RemainingPrincipal);
    }

    // ---- F.12 minimum remaining balance ----------------------------------------------------------

    [Fact]
    public void Withdrawal_leaving_below_the_minimum_remaining_balance_is_refused()
    {
        // min remaining 500,000; withdrawing 600,000 would leave 400,000 — refused.
        var policy = new PartialWithdrawalPolicy(
            MinWithdrawalCents: 0, MinRemainingBalanceCents: 500_000, LockupPeriodDays: 0);

        var ex = Assert.Throws<DomainRejectedException>(() => PartialWithdrawalDecider.Decide(
            ActivePosition(), new Money(600_000), Start, policy));
        Assert.Contains("minimum remaining balance", ex.Message);
    }

    [Fact]
    public void Withdrawal_leaving_exactly_the_minimum_remaining_balance_passes()
    {
        // min remaining 500,000; withdrawing 500,000 leaves exactly 500,000 — the boundary passes.
        var policy = new PartialWithdrawalPolicy(
            MinWithdrawalCents: 0, MinRemainingBalanceCents: 500_000, LockupPeriodDays: 0);

        var events = PartialWithdrawalDecider.Decide(
            ActivePosition(), new Money(500_000), Start, policy);
        Assert.Equal(new Money(500_000), DecodeSingle(events).RemainingPrincipal);
    }

    // ---- F.12 lock-up period ---------------------------------------------------------------------

    [Fact]
    public void Withdrawal_inside_the_lockup_is_refused()
    {
        // 90-day lock-up; a withdrawal on day 89 (one day before the window closes) is refused.
        var policy = new PartialWithdrawalPolicy(
            MinWithdrawalCents: 0, MinRemainingBalanceCents: 0, LockupPeriodDays: 90);

        var ex = Assert.Throws<DomainRejectedException>(() => PartialWithdrawalDecider.Decide(
            ActivePosition(), new Money(100_000), Start.AddDays(89), policy));
        Assert.Contains("lock-up", ex.Message);
    }

    [Fact]
    public void Withdrawal_on_the_first_day_after_the_lockup_passes()
    {
        // 90-day lock-up; the earliest permitted date is Start + 90 days, and that boundary day passes.
        var policy = new PartialWithdrawalPolicy(
            MinWithdrawalCents: 0, MinRemainingBalanceCents: 0, LockupPeriodDays: 90);

        var events = PartialWithdrawalDecider.Decide(
            ActivePosition(), new Money(100_000), Start.AddDays(90), policy);
        Assert.Equal(Start.AddDays(90), DecodeSingle(events).WithdrawnOn);
    }

    // ---- structural rules the decider always applies ---------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_withdrawal_is_refused(long cents)
    {
        var ex = Assert.Throws<DomainRejectedException>(() => PartialWithdrawalDecider.Decide(
            ActivePosition(), new Money(cents), Start, PartialWithdrawalPolicy.Unrestricted));
        Assert.Contains("positive amount", ex.Message);
    }

    [Fact]
    public void Withdrawing_the_whole_balance_is_an_early_termination_not_a_partial_withdrawal()
    {
        // Reducing the principal to exactly zero is a termination (F.4) — refused as a partial withdrawal.
        var ex = Assert.Throws<DomainRejectedException>(() => PartialWithdrawalDecider.Decide(
            ActivePosition(), new Money(PrincipalCents), Start, PartialWithdrawalPolicy.Unrestricted));
        Assert.Contains("early termination", ex.Message);
    }

    [Fact]
    public void Withdrawing_more_than_is_on_deposit_is_refused()
    {
        var ex = Assert.Throws<DomainRejectedException>(() => PartialWithdrawalDecider.Decide(
            ActivePosition(), new Money(PrincipalCents + 1), Start, PartialWithdrawalPolicy.Unrestricted));
        Assert.Contains("not", ex.Message);
    }

    [Fact]
    public void A_second_withdrawal_is_decided_against_the_already_reduced_remaining_principal()
    {
        // After a first withdrawal the position carries the REDUCED remaining principal; the minimum
        // remaining gate is measured against that, not the original principal.
        var policy = new PartialWithdrawalPolicy(
            MinWithdrawalCents: 0, MinRemainingBalanceCents: 500_000, LockupPeriodDays: 0);

        // Remaining is already 700,000. Withdrawing 250,000 leaves 450,000 — below the 500,000 floor.
        var ex = Assert.Throws<DomainRejectedException>(() => PartialWithdrawalDecider.Decide(
            ActivePosition(remainingCents: 700_000), new Money(250_000), Start, policy));
        Assert.Contains("minimum remaining balance", ex.Message);
    }

    // ---- F.3 lifecycle gate: partial withdrawal is legal only from Active -------------------------

    [Theory]
    [InlineData(DepositLifecycle.Pending)]
    [InlineData(DepositLifecycle.Matured)]
    [InlineData(DepositLifecycle.TerminatedEarly)]
    [InlineData(DepositLifecycle.Renewed)]
    [InlineData(DepositLifecycle.TransferredToHeirs)]
    [InlineData(DepositLifecycle.Failed)]
    public void A_withdrawal_is_refused_from_any_non_Active_lifecycle(DepositLifecycle lifecycle)
    {
        var position = ActivePosition() with { Lifecycle = lifecycle };

        var ex = Assert.Throws<DomainRejectedException>(() => PartialWithdrawalDecider.Decide(
            position, new Money(100_000), Start.AddDays(60), PartialWithdrawalPolicy.Unrestricted));
        Assert.Contains("legal only from Active", ex.Message);
    }

    // ---- product-shape gate: forbidden on ADVANCE (interest in advance) (bd babelstone-emtr) ------

    [Fact]
    public void Partial_withdrawal_is_forbidden_on_an_ADVANCE_product()
    {
        // ADVANCE pays the whole term's interest up front on the FULL principal, so a later principal
        // reduction would leave interest paid on money no longer on deposit with no flow to re-base it.
        // The product shape itself is refused — even with an Unrestricted policy and every other gate clear.
        var advance = ActivePosition() with { InterestVariant = "ADVANCE" };

        var ex = Assert.Throws<DomainRejectedException>(() => PartialWithdrawalDecider.Decide(
            advance, new Money(100_000), Start.AddDays(60), PartialWithdrawalPolicy.Unrestricted));
        Assert.Contains("advance", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
