namespace Babelstone.Orchestrator.Saga;

/// <summary>
/// The COARSE, agent-facing lifecycle status of a saga process (Document 11 Pattern 2; bd
/// babelstone-vjoi). The MCP async-completion polling tool (<c>get_process_status</c>) surfaces this
/// projection to an AI agent that cannot hold the long-lived SSE stream the browser edge uses — the
/// agent POLLS a single status snapshot instead. A closed vocabulary of exactly six values, deliberately
/// SMALLER than any saga's internal state set: an agent needs "is it still working / does a human need to
/// act / did it finish, and how", not the full operator-grade state machine an operator reads off
/// <c>saga_state.state</c>.
/// </summary>
/// <remarks>
/// These are the AGENT contract — coarse, stable, and saga-agnostic — whereas the verbatim
/// <c>saga_state.state</c> string is the FAMILY's own vocabulary (ADR-IC-018 §D3). The status endpoint
/// returns BOTH: the raw <c>state</c> (for an operator / a richer client) AND this coarse <c>status</c>
/// (for the agent), plus the machine's <c>terminal</c> flag. Mapping one to the other is a family concern
/// — the family owns what each of its states MEANS — so it lives behind <see cref="ISagaAgentStatusMap"/>,
/// resolved at the edge by <c>saga_type</c>, never as an edge-side switch over family state names.
/// </remarks>
public static class AgentStatus
{
    /// <summary>The process is in flight — the system is still working it (validations, an approved
    /// irreversible phase, a core-clearance wait, an automatic compensation). No caller action needed;
    /// poll again.</summary>
    public const string Processing = "PROCESSING";

    /// <summary>The process is paused on an approval the caller's side can resolve (the external
    /// approval workflow). Distinct from <see cref="ActionRequired"/>, which is an OPERATOR escalation.</summary>
    public const string AwaitingApproval = "AWAITING_APPROVAL";

    /// <summary>The process is parked pending manual reconciliation by a BANK OPERATOR
    /// (HUMAN_INTERVENTION_REQUIRED). Non-terminal — an operator resolves it out of band; the agent
    /// surfaces it but cannot clear it.</summary>
    public const string ActionRequired = "ACTION_REQUIRED";

    /// <summary>The process finished successfully (terminal).</summary>
    public const string Completed = "COMPLETED";

    /// <summary>The process ended in a clean failure with nothing left half-done (terminal) — e.g. a
    /// precondition refused before any irreversible effect.</summary>
    public const string Failed = "FAILED";

    /// <summary>The process was cancelled (terminal) — including the case where money moved and was
    /// returned by a compensating reversal. The agent does not need the with/without-reversal distinction
    /// the operator state set keeps.</summary>
    public const string Cancelled = "CANCELLED";
}

/// <summary>
/// The family-owned projection from a saga's verbatim <c>saga_state.state</c> string to the coarse
/// agent-facing <see cref="AgentStatus"/> (bd babelstone-vjoi / Document 11 Pattern 2). Each saga family
/// supplies exactly one of these per saga type and registers it in its <see cref="ISagaModule"/>'s
/// <c>ConfigureServices</c>; the edge's process-status endpoint resolves it by <c>saga_type</c> — the SAME
/// resolve-by-saga_type move the SSE read uses for the <see cref="ISagaStateMachine"/> terminality check
/// (ADR-IC-018 §D3/§7). The substrate names no family; the family owns what each of its states MEANS.
/// </summary>
/// <remarks>
/// Keeping this behind a family-resolved port (rather than an edge-side switch over state names) is what
/// preserves ADR-IC-018 §D3: the family owns its state VOCABULARY and its meaning, and the substrate/edge
/// treat the state as an opaque string they route on. A consumer holds the routed machine for the
/// <c>terminal</c> flag and the routed status map for the coarse <c>status</c>; the two stay consistent
/// because the family authors them together against the same state set.
/// </remarks>
public interface ISagaAgentStatusMap
{
    /// <summary>The <c>saga_type</c> discriminator this map governs — equal to the matching
    /// <see cref="ISagaStateMachine.SagaType"/>. The edge keys the per-saga-type registry on it.</summary>
    string SagaType { get; }

    /// <summary>
    /// Project the verbatim family <paramref name="state"/> to its coarse <see cref="AgentStatus"/>. MUST
    /// be total over the saga's state set: an unmapped state is a specification gap, not a default — an
    /// implementation throws so the per-state lock test (and a fail-closed 500 at the edge) catches a state
    /// added without a mapping, exactly as the terminality predicate is kept in lockstep with the table.
    /// </summary>
    string StatusFor(string state);
}
