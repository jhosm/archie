using Babelstone.Orchestrator.Saga;

namespace Babelstone.Families.TermDeposit.Orchestration;

/// <summary>
/// The <see cref="RenewalProcess"/> family's projection from its verbatim saga state to the coarse
/// agent-facing <see cref="AgentStatus"/> (bd babelstone-vjoi / Document 11 Pattern 2) — the renewal
/// sibling of <see cref="ConstitutionProcessAgentStatusMap"/>. The family owns what each of its states
/// MEANS (ADR-IC-018 §D3); the edge resolves this by <c>saga_type</c>.
/// </summary>
/// <remarks>
/// The renewal saga runs AFTER the closing deposit matured, so it has NO compensation/cancellation states:
/// money already moved at maturity and failures escalate rather than reverse (ADR-IC-003 §P6). Its forward
/// legs (RENEWAL_STARTED/CONSTITUTING/LINKING) are <see cref="AgentStatus.Processing"/>; the operator
/// escalation is <see cref="AgentStatus.ActionRequired"/>; RENEWAL_COMPLETED is the lone terminal success.
/// Total over <see cref="RenewalProcess.States"/> by construction — an unmapped state throws (the per-state
/// lock test guards it).
/// </remarks>
public sealed class RenewalProcessAgentStatusMap : ISagaAgentStatusMap
{
    /// <inheritdoc />
    public string SagaType => RenewalProcess.Type;

    /// <inheritdoc />
    public string StatusFor(string state) => state switch
    {
        // In flight — the two idempotent engine legs (constitute-renewal, renewal-link) are still working.
        RenewalProcess.States.RenewalStarted
            or RenewalProcess.States.RenewalConstituting
            or RenewalProcess.States.RenewalLinking => AgentStatus.Processing,

        // A bank OPERATOR must reconcile a refused leg manually (non-terminal) — never a compensation,
        // because the maturity payout already moved (ADR-IC-003 §P6).
        RenewalProcess.States.HumanInterventionRequired => AgentStatus.ActionRequired,

        // The lone terminal success: the new stream is open and the closing stream is linked (Renewed).
        RenewalProcess.States.RenewalCompleted => AgentStatus.Completed,

        _ => throw new ArgumentOutOfRangeException(
            nameof(state), state,
            $"No agent-status mapping for RenewalProcess state '{state}'. A state added to "
            + "RenewalProcess.States must gain an arm here (ADR-IC-018 §D3); the per-state lock test guards this."),
    };
}
