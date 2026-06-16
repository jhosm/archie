namespace Babelstone.Families.TermDeposit.Orchestration;

/// <summary>The client's standing relative to the bank, as it bears on the approval fork
/// (Document 05 step 3: ">€25,000 OR new client → external workflow"). A closed code — the
/// fork branches on the case, never on free text.</summary>
public enum ClientType
{
    /// <summary>An established relationship. Eligible for auto-approval when the amount is at or
    /// below the pinned threshold (Document 05 step 3 "existing client … auto-approval").</summary>
    Existing,

    /// <summary>A client whose relationship is new to the bank. ALWAYS routes to the external
    /// workflow regardless of amount (Document 05 step 3 ">€25,000 OR new client").</summary>
    New,
}

/// <summary>The disposition of the approval fork (Document 05 step 3). Either the saga
/// auto-approves (the amount is within policy and the client is established) or it routes to
/// the external workflow as a first-class wait (ADR-IC-003 §P4 / §S2 step "Important
/// Variation"). There is no third branch — the decision is total over its inputs.</summary>
public enum ApprovalDecision
{
    /// <summary>Auto-approve: the saga may move straight to APPROVED without an external
    /// workflow (Document 05 step 3 "auto-approval for the main flow").</summary>
    AutoApprove,

    /// <summary>Route to the external approval workflow: the saga arms AWAIT_WORKFLOW_APPROVAL
    /// and sleeps until the approval event arrives (Document 05 "Important Variation").</summary>
    WorkflowApprovalRequired,
}

/// <summary>
/// The resolved, edge-pinned inputs the approval fork decides over (Document 05 step 3). Every
/// field is a RESOLVED SCALAR captured at the edge when the constitution request was admitted —
/// the saga NEVER reaches back into live product config, a rate sheet, or a mutable policy at
/// decision time (replay determinism, ADR-PC-010 §P5; the same "pinned at the edge" discipline
/// the engine pins <c>pack_version</c>/<c>schema_version</c> onto the request envelope).
/// </summary>
/// <remarks>
/// <b>The threshold is pinned, not dereferenced.</b> <see cref="AutoApprovalThresholdMinorUnits"/>
/// arrives ON this input, resolved once at the edge from the policy in force when the request
/// was admitted. Re-resolving it from live config inside the fork would make the SAME request
/// decide differently across a config change or a replay — precisely the non-determinism the
/// pinned-at-the-edge rule forbids. Amounts are integer MINOR UNITS (cents) so the comparison
/// is exact — never a floating-point amount, never a <c>Money</c> dereference.
/// <para>
/// <b>No PII.</b> These are policy scalars and a closed client-type code, never identity data
/// (ADR-PC-004 §P2). The fork needs no NIF/IBAN/name to decide.
/// </para>
/// </remarks>
/// <param name="AmountMinorUnits">The constitution amount in integer minor units (cents),
/// resolved at the edge. Compared against the pinned threshold — exact integer arithmetic, no
/// float, no live <c>Money</c> lookup.</param>
/// <param name="AutoApprovalThresholdMinorUnits">The auto-approval ceiling in integer minor
/// units, PINNED onto this input at the edge from the policy in force at admission. The fork
/// reads it as a scalar argument — it is NEVER dereferenced from a live rate sheet or mutable
/// product config at decision time (Document 05 step 3 "€25,000"; ADR-PC-010 §P5).</param>
/// <param name="ClientType">The client's standing (existing / new), resolved at the edge. A new
/// client always routes to the workflow regardless of amount (Document 05 step 3).</param>
public readonly record struct ApprovalDecisionInput(
    long AmountMinorUnits,
    long AutoApprovalThresholdMinorUnits,
    ClientType ClientType);

/// <summary>
/// The approval-fork decider (Document 05 step 3 "Approval Decision — Synchronous or via
/// Workflow?"). A PURE function of the saga's current state and the edge-pinned
/// <see cref="ApprovalDecisionInput"/>: it decides auto-approve vs route-to-workflow with NO
/// clock, NO I/O, NO randomness, and NO live-config dereference (ADR-PC-010 §P5). Given the
/// same (state, input) it returns the same decision forever — the property that makes a saga
/// replay reproduce its history exactly.
/// </summary>
/// <remarks>
/// <para>
/// The fork is reachable only once the reversible validations have completed
/// (VALIDATIONS_COMPLETE) — it decides which event the orchestrator self-emits next, the
/// auto-approve <see cref="ConstitutionProcess.ConstitutionApproved"/> or the workflow-arming
/// <see cref="ConstitutionProcess.WorkflowApprovalRequired"/> (the distinct event that makes the
/// AWAIT_WORKFLOW_APPROVAL row reachable, per the §P2 note on <see cref="ConstitutionProcess"/>).
/// </para>
/// <para>
/// <b>What this decider deliberately does NOT do (the §P5 must-nots):</b> it does not read the
/// wall clock, does not mint a GUID, does not consult a rate sheet or live product config, and
/// does not look at any verdict's age. The threshold is an argument; the amount is an argument;
/// the client type is an argument. Everything the decision needs is on the input.
/// </para>
/// </remarks>
public static class ApprovalForkHandler
{
    /// <summary>
    /// Decide the approval fork for a saga in <paramref name="current"/> on the edge-pinned
    /// <paramref name="input"/>. PURE: the decision is a total function of (state, input) with no
    /// side effects (ADR-PC-010 §P5).
    /// <para>
    /// <b>Rule (Document 05 step 3):</b> auto-approve iff the client is
    /// <see cref="ClientType.Existing"/> AND the amount is at or below the PINNED threshold;
    /// otherwise route to the external workflow. A <see cref="ClientType.New"/> client ALWAYS
    /// routes, regardless of amount.
    /// </para>
    /// </summary>
    /// <param name="current">The saga state the fork is decided in. The fork is only legitimate
    /// at <see cref="ConstitutionProcess.States.ValidationsComplete"/> — every reversible precondition has
    /// succeeded and nothing irreversible has happened (Document 05 step 2c → 3).</param>
    /// <param name="input">The resolved, edge-pinned decision inputs. The threshold rides HERE.</param>
    /// <returns>The fork disposition.</returns>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="current"/> is not the
    /// VALIDATIONS_COMPLETE state the fork is decided in — the fork cannot be evaluated before
    /// validations complete or after the irreversible phase began (§P5 reversibility ordering).</exception>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="input"/> carries a
    /// negative amount or a negative threshold — a structurally impossible policy.</exception>
    public static ApprovalDecision Decide(string current, ApprovalDecisionInput input)
    {
        if (current != ConstitutionProcess.States.ValidationsComplete)
        {
            throw new ArgumentOutOfRangeException(
                nameof(current), current,
                "The approval fork is only decided at VALIDATIONS_COMPLETE — after the reversible " +
                "validations and before any irreversible effect (Document 05 step 2c → 3; §P5).");
        }

        if (input.AmountMinorUnits < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input), input.AmountMinorUnits, "A constitution amount cannot be negative.");
        }

        if (input.AutoApprovalThresholdMinorUnits < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input), input.AutoApprovalThresholdMinorUnits,
                "The pinned auto-approval threshold cannot be negative.");
        }

        // A new client always routes to the workflow, regardless of amount (Document 05 step 3
        // "OR new client"). The threshold gate only applies to established relationships.
        if (input.ClientType != ClientType.Existing)
        {
            return ApprovalDecision.WorkflowApprovalRequired;
        }

        // Existing client: auto-approve iff at or below the PINNED threshold. The threshold is
        // the argument input.AutoApprovalThresholdMinorUnits — never a live-config dereference.
        // Exact integer comparison on minor units (cents); no float, no Money lookup.
        return input.AmountMinorUnits <= input.AutoApprovalThresholdMinorUnits
            ? ApprovalDecision.AutoApprove
            : ApprovalDecision.WorkflowApprovalRequired;
    }

    /// <summary>
    /// The inbox event type the orchestrator self-emits for <paramref name="decision"/> — the
    /// DISTINCT next driver event that advances the saga out of VALIDATIONS_COMPLETE (per the
    /// <see cref="ConstitutionProcess"/> §P2 fork note). Auto-approve emits
    /// <see cref="ConstitutionProcess.ConstitutionApproved"/>; route-to-workflow emits
    /// <see cref="ConstitutionProcess.WorkflowApprovalRequired"/>, which arms the
    /// AWAIT_WORKFLOW_APPROVAL wait. Pure — a total map over the closed decision enum.
    /// </summary>
    public static string NextEventType(ApprovalDecision decision) => decision switch
    {
        ApprovalDecision.AutoApprove => ConstitutionProcess.ConstitutionApproved,
        ApprovalDecision.WorkflowApprovalRequired => ConstitutionProcess.WorkflowApprovalRequired,
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unknown approval decision."),
    };
}
