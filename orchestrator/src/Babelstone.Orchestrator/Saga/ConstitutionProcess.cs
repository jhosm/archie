namespace Babelstone.Orchestrator.Saga;

/// <summary>
/// The <c>ConstitutionProcess</c> saga state machine (ADR-IC-003 §Context; Document 05
/// "Constitution Saga Walkthrough"). The transition table below IS the specification
/// (ADR-IC-003 §P2): every legal advance the constitution saga can make, as explicit
/// <c>(from_state, event_type) → (next_state, commands)</c> rows. Anything not in the table
/// is rejected, so an illegal transition is impossible by construction.
/// </summary>
/// <remarks>
/// <b>Scope (babelstone-mj2i / H.1).</b> This is the SUBSTRATE's worked example: it wires
/// the FULL happy + compensation + escalation skeleton of Document 05 so the substrate's
/// behaviour (persisted transitions, compensation, idempotent advance) is exercised
/// end-to-end. The command names are the §P1 "specific commands it emits" as labels; the
/// concrete command PAYLOADS and the business decisions that fork the path (auto-approve vs
/// workflow, retry-before-compensate) are H.2's job (babelstone-n55u). H.3 renewal
/// (babelstone-mtto) is a SEPARATE machine on this same substrate.
/// <para>
/// <b>Reversibility ordering (ADR-IC-003 §P5, Primitive 6) is visible in the table:</b> the
/// reversible validations run first (PARALLEL_VALIDATION), the irreversible Core debit lands
/// only from APPROVED — every reversible precondition has succeeded before any irreversible
/// effect. <b>Compensation is a domain action (ADR-IC-003 §P6):</b> the COMPENSATE_* states
/// emit reversal COMMANDS (ReleaseBalanceReservation, ReverseCoreDebit), and a compensation
/// that cannot resolve lands in HUMAN_INTERVENTION_REQUIRED — never a swallowed exception.
/// </para>
/// </remarks>
public sealed class ConstitutionProcess : TableStateMachine
{
    /// <summary>The persisted <c>saga_type</c> discriminator for constitution sagas.</summary>
    public const string Type = "ConstitutionProcess";

    // --- Triggering inbox events (Document 05). Event TYPE names only — the orchestrator
    // keys the table on the inbox event's type, never on its (PII-free) payload. -------
    /// <summary>Edge → orchestrator: the saga's first event (Document 05 step 1).</summary>
    public const string ConstitutionRequested = "ConstitutionRequested";
    /// <summary>Core ACL: the reversible balance hold succeeded (Document 05 step 2a).</summary>
    public const string BalanceReserved = "BalanceReserved";
    /// <summary>Deposit aggregate: product limits passed (Document 05 step 2b).</summary>
    public const string LimitsValidated = "LimitsValidated";
    /// <summary>Orchestrator self-signal: validations passed but the amount needs the external
    /// workflow rather than auto-approval (Document 05 "Important Variation", >€25,000). The
    /// DISTINCT event that arms the AWAIT_WORKFLOW_APPROVAL wait — never the start event, so the
    /// fork is reachable through the advance handler. The concrete fork decision is H.2's.</summary>
    public const string WorkflowApprovalRequired = "WorkflowApprovalRequired";
    /// <summary>Deposit aggregate: product limits rejected — the early-failure trigger
    /// (Document 05 Scenario A).</summary>
    public const string LimitsRejected = "LimitsRejected";
    /// <summary>Approval granted (auto or via the external workflow) (Document 05 step 3).</summary>
    public const string ConstitutionApproved = "ConstitutionApproved";
    /// <summary>Core ACL: the hold became a real debit (Document 05 step 4a).</summary>
    public const string DebitConfirmed = "DebitConfirmed";
    /// <summary>Deposit aggregate: activation failed AFTER the debit — the late-failure
    /// trigger (Document 05 Scenario B).</summary>
    public const string ActivationFailed = "ActivationFailed";
    /// <summary>Deposit aggregate: the deposit was constituted; closes the saga
    /// (Document 05 step 6).</summary>
    public const string ProcessConstituted = "ProcessConstituted";
    /// <summary>Core ACL: the reversible hold was released — compensation done
    /// (Document 05 Scenario A).</summary>
    public const string ReservationReleased = "ReservationReleased";
    /// <summary>Core ACL: the debit reversal credit committed — late compensation done
    /// (Document 05 Scenario B).</summary>
    public const string DebitReversed = "DebitReversed";
    /// <summary>Core ACL: a compensation could not be completed — escalation trigger
    /// (Document 05 Scenario B "even worse case"). The ACL reported INDETERMINATE.</summary>
    public const string CompensationFailed = "CompensationFailed";

    // --- Commands the saga emits (Document 05; ADR-IC-003 §P1 "the specific commands it
    // emits"). Names are the contract the outbox seam dispatches; payloads are H.2's. ----
    private const string ReserveAccountBalance = "ReserveAccountBalance";
    private const string ValidateProductLimits = "ValidateProductLimits";
    private const string ConfirmDebit = "ConfirmDebit";
    private const string ActivateDeposit = "ActivateDeposit";
    private const string ReleaseBalanceReservation = "ReleaseBalanceReservation";
    private const string ReverseCoreDebit = "ReverseCoreDebit";

    public ConstitutionProcess()
        : base(Type, SagaState.Started, BuildTable())
    {
    }

    private static IEnumerable<((SagaState, string), TransitionOutcome)> BuildTable()
    {
        // Happy path (Document 05 steps 1–6). Reversible steps first (§P5).
        yield return ((SagaState.Started, ConstitutionRequested),
            TransitionOutcome.To(SagaState.ParallelValidation, ReserveAccountBalance, ValidateProductLimits));

        // The two parallel validations land in EITHER order (Document 05 §2c "when the two
        // arrive"). The two triggers have NO delivery-ordering guarantee — BalanceReserved is
        // an async Core SOAP round-trip (~120ms, §2a) while LimitsValidated is a synchronous
        // in-aggregate calc (§2b), so LimitsValidated frequently lands first. The join is made
        // order-INDEPENDENT by remembering which leg arrived in the STATE itself (the only
        // per-saga memory a (state, event)-keyed table has): the first arrival moves to the
        // matching "still awaiting the sibling" state, and EITHER awaiting-state completes to
        // VALIDATIONS_COMPLETE on its sibling. Both orderings reach the SAME join — neither
        // poisons. H.2 may refine this into a completed-legs set on the saga row; the substrate
        // proves the join is symmetric and progression is monotone.
        //
        //   PARALLEL_VALIDATION --BalanceReserved--> AWAIT_LIMITS_VALIDATED --LimitsValidated-->\
        //   PARALLEL_VALIDATION --LimitsValidated--> AWAIT_BALANCE_RESERVED --BalanceReserved-->/ VALIDATIONS_COMPLETE
        yield return ((SagaState.ParallelValidation, BalanceReserved),
            TransitionOutcome.To(SagaState.AwaitLimitsValidated));
        yield return ((SagaState.ParallelValidation, LimitsValidated),
            TransitionOutcome.To(SagaState.AwaitBalanceReserved));
        yield return ((SagaState.AwaitLimitsValidated, LimitsValidated),
            TransitionOutcome.To(SagaState.ValidationsComplete));
        yield return ((SagaState.AwaitBalanceReserved, BalanceReserved),
            TransitionOutcome.To(SagaState.ValidationsComplete));

        yield return ((SagaState.ValidationsComplete, ConstitutionApproved),
            TransitionOutcome.To(SagaState.Approved));

        // Irreversible phase — only ever entered from APPROVED, i.e. after every reversible
        // precondition succeeded (§P5).
        yield return ((SagaState.Approved, DebitConfirmed),
            TransitionOutcome.To(SagaState.Approved, ActivateDeposit));
        yield return ((SagaState.Approved, ProcessConstituted),
            TransitionOutcome.To(SagaState.Completed));

        // Long-wait variation: workflow approval (Document 05 "Important Variation", >€25,000).
        // A first-class waiting state (§P4), resumed by the approval event. The wait is armed by
        // a DISTINCT event (WorkflowApprovalRequired), NOT the start event — keying it on the
        // start event would make the row unreachable (the advance handler intercepts the start
        // event and routes it to StartAsync before the table lookup, SagaAdvanceHandler.cs §2),
        // so AWAIT_WORKFLOW_APPROVAL could never be entered. With its own event the fork is
        // reachable from the table alone (§P2 auditability). The concrete fork decision — which
        // amounts auto-approve vs route to workflow, and who emits WorkflowApprovalRequired — is
        // H.2's (babelstone-n55u); the substrate proves the waiting state is wired and resumable.
        yield return ((SagaState.ValidationsComplete, WorkflowApprovalRequired),
            TransitionOutcome.To(SagaState.AwaitWorkflowApproval));
        yield return ((SagaState.AwaitWorkflowApproval, ConstitutionApproved),
            TransitionOutcome.To(SagaState.Approved));

        // Compensation path A — early failure in validation (Document 05 Scenario A).
        // Compensation is a DOMAIN action: emit ReleaseBalanceReservation (§P6), not a rollback.
        // Order-independent like the success join: LimitsRejected can land before its sibling
        // (PARALLEL_VALIDATION) or after the Core hold already succeeded (AWAIT_LIMITS_VALIDATED).
        // Either way the reversible Core hold is released and the saga compensates — neither
        // ordering poisons. (From PARALLEL_VALIDATION the hold may not yet exist; the ACL's
        // ReleaseBalanceReservation is idempotent on a no-op, per Document 02 / ADR-IC-003 §P6.)
        yield return ((SagaState.ParallelValidation, LimitsRejected),
            TransitionOutcome.To(SagaState.CompensateValidations, ReleaseBalanceReservation));
        yield return ((SagaState.AwaitLimitsValidated, LimitsRejected),
            TransitionOutcome.To(SagaState.CompensateValidations, ReleaseBalanceReservation));
        yield return ((SagaState.CompensateValidations, ReservationReleased),
            TransitionOutcome.To(SagaState.Cancelled));
        // A compensation that itself fails escalates — never a swallowed exception (§P6).
        yield return ((SagaState.CompensateValidations, CompensationFailed),
            TransitionOutcome.To(SagaState.HumanInterventionRequired));

        // Compensation path B — late failure after the real debit (Document 05 Scenario B).
        // ReverseCoreDebit is the two-movement domain reversal (§P6), not an undo.
        yield return ((SagaState.Approved, ActivationFailed),
            TransitionOutcome.To(SagaState.CompensatePostDebit, ReverseCoreDebit));
        yield return ((SagaState.CompensatePostDebit, DebitReversed),
            TransitionOutcome.To(SagaState.CancelledAfterDebit));
        yield return ((SagaState.CompensatePostDebit, CompensationFailed),
            TransitionOutcome.To(SagaState.HumanInterventionRequired));
    }
}
