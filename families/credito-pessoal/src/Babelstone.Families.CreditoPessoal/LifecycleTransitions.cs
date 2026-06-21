namespace Babelstone.Families.CreditoPessoal;

/// <summary>
/// The credito_pessoal lifecycle state machine: the ONE explicit, auditable transition-legality table
/// for the loan aggregate — disburse → active → settle / write-off → closed. Mirrors the term-deposit
/// family's <c>LifecycleTransitions</c> (ADR-PC-021): pure data + a pure predicate (no clock, no I/O,
/// no randomness), consulted by the decider BEFORE appending, never an <c>IEventHandler</c> fold.
/// </summary>
/// <remarks>
/// The decider (ADR-PC-021 §P3) consults <see cref="IsLegal"/> and rejects an illegal command with the
/// established <c>DomainRejectedException</c>; the folds remain guard-free label-only writes. Naming each
/// transition by its driving event keeps the table in lock-step with the event taxonomy the family owns:
/// adding an event without a row here is the only way a new transition can exist — the auditability this buys.
/// </remarks>
public static class LifecycleTransitions
{
    /// <summary>A lifecycle transition keyed by the event that drives it (one transition per event).</summary>
    public enum Transition
    {
        /// <summary>Disburse a loan — <see cref="LoanDisbursed"/> (the stream's first event).</summary>
        Disburse,

        /// <summary>Reject disbursement — <see cref="LoanDisbursementFailed"/> (no loan opens).</summary>
        FailDisbursement,

        /// <summary>Pay a scheduled installment — <see cref="LoanInstallmentPaid"/> (state-preserving on an Active loan).</summary>
        PayInstallment,

        /// <summary>Repay early — <see cref="LoanRepaidEarly"/> (state-preserving on an Active loan; a FULL
        /// repayment is followed by a separate <see cref="Settle"/>).</summary>
        RepayEarly,

        /// <summary>Settle a fully-amortized loan — <see cref="LoanSettled"/> (Active → Settled).</summary>
        Settle,

        /// <summary>Write off after default — <see cref="LoanWrittenOff"/> (Active → WrittenOff).</summary>
        WriteOff,

        /// <summary>Erase the subject's personal data — <see cref="PersonalDataErasureRequested"/>
        /// (→ Erased). GDPR Article 17 (ADR-PC-004 §P3): legal from ANY state that still holds the
        /// subject's PII (live OR already-closed), never from Pending (no loan) or Erased (idempotent).</summary>
        Erase,
    }

    // The transition-legality table: for each transition, the set of lifecycle states it may fire FROM.
    //   - "Pending" is the seed state — the only legal source for disbursing or rejecting a loan
    //     (disburse-once).
    //   - "Active" is the live, amortizing loan. Every operating transition — pay an installment, repay
    //     early, settle, write off — fires only here.
    //   - Failed / Settled / WrittenOff are BUSINESS-TERMINAL ("closed"): no BUSINESS transition lists any
    //     of them as a legal source. Business terminality is expressed as ABSENCE from every
    //     business-transition source set — one table, no separate flag.
    //   - The ONE exception is the cross-cutting regulatory Erase transition (GDPR Article 17, ADR-PC-004
    //     §P3): it DELIBERATELY lists the business-terminal states as legal sources, because a closed loan
    //     still holds the subject's PII until erased.
    //   - Erased is the GENUINELY-terminal state: the legal source of NO transition (business or
    //     regulatory), so even a re-erasure is rejected (the idempotency guard).
    private static readonly IReadOnlyDictionary<Transition, IReadOnlySet<LoanLifecycle>> LegalSources =
        new Dictionary<Transition, IReadOnlySet<LoanLifecycle>>
        {
            // Opening / rejecting: only from the seed Pending state (disburse-once).
            [Transition.Disburse] = Set(LoanLifecycle.Pending),
            [Transition.FailDisbursement] = Set(LoanLifecycle.Pending),

            // Operating on a live loan: only from Active.
            [Transition.PayInstallment] = Set(LoanLifecycle.Active),
            [Transition.RepayEarly] = Set(LoanLifecycle.Active),

            // Closing a live loan (each lands in a distinct terminal state): only from Active.
            [Transition.Settle] = Set(LoanLifecycle.Active),
            [Transition.WriteOff] = Set(LoanLifecycle.Active),

            // GDPR erasure (ADR-PC-004 §P3): legal from any state that still holds the subject's PII —
            // a live loan OR an already-closed one. Excluded: Pending (no loan exists to erase) and
            // Erased itself (already erased — the decider rejects a re-erasure, also the idempotency guard).
            [Transition.Erase] = Set(
                LoanLifecycle.Active,
                LoanLifecycle.Failed,
                LoanLifecycle.Settled,
                LoanLifecycle.WrittenOff),
        };

    /// <summary>
    /// Is <paramref name="transition"/> legal from <paramref name="current"/>? Pure lookup against
    /// <see cref="LegalSources"/>. A source state not in a transition's set is illegal — the decider
    /// turns a <c>false</c> here into a <c>DomainRejectedException</c>.
    /// </summary>
    public static bool IsLegal(LoanLifecycle current, Transition transition) =>
        LegalSources.TryGetValue(transition, out var sources) && sources.Contains(current);

    private static IReadOnlySet<LoanLifecycle> Set(params LoanLifecycle[] states) =>
        new HashSet<LoanLifecycle>(states);
}
