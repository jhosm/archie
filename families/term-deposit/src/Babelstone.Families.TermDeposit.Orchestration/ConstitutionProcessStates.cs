namespace Babelstone.Families.TermDeposit.Orchestration;

public sealed partial class ConstitutionProcess
{
    /// <summary>
    /// The business states of the <c>ConstitutionProcess</c> saga (ADR-IC-003 §Context; Document 05
    /// "Constitution Saga Walkthrough") — the family-owned state VOCABULARY (ADR-IC-018 §D3/§P3). Each
    /// constant is the verbatim SCREAMING_SNAKE label persisted in <c>saga_state.state</c>; the substrate
    /// treats the value as an OPAQUE string, so dissolving the old central <c>SagaState</c> enum into
    /// these constants is a type-level change, not a schema one — the persisted strings are byte-for-byte
    /// what the enum's <c>SagaStateNames.ToName</c> produced.
    /// </summary>
    /// <remarks>
    /// Named for what the BUSINESS situation is, not for what the system is doing internally
    /// (ADR-IC-003 §P3) — an operator reads the saga's <c>state</c> column directly and understands what
    /// is happening, which is what makes the ops console possible without a vendor workflow UI
    /// (ADR-IC-003 §S2). Adding a state here is a deliberate, reviewable change to the state machine's
    /// vocabulary.
    /// </remarks>
    public static class States
    {
        // --- Happy path (ADR-IC-003 §Context "Happy path") -----------------------------
        /// <summary>The saga aggregate exists; the edge created it in this state (Document 05
        /// step 0) before the orchestrator took over. The entry state of every instance.</summary>
        public const string Started = "STARTED";

        /// <summary>The two reversible validations (reserve balance, validate product limits) have been
        /// dispatched in parallel and are in flight (Document 05 step 1–2). NEITHER has arrived yet.</summary>
        public const string ParallelValidation = "PARALLEL_VALIDATION";

        /// <summary>The balance-reservation leg arrived first; the saga waits on the product-limits leg
        /// before the join completes (Document 05 §2c). One half of the order-INDEPENDENT parallel join.</summary>
        public const string AwaitLimitsValidated = "AWAIT_LIMITS_VALIDATED";

        /// <summary>The product-limits leg arrived first; the saga waits on the balance-reservation leg
        /// before the join completes (Document 05 §2c). The mirror of <see cref="AwaitLimitsValidated"/>.</summary>
        public const string AwaitBalanceReserved = "AWAIT_BALANCE_RESERVED";

        /// <summary>Both validations succeeded; nothing irreversible has happened yet (Document 05 step 2c).</summary>
        public const string ValidationsComplete = "VALIDATIONS_COMPLETE";

        /// <summary>The constitution was approved (auto or via workflow); the irreversible phase may now
        /// begin (Document 05 step 3).</summary>
        public const string Approved = "APPROVED";

        // --- Waiting / escalation (ADR-IC-003 §Context "Waiting / escalation") ---------
        /// <summary>A long wait on the external approval workflow (Document 05 "Important Variation").
        /// A first-class state, not a blocked thread (ADR-IC-003 §P4).</summary>
        public const string AwaitWorkflowApproval = "AWAIT_WORKFLOW_APPROVAL";

        /// <summary>An indeterminate Core debit outcome is being resolved by the clearance job
        /// (Document 05 Scenario C). A long wait expressed as a first-class state, never a busy retry
        /// (ADR-IC-003 §P4).</summary>
        public const string AwaitCoreClearance = "AWAIT_CORE_CLEARANCE";

        /// <summary>A compensation (or an indeterminate effect) could not be resolved automatically; an
        /// operator must reconcile manually (ADR-IC-003 §P6; Document 05 Scenario B). NON-terminal — an
        /// operator resolves out of it (the resolution edge arrives with PR2).</summary>
        public const string HumanInterventionRequired = "HUMAN_INTERVENTION_REQUIRED";

        // --- Compensation paths (ADR-IC-003 §Context "Compensation paths") -------------
        /// <summary>An early failure (a validation rejected); release whatever reversible effect already
        /// succeeded — e.g. the Core hold (Document 05 Scenario A).</summary>
        public const string CompensateValidations = "COMPENSATE_VALIDATIONS";

        /// <summary>A late failure after the irreversible Core debit (Document 05 Scenario B): the debit
        /// must be reversed as a domain action (ADR-IC-003 §P6), never a DB rollback.</summary>
        public const string CompensatePostDebit = "COMPENSATE_POST_DEBIT";

        // --- Terminal (ADR-IC-003 §Context "Terminal") ---------------------------------
        /// <summary>The saga completed successfully (Document 05 step 6). Terminal.</summary>
        public const string Completed = "COMPLETED";

        /// <summary>The saga was cancelled before any irreversible effect — a clean business error with
        /// no real-world impact (Document 05 Scenario A). Terminal.</summary>
        public const string Cancelled = "CANCELLED";

        /// <summary>The saga was cancelled AFTER the Core debit, with the debit reversed by a
        /// compensating credit (Document 05 Scenario B). Terminal — and distinct from
        /// <see cref="Cancelled"/> because money DID move and was returned.</summary>
        public const string CancelledAfterDebit = "CANCELLED_AFTER_DEBIT";

        /// <summary>A required PRECONDITION was refused during the validation phase, BEFORE any
        /// irreversible effect and BEFORE approval (H.2). A clean terminal failure: nothing reversible was
        /// committed, so there is NOTHING to compensate. Terminal.</summary>
        public const string DepositConstitutionFailed = "DEPOSIT_CONSTITUTION_FAILED";

        /// <summary>
        /// Whether a state is terminal for the <see cref="ConstitutionProcess"/> — the saga is done and
        /// accepts no further transitions (ADR-IC-003 §Context "Terminal"). This is the SAME predicate the
        /// pre-dissolution <c>SagaStateNames.IsTerminal</c> applied, so the disposition of every state —
        /// and in particular HUMAN_INTERVENTION_REQUIRED — is unchanged by the substrate refactor.
        /// <para>
        /// <b>HUMAN_INTERVENTION_REQUIRED is intentionally EXCLUDED</b> (kept NON-terminal): an operator
        /// resolves it. The substrate's default <c>TableStateMachine.IsTerminal</c> (pure table inspection)
        /// would report HIR terminal today (no outgoing edge), so <see cref="ConstitutionProcess.IsTerminal"/>
        /// overrides the default to delegate HERE — the override is what keeps HIR non-terminal and the
        /// extraction behaviour-preserving (the MultiSagaSubstrateTests HIR test locks this).
        /// </para></summary>
        public static bool IsTerminal(string state) => state is
            Completed or Cancelled or CancelledAfterDebit or DepositConstitutionFailed;
    }
}
