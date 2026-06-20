using Babelstone.Orchestrator.Saga;

namespace Babelstone.Families.TermDeposit.Orchestration;

/// <summary>
/// The <see cref="ConstitutionProcess"/> family's projection from its verbatim saga state to the coarse
/// agent-facing <see cref="AgentStatus"/> (bd babelstone-vjoi / Document 11 Pattern 2). The family owns
/// what each of its states MEANS (ADR-IC-018 §D3), so the mapping lives here, next to the state vocabulary
/// it projects — never as an edge-side switch over state names. The edge's <c>get_process_status</c>
/// endpoint resolves this by <c>saga_type</c> and pairs it with the machine's <c>IsTerminal</c> answer.
/// </summary>
/// <remarks>
/// The map is TOTAL over <see cref="ConstitutionProcess.States"/> by construction: every state constant has
/// an arm, and an unmapped state throws (caught by the per-state lock test and surfaced as a fail-closed
/// 500 at the edge) — the same discipline that keeps <see cref="ConstitutionProcess.States.IsTerminal"/> in
/// lockstep with the transition table. The in-flight waits and automatic compensation paths all read as
/// <see cref="AgentStatus.Processing"/> (the system is still working it, no caller action); only the
/// external approval wait is <see cref="AgentStatus.AwaitingApproval"/> and only the operator escalation is
/// <see cref="AgentStatus.ActionRequired"/>.
/// </remarks>
public sealed class ConstitutionProcessAgentStatusMap : ISagaAgentStatusMap
{
    /// <inheritdoc />
    public string SagaType => ConstitutionProcess.Type;

    /// <inheritdoc />
    public string StatusFor(string state) => state switch
    {
        // In flight — the system is still working the process; no caller action, poll again. Covers the
        // happy-path advance (started → validations → approved) AND the automatic compensation/clearance
        // waits, which from the agent's view are "still processing", not a distinct agent affordance.
        ConstitutionProcess.States.Started
            or ConstitutionProcess.States.ParallelValidation
            or ConstitutionProcess.States.AwaitLimitsValidated
            or ConstitutionProcess.States.AwaitBalanceReserved
            or ConstitutionProcess.States.ValidationsComplete
            or ConstitutionProcess.States.Approved
            or ConstitutionProcess.States.AwaitCoreClearance
            or ConstitutionProcess.States.CompensateValidations
            or ConstitutionProcess.States.CompensatePostDebit => AgentStatus.Processing,

        // Paused on the external approval workflow — a first-class wait (ADR-IC-003 §P4), not a stuck
        // thread. The one non-terminal state the agent surfaces as "awaiting approval".
        ConstitutionProcess.States.AwaitWorkflowApproval => AgentStatus.AwaitingApproval,

        // A bank OPERATOR must reconcile manually (non-terminal). The agent surfaces it; it cannot clear it.
        ConstitutionProcess.States.HumanInterventionRequired => AgentStatus.ActionRequired,

        // Terminal dispositions.
        ConstitutionProcess.States.Completed => AgentStatus.Completed,
        ConstitutionProcess.States.DepositConstitutionFailed => AgentStatus.Failed,
        // CANCELLED and CANCELLED_AFTER_DEBIT both read as cancelled to the agent — the with/without-
        // reversal distinction the operator state set keeps is not an agent-facing affordance.
        ConstitutionProcess.States.Cancelled
            or ConstitutionProcess.States.CancelledAfterDebit => AgentStatus.Cancelled,

        _ => throw new ArgumentOutOfRangeException(
            nameof(state), state,
            $"No agent-status mapping for ConstitutionProcess state '{state}'. A state added to "
            + "ConstitutionProcess.States must gain an arm here (ADR-IC-018 §D3 — the family owns its "
            + "state meaning); the per-state lock test guards this."),
    };
}
