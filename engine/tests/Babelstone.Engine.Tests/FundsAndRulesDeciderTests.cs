using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// Tests for <see cref="FundsAndRulesDecider"/> — the engine-owned stages 3–5 of real-time
/// authorization (ADR-PC-030 / ADR-PC-033). In plain English: given what is spendable
/// now and the pack's limit rules, the decider either refuses the debit (declined, nothing
/// earmarked) or produces the <c>HoldPlaced</c> fact that earmarks the money. The suite pins the
/// gate order, the *descoberto autorizado* overdraft window, and the no-locking concurrency story:
/// an earlier approval's hold lowers the available-balance input, so the next decision sees it.
/// </summary>
public sealed class FundsAndRulesDeciderTests
{
    private static AuthorizationRequest Request(long cents, string holdId = "hold-1") => new(
        InstanceId: Guid.NewGuid(),
        AccountRef: "acct-1",
        HoldId: holdId,
        Amount: new Money(cents),
        ValueDate: new DateOnly(2026, 6, 25));

    [Fact]
    public void Authorizes_within_available_balance_and_earmarks_via_HoldPlaced()
    {
        var decision = FundsAndRulesDecider.Decide(Request(4_000), availableBalanceCents: 10_000, new AuthorizationRules());

        var authorized = Assert.IsType<AuthorizationDecision.Authorized>(decision);
        Assert.Equal("hold-1", authorized.Hold.HoldId);
        Assert.Equal("acct-1", authorized.Hold.AccountRef);
        Assert.Equal(4_000, authorized.Hold.Amount.Cents);
    }

    [Fact]
    public void Declines_a_debit_exceeding_the_available_balance_with_no_overdraft()
    {
        var decision = FundsAndRulesDecider.Decide(Request(10_001), availableBalanceCents: 10_000, new AuthorizationRules());

        var declined = Assert.IsType<AuthorizationDecision.Declined>(decision);
        Assert.Equal(AuthorizationDeclineReason.InsufficientAvailableBalance, declined.Reason);
    }

    [Fact]
    public void Authorizes_into_the_authorized_overdraft_window()
    {
        // Stage 4 (ADR-PC-030): *descoberto autorizado* is a pack rule — the available balance may
        // go down to −overdraft, but no further.
        var rules = new AuthorizationRules(OverdraftLimitCents: 5_000);

        var atTheEdge = FundsAndRulesDecider.Decide(Request(15_000), 10_000, rules);
        var pastTheEdge = FundsAndRulesDecider.Decide(Request(15_001), 10_000, rules);

        Assert.IsType<AuthorizationDecision.Authorized>(atTheEdge);
        var declined = Assert.IsType<AuthorizationDecision.Declined>(pastTheEdge);
        Assert.Equal(AuthorizationDeclineReason.InsufficientAvailableBalance, declined.Reason);
    }

    [Fact]
    public void Declines_over_the_per_transaction_limit_even_with_ample_funds()
    {
        var rules = new AuthorizationRules(PerTransactionLimitCents: 2_500);

        var decision = FundsAndRulesDecider.Decide(Request(2_501), availableBalanceCents: 1_000_000, rules);

        var declined = Assert.IsType<AuthorizationDecision.Declined>(decision);
        Assert.Equal(AuthorizationDeclineReason.PerTransactionLimitExceeded, declined.Reason);
    }

    private static AccountFreeze Freeze(
        string reason = "SANCTIONS_MATCH", string actor = "compliance-svc") =>
        new("freeze-1", Guid.NewGuid(), reason, actor, FreezeExpiresAt: null, FreezeState.Active);

    // FREEZE_GATES_AUTHORIZATION (ADR-PC-041 §Decision slot 5): while an instance is frozen the
    // decider refuses EVERY debit — even one the funds and limits would otherwise approve — and the
    // decline NAMES the freeze reason/actor so "why was this refused?" is a read.
    [Fact]
    public void A_freeze_refuses_a_debit_that_funds_and_limits_would_otherwise_authorize()
    {
        var decision = FundsAndRulesDecider.Decide(
            Request(4_000), availableBalanceCents: 1_000_000, new AuthorizationRules(),
            activeFreeze: Freeze(reason: "AML_SCREENING", actor: "aml-team"));

        var declined = Assert.IsType<AuthorizationDecision.Declined>(decision);
        Assert.Equal(AuthorizationDeclineReason.AccountFrozen, declined.Reason);
        // HOLD_REASON_OBSERVABLE: the decline carries the freeze's reason and actor.
        Assert.Equal("AML_SCREENING", declined.FreezeReason);
        Assert.Equal("aml-team", declined.ComplianceActor);
    }

    [Fact]
    public void The_freeze_gate_precedes_the_funds_and_limit_gates()
    {
        // A debit that ALSO exceeds the balance and the per-transaction limit still declines with
        // AccountFrozen, not InsufficientAvailableBalance/PerTransactionLimitExceeded — the freeze is
        // evaluated first (ADR-PC-041 slot 5).
        var decision = FundsAndRulesDecider.Decide(
            Request(9_999_999), availableBalanceCents: 10, new AuthorizationRules(PerTransactionLimitCents: 100),
            activeFreeze: Freeze());

        var declined = Assert.IsType<AuthorizationDecision.Declined>(decision);
        Assert.Equal(AuthorizationDeclineReason.AccountFrozen, declined.Reason);
    }

    [Fact]
    public void An_unfrozen_instance_authorizes_normally_and_the_decline_carries_no_freeze_detail()
    {
        // No freeze (null) — the gate is transparent; a normal decline names no freeze.
        var authorized = FundsAndRulesDecider.Decide(
            Request(4_000), availableBalanceCents: 10_000, new AuthorizationRules(), activeFreeze: null);
        Assert.IsType<AuthorizationDecision.Authorized>(authorized);

        var declined = Assert.IsType<AuthorizationDecision.Declined>(
            FundsAndRulesDecider.Decide(Request(20_000), 10_000, new AuthorizationRules(), activeFreeze: null));
        Assert.Equal(AuthorizationDeclineReason.InsufficientAvailableBalance, declined.Reason);
        Assert.Null(declined.FreezeReason);
        Assert.Null(declined.ComplianceActor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Declines_a_non_positive_amount(long cents)
    {
        var decision = FundsAndRulesDecider.Decide(Request(cents), availableBalanceCents: 10_000, new AuthorizationRules());

        var declined = Assert.IsType<AuthorizationDecision.Declined>(decision);
        Assert.Equal(AuthorizationDeclineReason.NonPositiveAmount, declined.Reason);
    }

    [Fact]
    public void An_earlier_approvals_hold_lowers_the_next_decisions_available_input()
    {
        // The no-locking double-spend story (ADR-PC-030 / ADR-PC-033): once the first debit's
        // HoldPlaced is drained into the read model, the available-balance fold the second
        // authorization reads is already lower. Modelled here by feeding the second decision the
        // post-hold available balance — what AccountBalanceReader returns once the first hold is
        // folded (the command shell drains before it decides).
        const long opening = 10_000;

        var first = FundsAndRulesDecider.Decide(Request(8_000, "hold-1"), opening, new AuthorizationRules());
        var firstHold = Assert.IsType<AuthorizationDecision.Authorized>(first).Hold;

        var availableAfterFirst = opening - firstHold.Amount.Cents;
        var second = FundsAndRulesDecider.Decide(Request(8_000, "hold-2"), availableAfterFirst, new AuthorizationRules());

        var declined = Assert.IsType<AuthorizationDecision.Declined>(second);
        Assert.Equal(AuthorizationDeclineReason.InsufficientAvailableBalance, declined.Reason);
    }

    [Fact]
    public void The_decision_is_deterministic_for_identical_inputs()
    {
        // Pure decider (ADR-PC-010): no clock, no randomness — the same inputs always produce the
        // same decision and the same HoldPlaced fact.
        var request = Request(4_000);

        var first = FundsAndRulesDecider.Decide(request, 10_000, new AuthorizationRules());
        var second = FundsAndRulesDecider.Decide(request, 10_000, new AuthorizationRules());

        Assert.Equal(
            Assert.IsType<AuthorizationDecision.Authorized>(first).Hold,
            Assert.IsType<AuthorizationDecision.Authorized>(second).Hold);
    }
}
