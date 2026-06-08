using Babelstone.Orchestrator.Saga;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// Pure tests of the <see cref="ConstitutionProcess"/> state machine — the ADR-IC-003 §P2
/// "the state machine is the specification" fitness function. No clock, no I/O, no DB: the
/// transition table is a pure data structure and these assert its shape directly (the table
/// IS the documentation; these prove the documentation).
/// </summary>
public sealed class ConstitutionProcessTests
{
    private readonly ConstitutionProcess _machine = new();

    [Fact]
    public void Starts_in_STARTED()
    {
        Assert.Equal(SagaState.Started, _machine.InitialState);
        Assert.Equal(ConstitutionProcess.Type, _machine.SagaType);
    }

    [Fact]
    public void Happy_path_drives_STARTED_to_COMPLETED()
    {
        // The exact Document 05 happy-path walk, asserted as a chain through the table.
        AssertTransition(SagaState.Started, ConstitutionProcess.ConstitutionRequested, SagaState.ParallelValidation,
            "ReserveAccountBalance", "ValidateProductLimits");
        // Balance-first ordering: through AWAIT_LIMITS_VALIDATED, then the join completes.
        AssertTransition(SagaState.ParallelValidation, ConstitutionProcess.BalanceReserved, SagaState.AwaitLimitsValidated);
        AssertTransition(SagaState.AwaitLimitsValidated, ConstitutionProcess.LimitsValidated, SagaState.ValidationsComplete);
        AssertTransition(SagaState.ValidationsComplete, ConstitutionProcess.ConstitutionApproved, SagaState.Approved);
        AssertTransition(SagaState.Approved, ConstitutionProcess.DebitConfirmed, SagaState.Approved, "ActivateDeposit");
        AssertTransition(SagaState.Approved, ConstitutionProcess.ProcessConstituted, SagaState.Completed);
    }

    [Fact]
    public void Parallel_validation_join_is_order_independent()
    {
        // Document 05 §2c: the saga completes "when the two arrive" — with NO delivery-ordering
        // guarantee between BalanceReserved (async ~120ms Core round-trip) and LimitsValidated
        // (synchronous in-aggregate calc). BOTH orderings must reach the SAME VALIDATIONS_COMPLETE
        // join; neither may poison. This locks the order-independence in as a fitness function.

        // Order 1: balance first, then limits.
        AssertTransition(SagaState.ParallelValidation, ConstitutionProcess.BalanceReserved, SagaState.AwaitLimitsValidated);
        AssertTransition(SagaState.AwaitLimitsValidated, ConstitutionProcess.LimitsValidated, SagaState.ValidationsComplete);

        // Order 2 (the COMMON one): limits first, then balance — same destination.
        AssertTransition(SagaState.ParallelValidation, ConstitutionProcess.LimitsValidated, SagaState.AwaitBalanceReserved);
        AssertTransition(SagaState.AwaitBalanceReserved, ConstitutionProcess.BalanceReserved, SagaState.ValidationsComplete);

        // Symmetry of the table itself: every state that accepts one leg's arrival has a
        // transition for the OTHER leg too — there is no (state, sibling-event) hole that would
        // poison the reverse delivery order.
        Assert.True(_machine.TryAdvance(SagaState.AwaitLimitsValidated, ConstitutionProcess.LimitsValidated, out _));
        Assert.True(_machine.TryAdvance(SagaState.AwaitBalanceReserved, ConstitutionProcess.BalanceReserved, out _));
    }

    [Fact]
    public void Workflow_approval_fork_is_reachable_on_a_distinct_event()
    {
        // The AWAIT_WORKFLOW_APPROVAL fork (Document 05 "Important Variation") must be armed by a
        // DISTINCT event, not the start event ConstitutionRequested — the advance handler
        // intercepts the start event and routes it to StartAsync before the table lookup, so a
        // start-event-keyed fork row would be unreachable. Keying it on WorkflowApprovalRequired
        // makes it reachable, and the wait resumes on approval (§P2 auditability, §P4 waiting state).
        AssertTransition(SagaState.ValidationsComplete, ConstitutionProcess.WorkflowApprovalRequired,
            SagaState.AwaitWorkflowApproval);
        AssertTransition(SagaState.AwaitWorkflowApproval, ConstitutionProcess.ConstitutionApproved,
            SagaState.Approved);

        // The start event is NOT a legal advance out of VALIDATIONS_COMPLETE — the old
        // unreachable placeholder row is gone.
        Assert.False(_machine.TryAdvance(SagaState.ValidationsComplete, ConstitutionProcess.ConstitutionRequested, out _));
    }

    [Fact]
    public void COMPLETED_is_terminal()
    {
        Assert.True(SagaStateNames.IsTerminal(SagaState.Completed));
        // No event advances a COMPLETED saga (terminal accepts nothing).
        Assert.False(_machine.TryAdvance(SagaState.Completed, ConstitutionProcess.ProcessConstituted, out _));
    }

    [Fact]
    public void Early_compensation_releases_the_hold_and_cancels()
    {
        // Document 05 Scenario A: a product-limit rejection compensates the reversible hold.
        AssertTransition(SagaState.ParallelValidation, ConstitutionProcess.LimitsRejected,
            SagaState.CompensateValidations, "ReleaseBalanceReservation");
        AssertTransition(SagaState.CompensateValidations, ConstitutionProcess.ReservationReleased, SagaState.Cancelled);
        Assert.True(SagaStateNames.IsTerminal(SagaState.Cancelled));
    }

    [Fact]
    public void Late_compensation_reverses_the_debit_and_cancels_after_debit()
    {
        // Document 05 Scenario B: an activation failure AFTER the debit reverses it via a
        // domain command (ADR-IC-003 §P6), not a rollback, and lands in a DISTINCT terminal
        // state because money moved and was returned.
        AssertTransition(SagaState.Approved, ConstitutionProcess.ActivationFailed,
            SagaState.CompensatePostDebit, "ReverseCoreDebit");
        AssertTransition(SagaState.CompensatePostDebit, ConstitutionProcess.DebitReversed, SagaState.CancelledAfterDebit);
        Assert.True(SagaStateNames.IsTerminal(SagaState.CancelledAfterDebit));
    }

    [Theory]
    [InlineData("COMPENSATE_VALIDATIONS")]
    [InlineData("COMPENSATE_POST_DEBIT")]
    public void A_failed_compensation_escalates_to_HUMAN_INTERVENTION_REQUIRED(string fromName)
    {
        // ADR-IC-003 §P6: "A compensation that fails must produce an INDETERMINATE or
        // HUMAN_INTERVENTION_REQUIRED state, not a swallowed exception." Both compensation
        // states have that escape, and the escalation target is NOT terminal — an operator
        // must still resolve it.
        var from = SagaStateNames.FromName(fromName);
        AssertTransition(from, ConstitutionProcess.CompensationFailed, SagaState.HumanInterventionRequired);
        Assert.False(SagaStateNames.IsTerminal(SagaState.HumanInterventionRequired));
    }

    [Fact]
    public void Illegal_transitions_are_rejected_not_silently_ignored()
    {
        // ADR-IC-003 §P2: "Any transition that is not in the table is rejected with an error,
        // not silently ignored." A confirmed debit out of STARTED (before any approval) is
        // not in the table — the machine refuses it.
        Assert.False(_machine.TryAdvance(SagaState.Started, ConstitutionProcess.DebitConfirmed, out _));
        // A wholly unknown event type is equally rejected.
        Assert.False(_machine.TryAdvance(SagaState.Started, "NoSuchEvent", out _));
        // The irreversible debit is reachable ONLY from APPROVED (§P5 reversibility ordering):
        // it is not a legal move out of VALIDATIONS_COMPLETE.
        Assert.False(_machine.TryAdvance(SagaState.ValidationsComplete, ConstitutionProcess.DebitConfirmed, out _));
    }

    [Fact]
    public void Irreversible_debit_only_follows_approval()
    {
        // §P5: every state that ACCEPTS DebitConfirmed must itself be APPROVED — the
        // irreversible effect never lands before approval. Proven directly from the table.
        foreach (var ((from, evt), _) in _machine.Transitions)
        {
            if (evt == ConstitutionProcess.DebitConfirmed)
            {
                Assert.Equal(SagaState.Approved, from);
            }
        }
    }

    [Fact]
    public void Every_state_name_round_trips()
    {
        // The persisted column form is decoupled from the enum order; assert the bijection so
        // a reorder never silently rewrites history.
        foreach (var state in Enum.GetValues<SagaState>())
        {
            Assert.Equal(state, SagaStateNames.FromName(SagaStateNames.ToName(state)));
        }
    }

    private void AssertTransition(SagaState from, string evt, SagaState expectedNext, params string[] expectedCommands)
    {
        Assert.True(_machine.TryAdvance(from, evt, out var outcome),
            $"({from}, '{evt}') must be a legal transition.");
        Assert.Equal(expectedNext, outcome.Next);
        Assert.Equal(expectedCommands, outcome.Commands);
    }
}
