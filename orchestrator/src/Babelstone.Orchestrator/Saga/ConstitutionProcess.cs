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

        // The two parallel validations land in EITHER order; both must arrive before the
        // saga is VALIDATIONS_COMPLETE. The intermediate single-arrival is modelled as a
        // self-loop on PARALLEL_VALIDATION (the saga stays put, emitting nothing, until its
        // sibling arrives). H.2 refines the join into per-leg tracking; the substrate proves
        // the legal pairs are accepted and progression is monotone.
        yield return ((SagaState.ParallelValidation, BalanceReserved),
            TransitionOutcome.To(SagaState.ParallelValidation));
        yield return ((SagaState.ParallelValidation, LimitsValidated),
            TransitionOutcome.To(SagaState.ValidationsComplete));

        yield return ((SagaState.ValidationsComplete, ConstitutionApproved),
            TransitionOutcome.To(SagaState.Approved));

        // Irreversible phase — only ever entered from APPROVED, i.e. after every reversible
        // precondition succeeded (§P5).
        yield return ((SagaState.Approved, DebitConfirmed),
            TransitionOutcome.To(SagaState.Approved, ActivateDeposit));
        yield return ((SagaState.Approved, ProcessConstituted),
            TransitionOutcome.To(SagaState.Completed));

        // Long-wait variation: workflow approval (Document 05 "Important Variation"). A
        // first-class waiting state (§P4), resumed by the approval event.
        yield return ((SagaState.ValidationsComplete, ConstitutionRequested),
            TransitionOutcome.To(SagaState.AwaitWorkflowApproval)); // (placeholder fork; H.2 decides)
        yield return ((SagaState.AwaitWorkflowApproval, ConstitutionApproved),
            TransitionOutcome.To(SagaState.Approved));

        // Compensation path A — early failure in validation (Document 05 Scenario A).
        // Compensation is a DOMAIN action: emit ReleaseBalanceReservation (§P6), not a rollback.
        yield return ((SagaState.ParallelValidation, LimitsRejected),
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
