namespace Babelstone.Families.TermDeposit.Orchestration;

/// <summary>The disposition of the indeterminate-clearance reissue budget (bd babelstone-rq3e). On a
/// NOT-EXECUTED clearance the saga either REISSUES the debit (the RETRY_PERMITTED disposition of
/// ADR-IC-012 §D5 step 5 / §P5) or, once the v1 reissue budget is spent, ESCALATES to
/// HUMAN_INTERVENTION_REQUIRED rather than reissuing forever. There is no third branch — the
/// decision is total over the clearance-cycle count.</summary>
public enum ClearanceReissueDecision
{
    /// <summary>Reissue the debit: the saga is still within its v1 reissue budget, so a not-executed
    /// clearance returns it to <c>(APPROVED, ConfirmDebit)</c> for another attempt (RETRY_PERMITTED).</summary>
    Reissue,

    /// <summary>Escalate: the saga has parked in AWAIT_CORE_CLEARANCE more than the budget permits, so
    /// instead of reissuing again it goes to HUMAN_INTERVENTION_REQUIRED — never a busy retry, never a
    /// stranded saga (ADR-IC-003 §P4 / §P6).</summary>
    Escalate,
}

/// <summary>
/// The indeterminate-clearance reissue BUDGET (bd babelstone-rq3e) — a v1 LIVENESS backstop on the
/// Scenario-C RETRY_PERMITTED loop. When a Core clearance finds an indeterminate debit NOT executed,
/// the saga reissues the debit (ADR-IC-012 §D5 step 5 / §P5, inherited by ADR-PC-016 §64). At v1 the
/// AUTHORITATIVE bound on that loop — the real ACL clearance job plus the ADR-IC-012 §244
/// INDETERMINATE-backlog alert — is not yet built (the ACL is a WireMock shim; DEF-1 / babelstone-ub9s
/// lands it), so a Core that kept answering not-executed would reissue indefinitely. This decider adds a
/// hard saga-side cap: after <see cref="MaxReissues"/> reissues, the saga escalates instead.
/// </summary>
/// <remarks>
/// <para>
/// <b>Defense-in-depth, not the primary bound.</b> ADR-IC-012 §D5 step 5 delegates the reissue decision
/// to "the saga's compensation logic" — this budget IS that logic deciding reissue-vs-escalate, so it
/// conforms rather than diverges. The ACL clearance remains the AUTHORITATIVE convergence; the budget is
/// the v1 backstop that keeps a stubbed ACL from busy-looping the saga (ADR-IC-003 §P4 — a long wait is a
/// named state, never a busy retry; §P6 — a process that cannot resolve escalates, never strands).
/// </para>
/// <para>
/// <b>Pure (ADR-PC-010 §P5).</b> The decision is a total function of the clearance-cycle count alone — no
/// clock, no I/O, no randomness. The impure shell (<c>SagaAdvanceHandler</c>) owns the COUNT (it reads the
/// transition log); this decider only maps that count to a disposition, so a replay reproduces it exactly.
/// </para>
/// </remarks>
public static class ClearanceReissueBudget
{
    /// <summary>
    /// The maximum number of RETRY_PERMITTED reissues the saga will attempt before escalating. After this
    /// many reissues the next not-executed clearance escalates to HUMAN_INTERVENTION_REQUIRED instead of
    /// reissuing again. A small v1 backstop value — the AUTHORITATIVE bound is the ACL clearance job +
    /// the ADR-IC-012 §244 backlog alert (DEF-1), not this number.
    /// </summary>
    public const int MaxReissues = 3;

    /// <summary>
    /// Decide reissue-vs-escalate from the number of times the saga has ENTERED AWAIT_CORE_CLEARANCE
    /// (its clearance-cycle count, read from the transition log). PURE — a total function of the count
    /// (ADR-PC-010 §P5).
    /// <para>
    /// <b>The arithmetic.</b> The FIRST AWAIT_CORE_CLEARANCE entry is the ORIGINAL indeterminate debit,
    /// not a reissue; each subsequent entry is a reissue that came back indeterminate. So the number of
    /// reissues already attempted is <c>priorClearanceEntries - 1</c>. The saga reissues while that is
    /// below <see cref="MaxReissues"/>, and escalates once the budget is spent. Worked example with
    /// <see cref="MaxReissues"/> = 3: entries 1, 2, 3 reissue (the original + 2 prior reissues each
    /// still under budget), and the 4th not-executed clearance (entries = 4, i.e. 3 reissues already
    /// done) escalates — exactly "reissue up to 3, then escalate".
    /// </para>
    /// </summary>
    /// <param name="priorClearanceEntries">The count of transitions that LANDED the saga in
    /// AWAIT_CORE_CLEARANCE so far, INCLUDING the park the not-executed clearance is currently resolving.
    /// A not-executed clearance can only arrive while the saga is parked, so this is always at least 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="priorClearanceEntries"/> is below
    /// 1 — a not-executed clearance with no recorded park is structurally impossible (the saga must have
    /// entered AWAIT_CORE_CLEARANCE to receive the clearance result).</exception>
    public static ClearanceReissueDecision Decide(int priorClearanceEntries)
    {
        if (priorClearanceEntries < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(priorClearanceEntries), priorClearanceEntries,
                "A not-executed clearance can only arrive while the saga is parked in AWAIT_CORE_CLEARANCE, " +
                "so the recorded clearance-cycle count must be at least 1.");
        }

        // reissues already attempted = entries - 1 (the first entry is the original indeterminate debit,
        // not a reissue). Reissue while under budget; escalate once it is spent.
        var reissuesAlreadyAttempted = priorClearanceEntries - 1;
        return reissuesAlreadyAttempted < MaxReissues
            ? ClearanceReissueDecision.Reissue
            : ClearanceReissueDecision.Escalate;
    }
}
