using Babelstone.Families.TermDeposit.Orchestration;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// Pure tests of the indeterminate-clearance reissue budget (bd babelstone-rq3e) — the v1 liveness
/// backstop on the Scenario-C RETRY_PERMITTED loop. The decider maps the saga's clearance-cycle count
/// (the number of AWAIT_CORE_CLEARANCE entries in the transition log) to reissue-vs-escalate; these
/// pin the budget arithmetic and its boundary so an off-by-one can never silently widen or narrow the
/// loop. No clock, no I/O, no DB — the decision is a total function of the count (ADR-PC-010 §P5).
/// </summary>
public sealed class ClearanceReissueBudgetTests
{
    [Fact]
    public void The_original_indeterminate_debit_reissues()
    {
        // The first AWAIT_CORE_CLEARANCE entry is the ORIGINAL indeterminate debit, not a reissue —
        // a not-executed clearance here is the FIRST reissue, always within budget.
        Assert.Equal(ClearanceReissueDecision.Reissue, ClearanceReissueBudget.Decide(1));
    }

    [Theory]
    [InlineData(1)] // original debit            → reissue #1
    [InlineData(2)] // 1 reissue already done    → reissue #2
    [InlineData(3)] // 2 reissues already done   → reissue #3
    public void Reissues_while_under_budget(int priorClearanceEntries)
    {
        // entries - 1 reissues have been attempted; while that is below MaxReissues (3), the saga
        // reissues. With MaxReissues = 3 the saga reissues on entries 1, 2 and 3.
        Assert.Equal(ClearanceReissueDecision.Reissue, ClearanceReissueBudget.Decide(priorClearanceEntries));
    }

    [Fact]
    public void Escalates_once_the_budget_is_spent()
    {
        // The 4th entry means 3 reissues have already been attempted (the budget) — the next
        // not-executed clearance escalates to HUMAN_INTERVENTION_REQUIRED instead of reissuing again.
        Assert.Equal(ClearanceReissueDecision.Escalate, ClearanceReissueBudget.Decide(ClearanceReissueBudget.MaxReissues + 1));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(100)]
    public void Stays_escalated_for_any_count_past_the_budget(int priorClearanceEntries)
    {
        // The decision is monotone: once the budget is spent it never reverts to reissue, however many
        // more times a misbehaving Core answers not-executed.
        Assert.Equal(ClearanceReissueDecision.Escalate, ClearanceReissueBudget.Decide(priorClearanceEntries));
    }

    [Fact]
    public void The_budget_boundary_is_exactly_MaxReissues_reissues_then_escalate()
    {
        // Pin the exact boundary as a fitness function: the last reissuing count is MaxReissues, the
        // first escalating count is MaxReissues + 1. A future edit that shifts the budget by one trips here.
        Assert.Equal(ClearanceReissueDecision.Reissue, ClearanceReissueBudget.Decide(ClearanceReissueBudget.MaxReissues));
        Assert.Equal(ClearanceReissueDecision.Escalate, ClearanceReissueBudget.Decide(ClearanceReissueBudget.MaxReissues + 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_clearance_with_no_recorded_park_is_structurally_impossible(int priorClearanceEntries)
    {
        // A not-executed clearance can only arrive while the saga is parked in AWAIT_CORE_CLEARANCE, so
        // the recorded count is always at least 1. A count below 1 is a corruption, not a tolerated value.
        Assert.Throws<ArgumentOutOfRangeException>(() => ClearanceReissueBudget.Decide(priorClearanceEntries));
    }

    [Fact]
    public void The_decision_is_pure_same_count_same_answer()
    {
        // No clock, no randomness could make two evaluations of the same count diverge (ADR-PC-010 §P5).
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(ClearanceReissueBudget.Decide(2), ClearanceReissueBudget.Decide(2));
            Assert.Equal(ClearanceReissueBudget.Decide(ClearanceReissueBudget.MaxReissues + 1),
                ClearanceReissueBudget.Decide(ClearanceReissueBudget.MaxReissues + 1));
        }
    }
}
