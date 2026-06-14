namespace Babelstone.Orchestrator.Saga;

/// <summary>
/// The business states of the <c>ConstitutionProcess</c> saga (ADR-IC-003 §Context;
/// Document 05 "Constitution Saga Walkthrough"). Named for what the BUSINESS situation is,
/// not for what the system is doing internally (ADR-IC-003 §P3) — an operator reads the
/// saga's <c>state</c> column directly and understands what is happening, which is what
/// makes the ops console possible without a vendor workflow UI (ADR-IC-003 §S2).
/// </summary>
/// <remarks>
/// The enum NAMES are the contract; the persisted column stores the name verbatim
/// (<see cref="SagaStateNames"/> round-trips it). Adding a state here is a deliberate,
/// reviewable change to the state machine's vocabulary — illegal states are impossible to
/// even name (ADR-IC-003 §P2).
/// </remarks>
public enum SagaState
{
    // --- Happy path (ADR-IC-003 §Context "Happy path") -----------------------------
    /// <summary>The saga aggregate exists; the edge created it in this state (Document 05
    /// step 0) before the orchestrator took over. The entry state of every instance.</summary>
    Started,

    /// <summary>The two reversible validations (reserve balance, validate product limits)
    /// have been dispatched in parallel and are in flight (Document 05 step 1–2). NEITHER
    /// has arrived yet.</summary>
    ParallelValidation,

    /// <summary>The balance-reservation leg arrived first; the saga waits on the product-limits
    /// leg before the join completes (Document 05 §2c "when the two arrive"). One half of the
    /// order-INDEPENDENT parallel join — the arrival order of the two legs has no delivery
    /// guarantee, so each first-arrival is remembered in the state rather than in a self-loop
    /// that loses which leg is still outstanding.</summary>
    AwaitLimitsValidated,

    /// <summary>The product-limits leg arrived first (the common case — it is a synchronous
    /// in-aggregate calc while balance reservation is an async ~120ms Core round-trip); the
    /// saga waits on the balance-reservation leg before the join completes (Document 05 §2c).
    /// The mirror of <see cref="AwaitLimitsValidated"/> — both converge on
    /// <see cref="ValidationsComplete"/>.</summary>
    AwaitBalanceReserved,

    /// <summary>Both validations succeeded; nothing irreversible has happened yet
    /// (Document 05 step 2c).</summary>
    ValidationsComplete,

    /// <summary>The constitution was approved (auto or via workflow); the irreversible
    /// phase may now begin (Document 05 step 3).</summary>
    Approved,

    // --- Waiting / escalation (ADR-IC-003 §Context "Waiting / escalation") ---------
    /// <summary>A long wait on the external approval workflow (Document 05 "Important
    /// Variation"). A first-class state, not a blocked thread (ADR-IC-003 §P4): the saga
    /// resumes when the approval event arrives.</summary>
    AwaitWorkflowApproval,

    /// <summary>An indeterminate Core debit outcome is being resolved by the clearance job
    /// (Document 05 Scenario C). A long wait expressed as a first-class state, never a busy retry
    /// (ADR-IC-003 §P4 — "long waits are states, not blocked threads"). <b>Wired (bd babelstone-t7o3.10):</b> a <see cref="ConstitutionProcess.CoreDebitIndeterminate"/>
    /// from <see cref="Approved"/> ENTERS it (arming the clearance query
    /// <see cref="ConstitutionProcess.QueryCoreDebitStatus"/>), and the clearance result LEAVES it three
    /// ways — a LATE <see cref="ConstitutionProcess.DebitConfirmed"/> resumes the happy path (back to
    /// <see cref="Approved"/>), a <see cref="ConstitutionProcess.DebitNotExecuted"/> REISSUES the debit
    /// (back to <c>(<see cref="Approved"/>, ConfirmDebit)</c> — the <c>RETRY_PERMITTED</c> disposition of
    /// ADR-IC-012 §D5 step 5 / §P5, inherited by ADR-PC-016 §64; Document 05 "handle as a normal error →
    /// retry"), and a <see cref="ConstitutionProcess.CompensationFailed"/> escalates to
    /// <see cref="HumanInterventionRequired"/> (§P6). The reissue cannot double-debit: a not-executed
    /// clearance is Core GROUND TRUTH that the debit did NOT land (no money moved), so re-sending is safe
    /// (ADR-IC-012 §P5 / §332 — double-debit prevented by construction); the same-idempotency-key re-send
    /// and the ACL's "only re-send from RETRY_PERMITTED" guard are the ACL's machinery (DEF-1 / babelstone-ub9s),
    /// and the retry BOUND is the ACL's clearance plus the §244 INDETERMINATE-backlog alert, NOT a saga
    /// busy-retry. The saga NEVER blind-retries the debit — it waits, queries, and the system converges.
    /// (The "unwired / H.2-reserved" note was removed when the edges landed; H.2 / babelstone-n55u is closed.)</summary>
    AwaitCoreClearance,

    /// <summary>A compensation (or an indeterminate effect) could not be resolved
    /// automatically; an operator must reconcile manually (ADR-IC-003 §P6; Document 05
    /// Scenario B "even worse case"). The system makes its need for help explicit rather
    /// than swallowing an exception.</summary>
    HumanInterventionRequired,

    // --- Compensation paths (ADR-IC-003 §Context "Compensation paths") -------------
    /// <summary>An early failure (a validation rejected); release whatever reversible
    /// effect already succeeded — e.g. the Core hold (Document 05 Scenario A).</summary>
    CompensateValidations,

    /// <summary>A late failure after the irreversible Core debit (Document 05 Scenario B):
    /// the debit must be reversed as a domain action (ADR-IC-003 §P6), never a DB rollback.</summary>
    CompensatePostDebit,

    // --- Terminal (ADR-IC-003 §Context "Terminal") ---------------------------------
    /// <summary>The saga completed successfully (Document 05 step 6). Terminal.</summary>
    Completed,

    /// <summary>The saga was cancelled before any irreversible effect — a clean business
    /// error with no real-world impact (Document 05 Scenario A). Terminal.</summary>
    Cancelled,

    /// <summary>The saga was cancelled AFTER the Core debit, with the debit reversed by a
    /// compensating credit (Document 05 Scenario B). Terminal — and distinct from
    /// <see cref="Cancelled"/> because money DID move and was returned.</summary>
    CancelledAfterDebit,

    /// <summary>A required PRECONDITION was refused during the validation phase, BEFORE any
    /// irreversible effect and BEFORE approval (H.2, babelstone-n55u). A clean terminal
    /// failure: nothing reversible was committed when the refusal landed, so there is NOTHING
    /// to compensate — the saga emits NO reversal command and ends here. Distinct from
    /// <see cref="Cancelled"/> (a validation REJECTION that DID release a Core hold) because a
    /// precondition refusal is a fail-CLOSED before any effect, mirroring the edge precondition
    /// where "the orchestrator never starts" (ADR-PC-024 §5; Document 05
    /// step 0 SCA/clearance pattern). The settlement-driven flavour of this terminal — a Core
    /// reserve refused for InsufficientBalance — is governed by ADR-PC-016 §70/§127
    /// (InsufficientBalance → DepositConstitutionFailed, failure_reason: INSUFFICIENT_FUNDS); the
    /// §127 reason taxonomy is H.2's verdict (babelstone-n55u), not carried at v1. Terminal.</summary>
    DepositConstitutionFailed,
}

/// <summary>
/// The verbatim string names the saga states persist as (ADR-IC-003 §P3: the column is
/// directly meaningful to a human operator, so it stores a readable SCREAMING_SNAKE label,
/// not an ordinal). The persisted form is decoupled from the enum's declaration order so a
/// reorder never silently rewrites history.
/// </summary>
public static class SagaStateNames
{
    /// <summary>The canonical persisted name for a <see cref="SagaState"/> (the column value).</summary>
    public static string ToName(SagaState state) => state switch
    {
        SagaState.Started => "STARTED",
        SagaState.ParallelValidation => "PARALLEL_VALIDATION",
        SagaState.AwaitLimitsValidated => "AWAIT_LIMITS_VALIDATED",
        SagaState.AwaitBalanceReserved => "AWAIT_BALANCE_RESERVED",
        SagaState.ValidationsComplete => "VALIDATIONS_COMPLETE",
        SagaState.Approved => "APPROVED",
        SagaState.AwaitWorkflowApproval => "AWAIT_WORKFLOW_APPROVAL",
        SagaState.AwaitCoreClearance => "AWAIT_CORE_CLEARANCE",
        SagaState.HumanInterventionRequired => "HUMAN_INTERVENTION_REQUIRED",
        SagaState.CompensateValidations => "COMPENSATE_VALIDATIONS",
        SagaState.CompensatePostDebit => "COMPENSATE_POST_DEBIT",
        SagaState.Completed => "COMPLETED",
        SagaState.Cancelled => "CANCELLED",
        SagaState.CancelledAfterDebit => "CANCELLED_AFTER_DEBIT",
        SagaState.DepositConstitutionFailed => "DEPOSIT_CONSTITUTION_FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown saga state."),
    };

    /// <summary>Parse a persisted name back to its <see cref="SagaState"/>. Throws on an
    /// unknown label — a row whose state column does not round-trip is a corruption, not a
    /// silently-tolerated value.</summary>
    public static SagaState FromName(string name) => name switch
    {
        "STARTED" => SagaState.Started,
        "PARALLEL_VALIDATION" => SagaState.ParallelValidation,
        "AWAIT_LIMITS_VALIDATED" => SagaState.AwaitLimitsValidated,
        "AWAIT_BALANCE_RESERVED" => SagaState.AwaitBalanceReserved,
        "VALIDATIONS_COMPLETE" => SagaState.ValidationsComplete,
        "APPROVED" => SagaState.Approved,
        "AWAIT_WORKFLOW_APPROVAL" => SagaState.AwaitWorkflowApproval,
        "AWAIT_CORE_CLEARANCE" => SagaState.AwaitCoreClearance,
        "HUMAN_INTERVENTION_REQUIRED" => SagaState.HumanInterventionRequired,
        "COMPENSATE_VALIDATIONS" => SagaState.CompensateValidations,
        "COMPENSATE_POST_DEBIT" => SagaState.CompensatePostDebit,
        "COMPLETED" => SagaState.Completed,
        "CANCELLED" => SagaState.Cancelled,
        "CANCELLED_AFTER_DEBIT" => SagaState.CancelledAfterDebit,
        "DEPOSIT_CONSTITUTION_FAILED" => SagaState.DepositConstitutionFailed,
        _ => throw new ArgumentException($"Unknown persisted saga state '{name}'.", nameof(name)),
    };

    /// <summary>Whether a state is terminal — the saga is done and accepts no further
    /// transitions (ADR-IC-003 §Context "Terminal"). A transition targeting a terminal
    /// state is the saga's last; an event arriving for a terminal saga is a no-op advance.</summary>
    public static bool IsTerminal(SagaState state) => state is
        SagaState.Completed or SagaState.Cancelled or SagaState.CancelledAfterDebit
        or SagaState.DepositConstitutionFailed;
}
