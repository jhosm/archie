namespace Babelstone.Families.TermDeposit;

/// <summary>
/// The term-deposit lifecycle state machine (F.3, babelstone-29v8): the ONE explicit, auditable
/// transition-legality table for the aggregate — constitution → active → maturity / termination /
/// succession → closed. F.2 (babelstone-5czr) landed the full <see cref="DepositLifecycle"/> enum
/// and all eleven events but deliberately did NOT enforce transition legality ("the F.3 state
/// machine, deliberately NOT enforced here"); this type is that enforcement, owned by the family.
/// </summary>
/// <remarks>
/// <para>
/// This is pure data + a pure predicate (no clock, no I/O, no randomness — BENG001/002/003): the
/// table is a static map and <see cref="IsLegal"/> only reads it. It is intentionally NOT an
/// <c>IEventHandler</c> fold — folds LABEL state (the F.2 handlers do that); this machine answers
/// the orthogonal command-side question the decider asks BEFORE appending: <em>is moving from the
/// current lifecycle via this transition legal?</em> The decider (ADR-PC-021 §P3) consults it and
/// rejects an illegal command with the established <c>DomainRejectedException</c>; the folds remain
/// guard-free label-only writes.
/// </para>
/// <para>
/// Scope (F.3 owns only <em>where transitions are legal</em> and <em>rejecting illegal commands</em>):
/// the table names every F.2 event's legal source states, including
/// Renewed / TerminatedEarly / PartiallyWithdrawn / TransferredToHeirs / Corrected — but the full
/// command logic for those flows is downstream and hangs off THIS table: early-termination policies
/// (F.4, babelstone-nbip), auto-renewal (F.5, babelstone-k4yr), partial-withdrawal rules
/// (F.12, babelstone-k6r8.5). F.3 does not build those; it makes their transitions legality-checkable.
/// </para>
/// </remarks>
public static class LifecycleTransitions
{
    /// <summary>
    /// A lifecycle transition keyed by the event that drives it (one transition per F.2 event).
    /// Naming the transition by its driving event — not by a free-standing token — keeps the table
    /// in lock-step with the event taxonomy the family already owns; adding an event without a row
    /// here is the only way a new transition can exist, which is the auditability F.3 buys.
    /// </summary>
    public enum Transition
    {
        /// <summary>Open a deposit — <see cref="DepositConstituted"/> (the stream's first event).</summary>
        Constitute,

        /// <summary>Reject constitution — <see cref="DepositConstitutionFailed"/> (no deposit opens).</summary>
        FailConstitution,

        /// <summary>Accrue interest — <see cref="InterestAccrued"/> (state-preserving on an Active deposit).</summary>
        AccrueInterest,

        /// <summary>Apply withholding — <see cref="WithholdingApplied"/> (state-preserving on an Active deposit).</summary>
        ApplyWithholding,

        /// <summary>Pay an intermediate coupon — <see cref="InterestPaid"/> (state-preserving on an Active deposit).</summary>
        PayInterest,

        /// <summary>Mature and pay out — <see cref="DepositMatured"/> (Active → Matured).</summary>
        Mature,

        /// <summary>Auto-renew into a new term — <see cref="DepositRenewed"/> (Active → Renewed). Command logic: F.5.</summary>
        Renew,

        /// <summary>Break before maturity — <see cref="DepositTerminatedEarly"/> (Active → TerminatedEarly). Command logic: F.4.</summary>
        TerminateEarly,

        /// <summary>Withdraw part of the principal — <see cref="DepositPartiallyWithdrawn"/> (state-preserving on an Active deposit). Command logic: F.12.</summary>
        PartiallyWithdraw,

        /// <summary>Transfer to heirs on succession — <see cref="DepositTransferredToHeirs"/> (Active → TransferredToHeirs).</summary>
        TransferToHeirs,

        /// <summary>Correct a recorded fact — <see cref="DepositCorrected"/> (state-preserving; the real bitemporal supersession is D.1/D.2).</summary>
        Correct,
    }

    // The transition-legality table: for each transition, the set of lifecycle states it may fire FROM.
    // This is the single source of truth the decider consults. Reading rules:
    //   - "Pending" is the seed state before any event — the only legal source for opening (Constitute)
    //     or rejecting (FailConstitution) a deposit. A deposit can only be constituted once.
    //   - "Active" is the live, accruing deposit. Every operating transition — accrue, withhold, coupon,
    //     mature, renew, terminate-early, partial-withdraw, transfer-to-heirs, correct — fires only here.
    //   - Matured / Failed / Renewed / TerminatedEarly / TransferredToHeirs are TERMINAL ("closed"):
    //     no transition lists any of them as a legal source, so the decider rejects e.g. maturing a
    //     Matured deposit or paying a coupon on a closed one. Terminality is expressed as ABSENCE from
    //     every source set, not a separate flag — one table, no second place to keep in sync.
    private static readonly IReadOnlyDictionary<Transition, IReadOnlySet<DepositLifecycle>> LegalSources =
        new Dictionary<Transition, IReadOnlySet<DepositLifecycle>>
        {
            // Opening / rejecting: only from the seed Pending state (constitute-once).
            [Transition.Constitute] = Set(DepositLifecycle.Pending),
            [Transition.FailConstitution] = Set(DepositLifecycle.Pending),

            // Operating on a live deposit: only from Active.
            [Transition.AccrueInterest] = Set(DepositLifecycle.Active),
            [Transition.ApplyWithholding] = Set(DepositLifecycle.Active),
            [Transition.PayInterest] = Set(DepositLifecycle.Active),
            [Transition.PartiallyWithdraw] = Set(DepositLifecycle.Active),
            [Transition.Correct] = Set(DepositLifecycle.Active),

            // Closing a live deposit (each lands in a distinct terminal state): only from Active.
            [Transition.Mature] = Set(DepositLifecycle.Active),
            [Transition.Renew] = Set(DepositLifecycle.Active),
            [Transition.TerminateEarly] = Set(DepositLifecycle.Active),
            [Transition.TransferToHeirs] = Set(DepositLifecycle.Active),
        };

    /// <summary>
    /// Is <paramref name="transition"/> legal from <paramref name="current"/>? Pure lookup against
    /// <see cref="LegalSources"/>. A transition with no row (impossible while every <see cref="Transition"/>
    /// is in the table) or a source state not in its set is illegal — the decider turns a <c>false</c>
    /// here into a <c>DomainRejectedException</c>.
    /// </summary>
    public static bool IsLegal(DepositLifecycle current, Transition transition) =>
        LegalSources.TryGetValue(transition, out var sources) && sources.Contains(current);

    private static IReadOnlySet<DepositLifecycle> Set(params DepositLifecycle[] states) =>
        new HashSet<DepositLifecycle>(states);
}
