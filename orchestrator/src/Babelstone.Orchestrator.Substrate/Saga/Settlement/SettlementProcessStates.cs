namespace Babelstone.Orchestrator.Saga.Settlement;

public sealed partial class SettlementProcess
{
    /// <summary>
    /// The business states of the substrate-owned <see cref="SettlementProcess"/> saga (ADR-IC-018 §D3) —
    /// the state VOCABULARY. Each constant is the verbatim SCREAMING_SNAKE label persisted in
    /// <c>saga_state.state</c>; the substrate treats the value as an OPAQUE string (it persists and compares
    /// it, never a central enum). Named for the BUSINESS situation, not the system's internals (ADR-IC-003
    /// §P3), so an operator reads the <c>state</c> column and understands where the cash leg is.
    /// </summary>
    /// <remarks>
    /// UNLIKE the term-deposit saga states, these name NO family — they are generic money-movement states
    /// (reserving a hold, confirming a debit/credit, awaiting clearance) over the ADR-PC-032 <c>Movement</c>
    /// atom, which is what lets this concrete saga be substrate-owned (the narrowed ORCH-2 allow-list).
    /// HUMAN_INTERVENTION_REQUIRED is the shared escalation state an operator resolves OUT of; it is
    /// NON-terminal BY TABLE (the OperatorResolved edge exists), so <see cref="IsTerminal"/> needs no
    /// override (the RenewalProcess posture).
    /// </remarks>
    public static class States
    {
        /// <summary>The saga aggregate exists, auto-started on a <c>Movement</c>-bearing event (ADR-IC-018
        /// §P5). The entry state; the direction-substituted start event branches it into the debit or credit
        /// path.</summary>
        public const string SettlementStarted = "SETTLEMENT_STARTED";

        /// <summary>Debit path: the reversible balance hold (<c>ReserveAccountBalance</c>) is in flight to
        /// the Core ACL — the §P5 reversible-first leg of a funds-gated debit.</summary>
        public const string Reserving = "RESERVING";

        /// <summary>Debit path: the hold succeeded; the irreversible debit (<c>ConfirmDebit</c>) is in flight
        /// — reachable ONLY after the reserve cleared (§P5 reversibility ordering).</summary>
        public const string ConfirmingDebit = "CONFIRMING_DEBIT";

        /// <summary>Credit path: the confirmation-gated credit (<c>ConfirmCredit</c>) is in flight to the
        /// Core ACL — a credit needs no reserve leg (the legacy Core always accepts it), only the confirm
        /// that drives reconciliation flow 1 (ADR-PC-016 slot 5).</summary>
        public const string ConfirmingCredit = "CONFIRMING_CREDIT";

        /// <summary>Debit path: the debit returned INDETERMINATE; the saga parks here and the clearance query
        /// (<c>QueryCoreDebitStatus</c>) is in flight — a first-class wait, never a blind retry (ADR-IC-003
        /// §P4; ADR-IC-012 §P5).</summary>
        public const string AwaitDebitClearance = "AWAIT_DEBIT_CLEARANCE";

        /// <summary>Credit path: the credit returned INDETERMINATE; the saga parks here and the credit
        /// clearance query (<c>QueryCoreCreditStatus</c>) is in flight — the credit-side clearance the new
        /// confirmation-gated credit surface adds (feature-design §10: a non-confirm enters clearance, never
        /// silent).</summary>
        public const string AwaitCreditClearance = "AWAIT_CREDIT_CLEARANCE";

        /// <summary>The cash leg cleared (debit or credit confirmed, or a clearance resolved as executed, or
        /// an operator resolved a parked saga). Terminal.</summary>
        public const string SettlementCompleted = "SETTLEMENT_COMPLETED";

        /// <summary>The cash leg could not be effected and the failure is unrecoverable (a refused reserve, a
        /// clearance that cannot resolve). An operator reconciles — the fact is durable append-first, so this
        /// is NEVER a compensation (ADR-IC-003 §P6 — the money either moved or did not). NON-terminal: the
        /// <c>OperatorResolved</c> edge resolves it (it exists in the table, so the substrate default reports
        /// it non-terminal — no IsTerminal override needed).</summary>
        public const string HumanInterventionRequired = "HUMAN_INTERVENTION_REQUIRED";

        /// <summary>Whether a state is terminal for the <see cref="SettlementProcess"/>. The terminal set IS
        /// exactly "has no outgoing edge in the table" — SETTLEMENT_COMPLETED is the only such state, and
        /// HUMAN_INTERVENTION_REQUIRED is NON-terminal (the table HAS an outgoing OperatorResolved edge). So
        /// this is the SAME answer as the substrate default <c>TableStateMachine.IsTerminal</c>, kept as a
        /// named predicate for tests and any reader, in lockstep with the table by construction.</summary>
        public static bool IsTerminal(string state) => state == SettlementCompleted;
    }
}
