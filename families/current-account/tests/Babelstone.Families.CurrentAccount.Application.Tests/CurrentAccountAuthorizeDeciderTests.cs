using Babelstone.Engine;
using Babelstone.Families.CurrentAccount;
using Babelstone.Families.CurrentAccount.Application;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.CurrentAccount.Application.Tests;

/// <summary>
/// Pure decider tests for the current_account synchronous AUTHORIZE decider (ADR-PC-037 §D6 /
/// ADR-PC-034): given a folded <see cref="AccountPosition"/>, a read available balance, the pack rules,
/// and any active freeze, the decider produces the ONE event the shell appends — an
/// <c>operations.HoldPlaced</c> earmark (authorized) or a family <see cref="AuthorizationDeclined"/>
/// refusal fact (declined). No engine, no Docker — every input is explicit. These cover the FAMILY layer
/// only: the lifecycle gate the spine does not do, and the mapping onto the D6 taxonomy. The spine gate
/// order (freeze → per-transaction → funds/overdraft) is proven by FundsAndRulesDeciderTests and not
/// re-tested here.
/// </summary>
public sealed class CurrentAccountAuthorizeDeciderTests
{
    private static readonly Guid AccountId = Guid.NewGuid();
    private const string HoldId = "hold-under-test";
    private static readonly DateOnly ValueDate = new(2026, 3, 5);

    private static AccountPosition InState(AccountLifecycle lifecycle) =>
        AccountPosition.Empty with { AccountId = AccountId, ProductCode = "ca_pt_standard", Currency = "EUR", Lifecycle = lifecycle };

    private static AuthorizationRequest Request(long amountCents) =>
        new(AccountId, AccountId.ToString(), HoldId, new Money(amountCents), ValueDate);

    private static DomainEvent Decide(
        AccountPosition position, long amountCents, long availableBalanceCents,
        AuthorizationRules? rules = null, AccountFreeze? freeze = null) =>
        CurrentAccountAuthorizeDecider.Decide(
            position, Request(amountCents), availableBalanceCents, rules ?? new AuthorizationRules(), freeze);

    // --- authorized ---

    [Fact]
    public void Within_available_balance_places_the_hold_carrying_the_attempt()
    {
        var result = Decide(InState(AccountLifecycle.Active), amountCents: 3_000, availableBalanceCents: 10_000);

        var hold = Assert.IsType<HoldPlaced>(result);
        Assert.Equal(AccountId, hold.InstanceId);
        Assert.Equal(HoldId, hold.HoldId);
        Assert.Equal(AccountId.ToString(), hold.AccountRef);
        Assert.Equal(3_000, hold.Amount.Cents);
        Assert.Equal(ValueDate, hold.ValueDate);
    }

    [Fact]
    public void The_exact_available_balance_authorizes_the_boundary_is_inclusive()
    {
        var result = Decide(InState(AccountLifecycle.Active), amountCents: 10_000, availableBalanceCents: 10_000);

        Assert.IsType<HoldPlaced>(result);
    }

    [Fact]
    public void A_debit_into_the_arranged_overdraft_window_authorizes()
    {
        // Balance 0, overdraft 5_000, debit 4_000 → available − amount = −4_000 ≥ −5_000 → authorized.
        var result = Decide(
            InState(AccountLifecycle.Active), amountCents: 4_000, availableBalanceCents: 0,
            rules: new AuthorizationRules(OverdraftLimitCents: 5_000));

        Assert.IsType<HoldPlaced>(result);
    }

    // --- declined: funds / overdraft split (ADR-PC-037 §D5/§D6) ---

    [Fact]
    public void Insufficient_balance_with_no_overdraft_declines_INSUFFICIENT_AVAILABLE_BALANCE()
    {
        var result = Decide(InState(AccountLifecycle.Active), amountCents: 12_000, availableBalanceCents: 10_000);

        var declined = Assert.IsType<AuthorizationDeclined>(result);
        Assert.Equal(AccountDeclinedReason.InsufficientAvailableBalance, declined.DeclinedReason);
        Assert.Equal(12_000, declined.Amount.Cents);
        Assert.Equal(ValueDate, declined.ValueDate);
        Assert.Equal(AccountId, declined.AccountId);
    }

    [Fact]
    public void A_debit_beyond_the_arranged_overdraft_declines_OVERDRAFT_LIMIT_EXCEEDED()
    {
        // Balance 0, overdraft 5_000, debit 6_000 → available − amount = −6_000 < −5_000 → refused; and
        // because an overdraft WAS arranged, the family names it OVERDRAFT_LIMIT_EXCEEDED (ultrapassagem).
        var result = Decide(
            InState(AccountLifecycle.Active), amountCents: 6_000, availableBalanceCents: 0,
            rules: new AuthorizationRules(OverdraftLimitCents: 5_000));

        var declined = Assert.IsType<AuthorizationDeclined>(result);
        Assert.Equal(AccountDeclinedReason.OverdraftLimitExceeded, declined.DeclinedReason);
    }

    // --- declined: per-transaction limit ---

    [Fact]
    public void Exceeding_the_per_transaction_limit_declines_LIMIT_EXCEEDED()
    {
        var result = Decide(
            InState(AccountLifecycle.Active), amountCents: 3_000, availableBalanceCents: 1_000_000,
            rules: new AuthorizationRules(PerTransactionLimitCents: 2_500));

        var declined = Assert.IsType<AuthorizationDeclined>(result);
        Assert.Equal(AccountDeclinedReason.LimitExceeded, declined.DeclinedReason);
    }

    // --- declined: lifecycle gate (the spine never reads lifecycle) ---

    [Theory]
    [InlineData(AccountLifecycle.Pending)]
    [InlineData(AccountLifecycle.Dormant)]
    [InlineData(AccountLifecycle.Failed)]
    [InlineData(AccountLifecycle.Closed)]
    [InlineData(AccountLifecycle.Erased)]
    public void A_non_active_account_declines_ACCOUNT_NOT_ACTIVE_before_any_funds_check(AccountLifecycle lifecycle)
    {
        // Ample balance: the refusal is the lifecycle gate, not funds — proving the family gate runs first.
        var result = Decide(InState(lifecycle), amountCents: 100, availableBalanceCents: 1_000_000);

        var declined = Assert.IsType<AuthorizationDeclined>(result);
        Assert.Equal(AccountDeclinedReason.AccountNotActive, declined.DeclinedReason);
        Assert.Equal(lifecycle.ToString(), declined.Detail);
    }

    // --- declined: compliance freeze maps to ACCOUNT_NOT_ACTIVE and names the freeze ---

    [Fact]
    public void An_active_but_frozen_account_declines_ACCOUNT_NOT_ACTIVE_naming_the_freeze()
    {
        var freeze = new AccountFreeze(
            "frz-1", AccountId, "SUSPECTED_FRAUD", "ops:compliance", FreezeExpiresAt: null, FreezeState.Active);

        var result = Decide(
            InState(AccountLifecycle.Active), amountCents: 100, availableBalanceCents: 1_000_000, freeze: freeze);

        var declined = Assert.IsType<AuthorizationDeclined>(result);
        Assert.Equal(AccountDeclinedReason.AccountNotActive, declined.DeclinedReason);
        Assert.Equal("SUSPECTED_FRAUD", declined.Detail);
    }

    // --- determinism (ADR-PC-010 §P5): the same inputs yield an equal decision ---

    [Fact]
    public void The_decision_is_deterministic_for_equal_inputs()
    {
        var a = Decide(InState(AccountLifecycle.Active), amountCents: 7_000, availableBalanceCents: 5_000);
        var b = Decide(InState(AccountLifecycle.Active), amountCents: 7_000, availableBalanceCents: 5_000);

        Assert.Equal(a, b);
    }
}
