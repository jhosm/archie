namespace Babelstone.Families.TermDeposit.Orchestration;

public sealed partial class RenewalProcess
{
    /// <summary>
    /// The business states of the <see cref="RenewalProcess"/> saga (ADR-IC-018 §D3) — the family-owned
    /// state VOCABULARY. Each constant is the verbatim SCREAMING_SNAKE label persisted in
    /// <c>saga_state.state</c>; the substrate treats the value as an OPAQUE string (it persists and
    /// compares it, never a central enum). Named for the BUSINESS situation, not the system's internals
    /// (ADR-IC-003 §P3), so an operator reads the saga's <c>state</c> column and understands where the
    /// renewal is.
    /// </summary>
    /// <remarks>
    /// The renewal saga drives the cross-stream renewal sequence AFTER the closing deposit has matured
    /// (ADR-IC-003 §P6 — the payout already moved at maturity, so failures NEVER compensate; they
    /// escalate). The three forward states track the two idempotent engine legs PR B shipped:
    /// RENEWAL_CONSTITUTING (the <c>constitute-renewal</c> leg opening the new stream) and RENEWAL_LINKING
    /// (the <c>renewal-link</c> leg folding the closing stream Matured → Renewed). HUMAN_INTERVENTION_REQUIRED
    /// is the shared escalation state an operator resolves OUT of — and here, UNLIKE the constitution
    /// saga, it is NON-terminal BY TABLE (the OperatorResolved edge exists in <see cref="RenewalProcess"/>'s
    /// table), so <see cref="IsTerminal"/> needs no override.
    /// </remarks>
    public static class States
    {
        /// <summary>The saga aggregate exists, started on the closing deposit's <c>DepositMatured</c> bus
        /// fact (ADR-IC-018 §P5 event-auto-start). The entry state of every renewal instance.</summary>
        public const string RenewalStarted = "RENEWAL_STARTED";

        /// <summary>The <c>ConstituteRenewal</c> command is in flight to the engine's
        /// <c>constitute-renewal</c> leg — opening the NEW (renewed) stream off the Matured closing
        /// deposit (02 §2.4.4 step 2; PR B's idempotent endpoint).</summary>
        public const string RenewalConstituting = "RENEWAL_CONSTITUTING";

        /// <summary>The new stream is constituted; the <c>LinkRenewal</c> command is in flight to the
        /// engine's <c>renewal-link</c> leg — folding the CLOSING stream Matured → Renewed (terminal),
        /// the old→new link the maturity calendar follows.</summary>
        public const string RenewalLinking = "RENEWAL_LINKING";

        /// <summary>The renewal completed successfully: the new stream is open and the closing stream is
        /// linked to it (Renewed). Terminal.</summary>
        public const string RenewalCompleted = "RENEWAL_COMPLETED";

        /// <summary>A renewal leg could not be completed automatically (a refused constitute/link, or an
        /// explicit escalation). An operator reconciles manually — money ALREADY moved at maturity, so this
        /// is NEVER a compensation (ADR-IC-003 §P6). NON-terminal: the <c>OperatorResolved</c> edge resolves
        /// it (it exists in the table, so the substrate default reports it non-terminal — no IsTerminal
        /// override needed, unlike the constitution saga whose resolution edge does not yet exist).</summary>
        public const string HumanInterventionRequired = "HUMAN_INTERVENTION_REQUIRED";

        /// <summary>
        /// Whether a state is terminal for the <see cref="RenewalProcess"/>. The renewal saga's terminal
        /// set IS exactly "has no outgoing edge in the table" — RENEWAL_COMPLETED is the only such state,
        /// and HUMAN_INTERVENTION_REQUIRED is NON-terminal because the table HAS an outgoing OperatorResolved
        /// edge from it. So this is the SAME answer as the substrate's default
        /// <c>TableStateMachine.IsTerminal</c> (pure table inspection), and <see cref="RenewalProcess"/>
        /// deliberately does NOT override it (unlike <c>ConstitutionProcess</c>, whose HIR has no outgoing
        /// edge yet and so needs an override to stay non-terminal). Provided as a named predicate for the
        /// unit test and any reader, kept in lockstep with the table by construction.
        /// </summary>
        public static bool IsTerminal(string state) => state == RenewalCompleted;
    }
}
