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
        // Crossing into APPROVED arms the FIRST irreversible command, ConfirmDebit (Document 05 step 4a).
        AssertTransition(SagaState.ValidationsComplete, ConstitutionProcess.ConstitutionApproved, SagaState.Approved, "ConfirmDebit");
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
        // The workflow-approved path arms the SAME first irreversible command as the auto path.
        AssertTransition(SagaState.AwaitWorkflowApproval, ConstitutionProcess.ConstitutionApproved,
            SagaState.Approved, "ConfirmDebit");

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

    [Fact]
    public void Indeterminate_debit_parks_in_AWAIT_CORE_CLEARANCE_and_resumes_or_fails_on_clearance()
    {
        // Document 05 Scenario C (bd babelstone-t7o3.10): a ConfirmDebit whose response never arrived
        // (the ACL reported INDETERMINATE) must NOT blind-retry — it parks in the first-class waiting
        // state AWAIT_CORE_CLEARANCE (ADR-IC-003 §P5), emitting the clearance QUERY command. The
        // clearance result then either RESUMES the happy path (the debit DID execute → DebitConfirmed,
        // late) or FAILS closed (the debit did NOT execute → DebitNotExecuted, no money moved).

        // ENTRY: an INDETERMINATE debit from APPROVED parks the saga and arms the clearance query.
        AssertTransition(SagaState.Approved, ConstitutionProcess.CoreDebitIndeterminate,
            SagaState.AwaitCoreClearance, "QueryCoreDebitStatus");

        // EXIT resume: the clearance found the debit DID execute → the LATE DebitConfirmed resumes the
        // happy path exactly as a timely one would (back to APPROVED, arming ActivateDeposit).
        AssertTransition(SagaState.AwaitCoreClearance, ConstitutionProcess.DebitConfirmed,
            SagaState.Approved, "ActivateDeposit");

        // EXIT fail: the clearance found the debit did NOT execute → fail-CLOSED terminal with NO
        // reversal (no money moved, so there is nothing to compensate).
        AssertTransition(SagaState.AwaitCoreClearance, ConstitutionProcess.DebitNotExecuted,
            SagaState.DepositConstitutionFailed);
        Assert.True(SagaStateNames.IsTerminal(SagaState.DepositConstitutionFailed));

        // EXIT escalate (§P6 robustness): a clearance that itself cannot resolve (CompensationFailed)
        // escalates to HUMAN_INTERVENTION_REQUIRED rather than stranding the saga.
        AssertTransition(SagaState.AwaitCoreClearance, ConstitutionProcess.CompensationFailed,
            SagaState.HumanInterventionRequired);

        // The DebitNotExecuted EXIT emits NO reversal command — the no-money-moved terminal genuinely
        // has nothing to compensate.
        Assert.True(_machine.TryAdvance(SagaState.AwaitCoreClearance, ConstitutionProcess.DebitNotExecuted, out var fail));
        Assert.Empty(fail.Commands);
    }

    [Fact]
    public void Indeterminate_debit_entry_is_reachable_only_from_APPROVED()
    {
        // §P5: CoreDebitIndeterminate is the irreversible-debit's THIRD outcome (alongside confirmed and
        // refused), so like DebitConfirmed it is only ever a legal advance out of APPROVED — never from
        // a pre-approval validation state. Proven directly from the table.
        foreach (var ((from, evt), _) in _machine.Transitions)
        {
            if (evt == ConstitutionProcess.CoreDebitIndeterminate)
            {
                Assert.Equal(SagaState.Approved, from);
            }
        }

        // And it is NOT a legal move out of a pre-approval validation state.
        Assert.False(_machine.TryAdvance(SagaState.ValidationsComplete, ConstitutionProcess.CoreDebitIndeterminate, out _));
        Assert.False(_machine.TryAdvance(SagaState.ParallelValidation, ConstitutionProcess.CoreDebitIndeterminate, out _));
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
        // §P5: every state that ACCEPTS DebitConfirmed must be at-or-past the APPROVED line — the
        // irreversible effect never lands before approval. APPROVED accepts a timely confirm;
        // AWAIT_CORE_CLEARANCE accepts a LATE confirm (Scenario C, bd babelstone-t7o3.10) — and that
        // state is itself only reachable FROM APPROVED (the indeterminate debit entry), so the §P5
        // ordering still holds: no DebitConfirmed source predates approval. Proven from the table.
        var postApprovalConfirmSources = new[] { SagaState.Approved, SagaState.AwaitCoreClearance };
        foreach (var ((from, evt), _) in _machine.Transitions)
        {
            if (evt == ConstitutionProcess.DebitConfirmed)
            {
                Assert.Contains(from, postApprovalConfirmSources);
            }
        }

        // AWAIT_CORE_CLEARANCE is genuinely a post-approval state — it is entered ONLY from APPROVED
        // (the indeterminate debit), so accepting a late DebitConfirmed there does not breach §P5.
        foreach (var ((from, evt), outcome) in _machine.Transitions)
        {
            if (outcome.Next == SagaState.AwaitCoreClearance)
            {
                Assert.Equal(SagaState.Approved, from);
                Assert.Equal(ConstitutionProcess.CoreDebitIndeterminate, evt);
            }
        }
    }

    [Fact]
    public void Irreversible_commands_ConfirmDebit_and_ActivateDeposit_are_emitted_only_from_APPROVED()
    {
        // §P5 reversibility ordering as a COMMAND-reachability fitness function: the two
        // irreversible commands (ConfirmDebit — convert the hold to a real debit; ActivateDeposit
        // — activate after the debit) may be EMITTED only by a transition that crosses INTO or
        // sits AT the irreversible phase. ConfirmDebit is armed exactly when the saga reaches
        // APPROVED (from VALIDATIONS_COMPLETE or AWAIT_WORKFLOW_APPROVAL); ActivateDeposit only from a
        // post-approval state (APPROVED, or AWAIT_CORE_CLEARANCE on a Scenario-C late-confirm resume —
        // bd babelstone-t7o3.10, itself reachable only from APPROVED). Neither is ever emitted from a
        // pre-approval validation state. Proven directly from the table — if a future edit emits either
        // earlier, this fails.
        var irreversibleApprovalEntries = new[] { SagaState.ValidationsComplete, SagaState.AwaitWorkflowApproval };
        var postApprovalActivateSources = new[] { SagaState.Approved, SagaState.AwaitCoreClearance };

        foreach (var ((from, _), outcome) in _machine.Transitions)
        {
            if (outcome.Commands.Contains(ConstitutionProcess.ConfirmDebit))
            {
                // ConfirmDebit is armed on the crossing INTO APPROVED — its source is an approval
                // entry, and its destination is APPROVED itself (the irreversible phase).
                Assert.Contains(from, irreversibleApprovalEntries);
                Assert.Equal(SagaState.Approved, outcome.Next);
            }

            if (outcome.Commands.Contains(ConstitutionProcess.ActivateDeposit))
            {
                // ActivateDeposit is emitted from a post-approval state: APPROVED (the normal step) or
                // AWAIT_CORE_CLEARANCE (the late-confirm resume), the latter only reachable from APPROVED.
                Assert.Contains(from, postApprovalActivateSources);
            }
        }

        // And the commands ARE actually reachable (the assertions above are not vacuous): the
        // approval crossing emits ConfirmDebit, and DebitConfirmed-from-APPROVED emits ActivateDeposit.
        Assert.True(_machine.TryAdvance(SagaState.ValidationsComplete, ConstitutionProcess.ConstitutionApproved, out var approve));
        Assert.Contains(ConstitutionProcess.ConfirmDebit, approve.Commands);
        Assert.True(_machine.TryAdvance(SagaState.Approved, ConstitutionProcess.DebitConfirmed, out var debit));
        Assert.Contains(ConstitutionProcess.ActivateDeposit, debit.Commands);
    }

    [Fact]
    public void Precondition_refusal_is_a_terminal_failure_reachable_only_before_approval_with_no_reversal()
    {
        // H.2: a PreconditionRefused event lands the saga in the terminal DEPOSIT_CONSTITUTION_FAILED
        // state, emitting NO reversal command — nothing reversible was committed at a pre-approval
        // validation state, so there is nothing to compensate (ADR-PC-024 §5;
        // the edge precondition "the orchestrator never starts", lifted in-saga). A fitness
        // function over the table: every PreconditionRefused transition (a) starts from a
        // PRE-APPROVAL validation state, (b) targets the terminal DEPOSIT_CONSTITUTION_FAILED, and
        // (c) emits NO command (no reversal, no anything).
        var preApprovalValidationStates = new[]
        {
            SagaState.ParallelValidation,
            SagaState.AwaitLimitsValidated,
            SagaState.AwaitBalanceReserved,
            SagaState.ValidationsComplete,
        };

        var refusalSources = new List<SagaState>();
        foreach (var ((from, evt), outcome) in _machine.Transitions)
        {
            if (evt != ConstitutionProcess.PreconditionRefused)
            {
                continue;
            }

            refusalSources.Add(from);
            // (a) only from a pre-approval validation state — never from APPROVED or later.
            Assert.Contains(from, preApprovalValidationStates);
            // (b) terminal failure destination.
            Assert.Equal(SagaState.DepositConstitutionFailed, outcome.Next);
            // (c) NO reversal — and indeed no command at all.
            Assert.Empty(outcome.Commands);
        }

        // The refusal is honoured from EVERY pre-approval validation state (no hole that would
        // strand a refusal mid-validation).
        Assert.Equal(
            preApprovalValidationStates.OrderBy(s => s).ToArray(),
            refusalSources.OrderBy(s => s).ToArray());

        // DEPOSIT_CONSTITUTION_FAILED is terminal — no event advances it, and it emits no reversal
        // (it never CompensatesValidations or CompensatesPostDebit).
        Assert.True(SagaStateNames.IsTerminal(SagaState.DepositConstitutionFailed));
        Assert.False(_machine.TryAdvance(SagaState.DepositConstitutionFailed, ConstitutionProcess.ReservationReleased, out _));

        // A precondition refusal is NOT reachable once APPROVED — past the irreversible line a
        // failure is a COMPENSATION (ActivationFailed → ReverseCoreDebit), never this no-op terminal.
        Assert.False(_machine.TryAdvance(SagaState.Approved, ConstitutionProcess.PreconditionRefused, out _));
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
