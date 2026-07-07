namespace Babelstone.Families.CurrentAccount;

/// <summary>
/// The current_account lifecycle state machine (ADR-PC-037): the ONE explicit, auditable
/// transition-legality table for the demand account — open → active (transacting) → (dormant ⇄
/// active) → closed, plus the open-refusal and GDPR-erasure terminals. This is the enforcement the
/// pure folds deliberately omit (folds LABEL state; this machine answers the orthogonal command-side
/// question the decider asks BEFORE appending: <em>is moving from the current lifecycle via this
/// transition legal?</em>).
/// </summary>
/// <remarks>
/// <para>
/// Pure data + a pure predicate (no clock, no I/O, no randomness — BENG001/002/003): the table is a
/// static map and <see cref="IsLegal"/> only reads it. It is intentionally NOT an <c>IEventHandler</c>
/// fold. The decider (ADR-PC-021 §P3) consults it and rejects an illegal command with the established
/// <c>DomainRejectedException</c>; the folds remain guard-free label-only writes.
/// </para>
/// <para>
/// <b>Dormant is non-terminal and reversible</b> (ADR-PC-037), distinguishing this family from the
/// loan's good-or-closed binary: <c>MarkDormant</c> runs Active → Dormant and <c>Reactivate</c> runs
/// Dormant → Active, so Dormant is legal only between Active states. Closing a DORMANT account
/// (Dormant → Closed) is deliberately NOT modelled here: ADR-PC-037 keeps operating transitions
/// running only from Active and defers a widened dormant policy as an additive change — so a
/// Dormant → Closed row is a one-line additive extension a later change makes, not a silent divergence
/// this scaffold takes.
/// </para>
/// </remarks>
public static class LifecycleTransitions
{
    /// <summary>
    /// A lifecycle transition keyed by the event that drives it (one transition per family event).
    /// Naming the transition by its driving event — not by a free-standing token — keeps the table in
    /// lock-step with the event taxonomy the family owns; adding an event without a row here is the
    /// only way a new transition can exist, which is the auditability this table buys.
    /// </summary>
    public enum Transition
    {
        /// <summary>Open the account — <see cref="AccountOpened"/> (the stream's first event).</summary>
        Open,

        /// <summary>Reject opening — <see cref="AccountOpeningFailed"/> (no account opens).</summary>
        FailOpening,

        /// <summary>Mark the account dormant — <see cref="AccountMarkedDormant"/> (Active → Dormant).</summary>
        MarkDormant,

        /// <summary>Reactivate a dormant account — <see cref="AccountReactivated"/> (Dormant → Active).</summary>
        Reactivate,

        /// <summary>Close the account — <see cref="AccountClosed"/> (Active → Closed).</summary>
        Close,

        /// <summary>Erase the subject's personal data — the engine-declared cross-cutting
        /// <see cref="Babelstone.Engine.PersonalDataErasureRequested"/> (→ Erased). GDPR Article 17
        /// (ADR-PC-004 §P3/A4): legal from ANY state that still holds the subject's PII (live OR
        /// already-closed), never from Pending (no account) or Erased (idempotent).</summary>
        Erase,
    }

    // The transition-legality table: for each transition, the set of lifecycle states it may fire FROM.
    // This is the single source of truth the decider consults. Reading rules:
    //   - "Pending" is the seed state before any event — the only legal source for opening (Open) or
    //     rejecting (FailOpening) an account. An account can only be opened once.
    //   - "Active" is the live, transacting account. Every operating transition — mark-dormant, close
    //     (and the authorize path, a later change) — fires only here.
    //   - "Dormant" is the reversible non-terminal state: reachable from Active (MarkDormant) and
    //     legal only as the source of Reactivate (Dormant → Active). It lists no business-closing
    //     transition (see the §D2 note in the class remarks).
    //   - Failed / Closed are BUSINESS-TERMINAL: no BUSINESS transition lists either as a legal source,
    //     so the decider rejects e.g. closing a Closed account. Business terminality is expressed as
    //     ABSENCE from every business-transition source set — one table, no separate flag.
    //   - The ONE exception is the cross-cutting regulatory Erase transition (GDPR Article 17,
    //     ADR-PC-004 §P3): it DELIBERATELY lists the business-terminal states as legal sources, because
    //     a closed/failed account still holds the subject's PII until erased. Erasure is orthogonal to
    //     the business lifecycle, not part of it.
    //   - Erased is the GENUINELY-terminal state: the legal source of NO transition (business or
    //     regulatory), so even a re-erasure is rejected (the idempotency guard).
    private static readonly IReadOnlyDictionary<Transition, IReadOnlySet<AccountLifecycle>> LegalSources =
        new Dictionary<Transition, IReadOnlySet<AccountLifecycle>>
        {
            // Opening / rejecting: only from the seed Pending state (open-once).
            [Transition.Open] = Set(AccountLifecycle.Pending),
            [Transition.FailOpening] = Set(AccountLifecycle.Pending),

            // Operating on a live account: mark-dormant and close fire only from Active.
            [Transition.MarkDormant] = Set(AccountLifecycle.Active),
            [Transition.Close] = Set(AccountLifecycle.Active),

            // The reversible Dormant ⇄ Active pair: reactivation fires only from Dormant.
            [Transition.Reactivate] = Set(AccountLifecycle.Dormant),

            // GDPR erasure (ADR-PC-004 §P3): legal from any state that still holds the subject's PII —
            // a live account (Active/Dormant) OR an already-closed one (Failed/Closed). Excluded:
            // Pending (no account exists to erase) and Erased itself (already erased — the decider
            // rejects a re-erasure as an illegal transition, which is also the idempotency guard).
            [Transition.Erase] = Set(
                AccountLifecycle.Active,
                AccountLifecycle.Dormant,
                AccountLifecycle.Failed,
                AccountLifecycle.Closed),
        };

    /// <summary>
    /// Is <paramref name="transition"/> legal from <paramref name="current"/>? Pure lookup against
    /// <see cref="LegalSources"/>. A transition with no row (impossible while every
    /// <see cref="Transition"/> is in the table) or a source state not in its set is illegal — the
    /// decider turns a <c>false</c> here into a <c>DomainRejectedException</c>.
    /// </summary>
    public static bool IsLegal(AccountLifecycle current, Transition transition) =>
        LegalSources.TryGetValue(transition, out var sources) && sources.Contains(current);

    private static IReadOnlySet<AccountLifecycle> Set(params AccountLifecycle[] states) =>
        new HashSet<AccountLifecycle>(states);
}
