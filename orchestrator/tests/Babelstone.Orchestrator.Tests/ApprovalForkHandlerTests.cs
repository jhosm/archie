using Babelstone.Orchestrator.Handlers;
using Babelstone.Orchestrator.Saga;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// Pure tests of the <see cref="ApprovalForkHandler"/> decider (Document 05 step 3 "auto-approval
/// vs external workflow"). No clock, no I/O, no live config: the threshold is SUPPLIED as an
/// argument on the edge-pinned <see cref="ApprovalDecisionInput"/> and the decision is a total
/// function of (state, input). These lock in the fork rule and the must-nots (no live-config
/// dereference, no minted value) as a fitness function.
/// </summary>
public sealed class ApprovalForkHandlerTests
{
    // The Document 05 worked threshold (€25,000) expressed in integer minor units (cents). It is
    // supplied to Decide as an ARGUMENT on the input — never read from a rate sheet here.
    private const long ThresholdCents = 25_000_00;

    [Theory]
    // Existing client, at or below the pinned threshold → auto-approve.
    [InlineData(10_000_00, ThresholdCents, ClientType.Existing, ApprovalDecision.AutoApprove)]
    [InlineData(0, ThresholdCents, ClientType.Existing, ApprovalDecision.AutoApprove)]
    // Boundary: EXACTLY at the threshold is auto-approve (the rule is ">€25,000 → workflow").
    [InlineData(ThresholdCents, ThresholdCents, ClientType.Existing, ApprovalDecision.AutoApprove)]
    // One cent over the threshold → workflow.
    [InlineData(ThresholdCents + 1, ThresholdCents, ClientType.Existing, ApprovalDecision.WorkflowApprovalRequired)]
    [InlineData(50_000_00, ThresholdCents, ClientType.Existing, ApprovalDecision.WorkflowApprovalRequired)]
    // New client ALWAYS routes to the workflow regardless of amount — even a tiny one.
    [InlineData(1_00, ThresholdCents, ClientType.New, ApprovalDecision.WorkflowApprovalRequired)]
    [InlineData(10_000_00, ThresholdCents, ClientType.New, ApprovalDecision.WorkflowApprovalRequired)]
    [InlineData(ThresholdCents, ThresholdCents, ClientType.New, ApprovalDecision.WorkflowApprovalRequired)]
    public void Decide_forks_on_amount_vs_pinned_threshold_and_client_type(
        long amountCents, long thresholdCents, ClientType clientType, ApprovalDecision expected)
    {
        var input = new ApprovalDecisionInput(amountCents, thresholdCents, clientType);

        // The fork is decided at VALIDATIONS_COMPLETE — after the reversible validations, before
        // any irreversible effect (Document 05 step 2c → 3).
        var decision = ApprovalForkHandler.Decide(SagaState.ValidationsComplete, input);

        Assert.Equal(expected, decision);
    }

    [Fact]
    public void The_threshold_is_an_argument_not_a_live_config_dereference()
    {
        // The SAME amount + client type decides DIFFERENTLY purely on the threshold ARGUMENT —
        // proving the threshold rides the input, not a mutable rate sheet the handler reaches
        // into. A €30,000 amount auto-approves under a €40,000 pinned threshold and routes under
        // a €25,000 one, with nothing else changed.
        const long amount = 30_000_00;

        Assert.Equal(
            ApprovalDecision.AutoApprove,
            ApprovalForkHandler.Decide(
                SagaState.ValidationsComplete,
                new ApprovalDecisionInput(amount, 40_000_00, ClientType.Existing)));

        Assert.Equal(
            ApprovalDecision.WorkflowApprovalRequired,
            ApprovalForkHandler.Decide(
                SagaState.ValidationsComplete,
                new ApprovalDecisionInput(amount, 25_000_00, ClientType.Existing)));
    }

    [Fact]
    public void Decide_is_deterministic_for_a_given_input()
    {
        // Replay determinism (ADR-PC-010 §P5): the SAME (state, input) returns the SAME decision
        // every call — no clock, no randomness could make two evaluations diverge.
        var input = new ApprovalDecisionInput(25_000_01, ThresholdCents, ClientType.Existing);

        var first = ApprovalForkHandler.Decide(SagaState.ValidationsComplete, input);
        var second = ApprovalForkHandler.Decide(SagaState.ValidationsComplete, input);
        var third = ApprovalForkHandler.Decide(SagaState.ValidationsComplete, input);

        Assert.Equal(ApprovalDecision.WorkflowApprovalRequired, first);
        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }

    [Fact]
    public void Auto_approve_emits_ConstitutionApproved_and_workflow_emits_WorkflowApprovalRequired()
    {
        // The fork chooses the DISTINCT next driver event (per the ConstitutionProcess §P2 fork
        // note): auto-approve self-emits ConstitutionApproved; route-to-workflow self-emits
        // WorkflowApprovalRequired, which arms the AWAIT_WORKFLOW_APPROVAL wait.
        Assert.Equal(
            ConstitutionProcess.ConstitutionApproved,
            ApprovalForkHandler.NextEventType(ApprovalDecision.AutoApprove));
        Assert.Equal(
            ConstitutionProcess.WorkflowApprovalRequired,
            ApprovalForkHandler.NextEventType(ApprovalDecision.WorkflowApprovalRequired));
    }

    [Fact]
    public void The_emitted_event_drives_a_legal_transition_out_of_VALIDATIONS_COMPLETE()
    {
        // The fork's chosen event must be one the ConstitutionProcess table actually accepts out
        // of VALIDATIONS_COMPLETE — the decider and the state machine agree by construction.
        var machine = new ConstitutionProcess();

        var autoEvent = ApprovalForkHandler.NextEventType(ApprovalDecision.AutoApprove);
        Assert.True(machine.TryAdvance(SagaState.ValidationsComplete, autoEvent, out var autoOutcome));
        Assert.Equal(SagaState.Approved, autoOutcome.Next);

        var workflowEvent = ApprovalForkHandler.NextEventType(ApprovalDecision.WorkflowApprovalRequired);
        Assert.True(machine.TryAdvance(SagaState.ValidationsComplete, workflowEvent, out var workflowOutcome));
        Assert.Equal(SagaState.AwaitWorkflowApproval, workflowOutcome.Next);
    }

    [Fact]
    public void Decide_refuses_to_evaluate_the_fork_before_validations_complete()
    {
        // §P5: the fork cannot be decided before the reversible validations finish — the input's
        // legitimacy is bound to VALIDATIONS_COMPLETE. A fork decided in PARALLEL_VALIDATION or
        // APPROVED is a programming error, raised loud, not silently answered.
        var input = new ApprovalDecisionInput(10_000_00, ThresholdCents, ClientType.Existing);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ApprovalForkHandler.Decide(SagaState.ParallelValidation, input));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ApprovalForkHandler.Decide(SagaState.Approved, input));
    }

    [Theory]
    [InlineData(-1, ThresholdCents)]
    [InlineData(10_000_00, -1)]
    public void Decide_rejects_a_structurally_impossible_negative_policy(long amountCents, long thresholdCents)
    {
        var input = new ApprovalDecisionInput(amountCents, thresholdCents, ClientType.Existing);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ApprovalForkHandler.Decide(SagaState.ValidationsComplete, input));
    }
}
