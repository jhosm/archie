using System.Diagnostics;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Saga;
using Npgsql;

namespace Babelstone.Families.TermDeposit.Orchestration;

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
public sealed partial class ConstitutionProcess : TableStateMachine, IEventSubstitutor, IPostAdvanceHook
{
    /// <summary>The persisted <c>saga_type</c> discriminator for constitution sagas.</summary>
    public const string Type = "ConstitutionProcess";

    /// <summary>The per-saga business-reference store the post-advance approval-fork hook reads (the
    /// references the edge pinned at start). Constructed once; the substrate names no family store.</summary>
    private readonly SagaBusinessReferenceStore _businessReferenceStore = new();

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
    /// <summary>Deposit aggregate: the deposit was constituted; closes the saga (Document 05 step 6).
    /// <para>
    /// <b>The VALUE is the engine's catalogued event name, not a saga-internal label (bd babelstone-3klm).</b>
    /// The saga reaches COMPLETED on the engine's resulting <c>DepositConstituted</c> EVENT — the ADR-PC-029
    /// slot-2 bus-resume advance, NOT the <c>ActivateDeposit</c> HTTP 2xx (which bd babelstone-t7o3.8
    /// deliberately stopped self-advancing on). The engine's outbox relay publishes that fact with
    /// <c>ce_type = com.bank.deposits.DepositConstituted</c>, and the consume loop keys the transition
    /// table on the <c>ce_type</c>'s record name (<c>SagaConsumeLoop.RecordName</c> →
    /// <c>"DepositConstituted"</c>), correlated to this saga by <c>ce_subject → process_id</c>. So this
    /// constant's VALUE must be that exact record name, <c>"DepositConstituted"</c> — otherwise a real
    /// bus event lands as <c>AdvanceOutcome.NoTransition</c> (poison) and the happy path strands at
    /// APPROVED. The C# identifier stays <c>ProcessConstituted</c> (the saga's vocabulary for "the process
    /// constituted the deposit"); ADR-IC-003's 2026-06-14 amendment already treats
    /// <c>DepositConstituted</c>/<c>ProcessConstituted</c> as the same engine-relayed fact, so aligning the
    /// value to the engine's name is the conformant move, not a divergence — no Accepted ADR Decision is
    /// edited.
    /// </para></summary>
    public const string ProcessConstituted = "DepositConstituted";
    /// <summary>Core ACL: the reversible hold was released — compensation done
    /// (Document 05 Scenario A).</summary>
    public const string ReservationReleased = "ReservationReleased";
    /// <summary>Core ACL: the debit reversal credit committed — late compensation done
    /// (Document 05 Scenario B).</summary>
    public const string DebitReversed = "DebitReversed";
    /// <summary>Core ACL: a compensation could not be completed — escalation trigger
    /// (Document 05 Scenario B "even worse case"). The ACL reported INDETERMINATE.</summary>
    public const string CompensationFailed = "CompensationFailed";
    /// <summary>Core ACL: the ConfirmDebit returned INDETERMINATE — the network dropped after the debit
    /// was sent, so the ACL cannot yet confirm whether the Core actually executed it (Document 05
    /// Scenario C; bd babelstone-t7o3.10). The saga must NOT blind-retry (it could double-debit); this
    /// event parks it in the first-class waiting state <c>States.AwaitCoreClearance</c> until a
    /// clearance query resolves the outcome (ADR-IC-003 §P4 — a long wait is a named state, never a busy
    /// retry). The THIRD Core-ACL debit outcome alongside
    /// <see cref="DebitConfirmed"/> (executed) and a refused debit. NOT a timeout — a timeout stays a
    /// transient idempotent retry; INDETERMINATE is an EXPLICIT ACL settlement signal.</summary>
    public const string CoreDebitIndeterminate = "CoreDebitIndeterminate";
    /// <summary>Core ACL clearance: the indeterminate debit was resolved as NOT executed — no money
    /// moved (Document 05 Scenario C; bd babelstone-t7o3.10). Handled as a normal error → <b>retry</b>
    /// (Document 05 "handle as a normal error → retry"; ADR-IC-012 §D5 step 5 / §P5, inherited by
    /// ADR-PC-016 §64): the saga REISSUES the debit, transitioning <c>States.AwaitCoreClearance</c>
    /// → <c>(APPROVED, ConfirmDebit)</c> — the <c>RETRY_PERMITTED</c> disposition. <b>Safety:</b> the
    /// not-executed clearance result is Core GROUND TRUTH that the debit did NOT land, so the reissue
    /// cannot double-debit (ADR-IC-012 §P5 / §332 — double-debit is prevented by construction). The
    /// same-idempotency-key reissue and the ACL's "only re-send from <c>RETRY_PERMITTED</c>" guard are the
    /// ACL's own machinery (DEF-1 / bd babelstone-ub9s); the retry BOUND is the ACL's clearance plus the
    /// §244 INDETERMINATE-backlog alert, NOT a saga busy-retry (ADR-IC-003 §P4 — a long wait is a named
    /// state, never a busy retry). The reissue counterpart of a late <see cref="DebitConfirmed"/> out of
    /// <c>States.AwaitCoreClearance</c>.</summary>
    public const string DebitNotExecuted = "DebitNotExecuted";
    /// <summary>Orchestrator self-signal (bd babelstone-rq3e): the indeterminate-clearance reissue
    /// BUDGET is spent — the saga has parked in <c>States.AwaitCoreClearance</c> more than the
    /// permitted number of times, so instead of REISSUING the debit again on a not-executed clearance it
    /// escalates. A DISTINCT escalation event (NOT <see cref="CompensationFailed"/> — the compensation did
    /// not fail; the reissue budget did), so the transition log records EXACTLY why the saga went to
    /// <c>States.HumanInterventionRequired</c>. The impure shell (<c>SagaAdvanceHandler</c>)
    /// substitutes this for <see cref="DebitNotExecuted"/> once the budget is spent (it counts prior
    /// AWAIT_CORE_CLEARANCE entries and applies <see cref="ClearanceReissueBudget"/>); the table
    /// maps it to HUMAN_INTERVENTION_REQUIRED. This is a v1 LIVENESS backstop — the AUTHORITATIVE bound on
    /// retries remains the ACL clearance job + the ADR-IC-012 §244 INDETERMINATE-backlog alert (DEF-1 /
    /// babelstone-ub9s); the budget is defense-in-depth so a stubbed ACL cannot busy-loop the saga
    /// (ADR-IC-003 §P4 — a long wait is a named state, never a busy retry; §P6 — escalate, never strand).
    /// Like <see cref="ConstitutionApproved"/> on the approval fork, it is an orchestrator-derived event,
    /// not a Core-ACL one — there is no Avro, no catalog entry, no schema-registry subject.</summary>
    public const string ReissueBudgetExhausted = "ReissueBudgetExhausted";
    /// <summary>An upstream precondition was REFUSED during the validation phase (H.2,
    /// babelstone-n55u): a verdict that did not <see cref="PreconditionVerdict.Accepts"/> arrived
    /// before approval and before any irreversible effect. The fail-CLOSED trigger that lands the
    /// saga in <c>States.DepositConstitutionFailed</c> with NO reversal — nothing was
    /// committed yet, so nothing is compensated (ADR-PC-024 §5; the edge
    /// precondition "the orchestrator never starts", lifted in-saga). DISTINCT from
    /// <see cref="LimitsRejected"/>, which DOES release a Core hold.</summary>
    public const string PreconditionRefused = "PreconditionRefused";

    // --- Commands the saga emits (Document 05; ADR-IC-003 §P1 "the specific commands it
    // emits"). Names are the contract the outbox seam dispatches; the concrete payloads are
    // the H.2 command DTOs (Commands/ConstitutionProcessCommands.cs) keyed on these names.
    // Public so the payload DTOs and the structural fitness tests reference the SAME constant,
    // never a re-typed literal that could drift from the transition table. ----
    /// <summary>Core ACL: place the reversible balance hold (Document 05 step 2a).</summary>
    public const string ReserveAccountBalance = "ReserveAccountBalance";
    /// <summary>Deposit aggregate: check product limits (Document 05 step 2b).</summary>
    public const string ValidateProductLimits = "ValidateProductLimits";
    /// <summary>Core ACL: convert the hold into a real debit — the irreversible step
    /// (Document 05 step 4a). Reachable ONLY from APPROVED (§P5).</summary>
    public const string ConfirmDebit = "ConfirmDebit";
    /// <summary>Deposit aggregate: activate the deposit after the debit (Document 05 step 4b).
    /// Reachable ONLY from APPROVED (§P5).</summary>
    public const string ActivateDeposit = "ActivateDeposit";
    /// <summary>Core ACL: release the reversible hold — early compensation (Document 05
    /// Scenario A). A DOMAIN reversal command, never a rollback (§P6).</summary>
    public const string ReleaseBalanceReservation = "ReleaseBalanceReservation";
    /// <summary>Core ACL: reverse the committed debit with a compensating credit — late
    /// compensation (Document 05 Scenario B). A DOMAIN reversal command (§P6).</summary>
    public const string ReverseCoreDebit = "ReverseCoreDebit";
    /// <summary>Core ACL: query the Core for the actual outcome of an INDETERMINATE debit — the v1
    /// clearance-job mechanism (Document 05 Scenario C; bd babelstone-t7o3.10). Emitted on entering
    /// <c>States.AwaitCoreClearance</c>. A SINGLE event-driven query routed to the Settlement
    /// ACL (POST /v1/debits/clearance), NOT a poll loop — the wait is a first-class state, not a busy
    /// retry (ADR-IC-003 §P4). Its delivery outcome bridges back to the clearance result: executed (2xx)
    /// → a late <see cref="DebitConfirmed"/>; not-executed (4xx) → <see cref="DebitNotExecuted"/>.</summary>
    public const string QueryCoreDebitStatus = "QueryCoreDebitStatus";

    public ConstitutionProcess()
        : base(Type, States.Started, BuildTable())
    {
    }

    /// <summary>
    /// The ConstitutionProcess terminal set is NOT the substrate default "has no outgoing edge"
    /// (ADR-IC-018 §P6 — keeping the extraction behaviour-preserving). It delegates to
    /// <see cref="States.IsTerminal"/>, the exact predicate the advance handler used before the
    /// multi-saga generalisation, so the disposition of every state — and in particular
    /// HUMAN_INTERVENTION_REQUIRED — is unchanged by the refactor.
    /// <para>
    /// <b>Why the override is required, not optional.</b> HUMAN_INTERVENTION_REQUIRED appears in
    /// <see cref="BuildTable"/> only as a <c>To()</c> target (the compensation / clearance escalations),
    /// never as a <c>From</c>-key, so the base <see cref="TableStateMachine.IsTerminal"/> table inspection
    /// would report it terminal (no outgoing edge). But HIR is a production-reachable ESCALATION state an
    /// operator resolves OUT of — the operator-resolution edge does not exist YET (it arrives with PR2).
    /// Treating it as terminal here would change the advance handler's disposition for a late event on a
    /// HIR-parked saga from <c>AdvanceOutcome.NoTransition</c> (the pre-PR1 path → poison-metric)
    /// to <c>AdvanceOutcome.Terminal</c> (a benign no-op), so a replayed event stream would diverge
    /// from the live one. Delegating to <see cref="States.IsTerminal"/> keeps HIR non-terminal — identical
    /// to pre-PR1 — until PR2 adds the resolution edge (at which point the table inspection and this
    /// predicate AGREE on HIR and this override can fold back into the substrate default).
    /// <see cref="States.IsTerminal"/> and this machine's predicate are therefore the SAME answer by
    /// construction, which is what keeps the edge-SSE read (the host's <c>ProcessApiEndpoints</c>, resolving
    /// this machine by saga_type) and the advance handler (on this machine) consistent.
    /// </para></summary>
    public override bool IsTerminal(string state) => States.IsTerminal(state);

    private static IEnumerable<((string, string), TransitionOutcome)> BuildTable()
    {
        // Happy path (Document 05 steps 1–6). Reversible steps first (§P5).
        yield return ((States.Started, ConstitutionRequested),
            TransitionOutcome.To(States.ParallelValidation, ReserveAccountBalance, ValidateProductLimits));

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
        yield return ((States.ParallelValidation, BalanceReserved),
            TransitionOutcome.To(States.AwaitLimitsValidated));
        yield return ((States.ParallelValidation, LimitsValidated),
            TransitionOutcome.To(States.AwaitBalanceReserved));
        yield return ((States.AwaitLimitsValidated, LimitsValidated),
            TransitionOutcome.To(States.ValidationsComplete));
        yield return ((States.AwaitBalanceReserved, BalanceReserved),
            TransitionOutcome.To(States.ValidationsComplete));

        // Approval (auto, via the fork's ConstitutionApproved self-emission) moves to APPROVED
        // and emits ConfirmDebit — the FIRST irreversible command (Document 05 step 4a "Confirm
        // Debit in Core"), issued exactly once the saga crosses into the irreversible phase, and
        // ONLY from a state that has passed every reversible precondition (§P5). The fork that
        // chooses auto-approve vs route-to-workflow is the H.2 ApprovalForkHandler (a pure
        // decider on the edge-pinned amount/threshold/client-type); the table simply records
        // that crossing APPROVED arms the debit.
        yield return ((States.ValidationsComplete, ConstitutionApproved),
            TransitionOutcome.To(States.Approved, ConfirmDebit));

        // Irreversible phase — only ever entered from APPROVED, i.e. after every reversible
        // precondition succeeded (§P5). ConfirmDebit (above) and ActivateDeposit (below) are the
        // two irreversible commands, both reachable ONLY through APPROVED.
        yield return ((States.Approved, DebitConfirmed),
            TransitionOutcome.To(States.Approved, ActivateDeposit));
        yield return ((States.Approved, ProcessConstituted),
            TransitionOutcome.To(States.Completed));

        // Precondition refusal — a fail-CLOSED terminal in the VALIDATION phase, before approval
        // and before any irreversible effect (H.2, babelstone-n55u). A PreconditionRefused event
        // (a verdict that did not Accept, decided by the pure refusal logic over a
        // PreconditionVerdict) lands the saga in DEPOSIT_CONSTITUTION_FAILED, emitting NO reversal
        // command — nothing reversible has been committed at these states, so there is nothing to
        // compensate (ADR-PC-024 §5 "the deposit is never constituted, so there is nothing to unwind"; the
        // edge precondition pattern lifted in-saga). Reachable from every pre-approval validation
        // state, so the refusal is honoured whenever it arrives during validation; NOT reachable
        // from APPROVED or later — past the irreversible line a failure is a COMPENSATION
        // (ActivationFailed → ReverseCoreDebit), never this no-op terminal.
        yield return ((States.ParallelValidation, PreconditionRefused),
            TransitionOutcome.To(States.DepositConstitutionFailed));
        yield return ((States.AwaitLimitsValidated, PreconditionRefused),
            TransitionOutcome.To(States.DepositConstitutionFailed));
        yield return ((States.AwaitBalanceReserved, PreconditionRefused),
            TransitionOutcome.To(States.DepositConstitutionFailed));
        yield return ((States.ValidationsComplete, PreconditionRefused),
            TransitionOutcome.To(States.DepositConstitutionFailed));

        // Long-wait variation: workflow approval (Document 05 "Important Variation", >€25,000).
        // A first-class waiting state (§P4), resumed by the approval event. The wait is armed by
        // a DISTINCT event (WorkflowApprovalRequired), NOT the start event — keying it on the
        // start event would make the row unreachable (the advance handler intercepts the start
        // event and routes it to StartAsync before the table lookup, SagaAdvanceHandler.cs §2),
        // so AWAIT_WORKFLOW_APPROVAL could never be entered. With its own event the fork is
        // reachable from the table alone (§P2 auditability). The concrete fork decision — which
        // amounts auto-approve vs route to workflow, and who emits WorkflowApprovalRequired — is
        // H.2's (babelstone-n55u); the substrate proves the waiting state is wired and resumable.
        yield return ((States.ValidationsComplete, WorkflowApprovalRequired),
            TransitionOutcome.To(States.AwaitWorkflowApproval));
        // The workflow-approved path crosses into APPROVED exactly like the auto-approved one,
        // so it arms the SAME first irreversible command (ConfirmDebit) — the irreversible debit
        // is reachable ONLY through APPROVED whichever approval branch was taken (§P5).
        yield return ((States.AwaitWorkflowApproval, ConstitutionApproved),
            TransitionOutcome.To(States.Approved, ConfirmDebit));

        // Compensation path A — early failure in validation (Document 05 Scenario A).
        // Compensation is a DOMAIN action: emit ReleaseBalanceReservation (§P6), not a rollback.
        // Order-independent like the success join: LimitsRejected can land before its sibling
        // (PARALLEL_VALIDATION) or after the Core hold already succeeded (AWAIT_LIMITS_VALIDATED).
        // Either way the reversible Core hold is released and the saga compensates — neither
        // ordering poisons. (From PARALLEL_VALIDATION the hold may not yet exist; the ACL's
        // ReleaseBalanceReservation is idempotent on a no-op, per Document 02 / ADR-IC-003 §P6.)
        yield return ((States.ParallelValidation, LimitsRejected),
            TransitionOutcome.To(States.CompensateValidations, ReleaseBalanceReservation));
        yield return ((States.AwaitLimitsValidated, LimitsRejected),
            TransitionOutcome.To(States.CompensateValidations, ReleaseBalanceReservation));
        yield return ((States.CompensateValidations, ReservationReleased),
            TransitionOutcome.To(States.Cancelled));
        // A compensation that itself fails escalates — never a swallowed exception (§P6).
        yield return ((States.CompensateValidations, CompensationFailed),
            TransitionOutcome.To(States.HumanInterventionRequired));

        // Compensation path B — late failure after the real debit (Document 05 Scenario B).
        // ReverseCoreDebit is the two-movement domain reversal (§P6), not an undo.
        yield return ((States.Approved, ActivationFailed),
            TransitionOutcome.To(States.CompensatePostDebit, ReverseCoreDebit));
        yield return ((States.CompensatePostDebit, DebitReversed),
            TransitionOutcome.To(States.CancelledAfterDebit));
        yield return ((States.CompensatePostDebit, CompensationFailed),
            TransitionOutcome.To(States.HumanInterventionRequired));

        // Scenario C — INDETERMINATE Core debit clearance (Document 05 Scenario C; bd babelstone-t7o3.10).
        // The ConfirmDebit was sent but the network dropped before its response arrived, so the ACL
        // reported INDETERMINATE: it is UNKNOWN whether the Core actually executed the debit. The saga
        // must NOT blind-retry (a blind retry could double-debit). It parks in the FIRST-CLASS waiting
        // state AWAIT_CORE_CLEARANCE (ADR-IC-003 §P4 — a long wait is a named state, never a busy retry)
        // and emits the clearance QUERY command (QueryCoreDebitStatus), a single event-driven query to the
        // Core ACL — "no blocking thread, no aggressive retries, no inventing state". CoreDebitIndeterminate
        // is the THIRD outcome of the irreversible debit, so like DebitConfirmed it is reachable ONLY from
        // APPROVED (§P5). The clearance query asks Core for GROUND TRUTH, and its verdict resolves the wait:
        //   • EXECUTED: the debit DID land (DebitConfirmed arrives LATE) → resume the happy path exactly
        //     as a timely confirm would (back to APPROVED, arming ActivateDeposit).
        //   • NOT EXECUTED: the clearance is Core ground truth that the debit did NOT land (no money moved)
        //     → REISSUE the debit. The saga returns to (APPROVED, ConfirmDebit) — the RETRY_PERMITTED
        //     disposition. Document 05 says "handle as a normal error → retry"; ADR-IC-012 §D5 step 5 / §P5
        //     (inherited verbatim by ADR-PC-016 §64) specify exactly this not-executed disposition:
        //     transition the in-flight operation to RETRY_PERMITTED and "let the saga's compensation logic
        //     decide whether to reissue (with the same idempotency_key)". This conforms — there is no
        //     divergence to record.
        //     SAFETY (no double-debit): because the not-executed verdict is Core ground truth that nothing
        //     was committed, reissuing cannot double-debit (ADR-IC-012 §P5 / §332 — double-debit prevented
        //     by construction). The same-idempotency-key re-send and the ACL's guard that only re-sends from
        //     RETRY_PERMITTED are the ACL's machinery (DEF-1 / babelstone-ub9s), not the saga's. The
        //     AUTHORITATIVE bound on retries is the ACL's clearance plus the §244 INDETERMINATE-backlog
        //     ALERT, NOT a saga busy-retry (§P4: a long wait is a named state, never a busy retry). At v1
        //     that authoritative bound is not yet built (the ACL is a WireMock shim), so a saga-side reissue
        //     BUDGET backstops the loop as DEFENSE-IN-DEPTH (the ReissueBudgetExhausted edge below; bd
        //     babelstone-rq3e) — it does not replace the ACL's bound, it keeps a stubbed ACL from looping forever.
        //   • A clearance that itself cannot resolve (CompensationFailed) escalates to
        //     HUMAN_INTERVENTION_REQUIRED, never a swallowed/stranded saga (§P6 robustness).
        yield return ((States.Approved, CoreDebitIndeterminate),
            TransitionOutcome.To(States.AwaitCoreClearance, QueryCoreDebitStatus));
        yield return ((States.AwaitCoreClearance, DebitConfirmed),
            TransitionOutcome.To(States.Approved, ActivateDeposit));
        // RETRY_PERMITTED: a not-executed clearance reissues the debit (ADR-IC-012 §D5 step 5 / §P5).
        yield return ((States.AwaitCoreClearance, DebitNotExecuted),
            TransitionOutcome.To(States.Approved, ConfirmDebit));
        yield return ((States.AwaitCoreClearance, CompensationFailed),
            TransitionOutcome.To(States.HumanInterventionRequired));
        // Reissue BUDGET backstop (bd babelstone-rq3e). The ACL clearance is the AUTHORITATIVE convergence,
        // but at v1 it is a stub, so a Core that keeps answering not-executed would reissue forever via the
        // DebitNotExecuted edge above. The impure shell (SagaAdvanceHandler) counts prior AWAIT_CORE_CLEARANCE
        // entries and, once the reissue budget is spent (Handlers.ClearanceReissueBudget), feeds
        // ReissueBudgetExhausted INSTEAD of DebitNotExecuted — landing the saga in HUMAN_INTERVENTION_REQUIRED
        // rather than busy-looping (ADR-IC-003 §P4 / §P6). ADR-IC-012 §D5 step 5 delegates the reissue decision
        // to "the saga's compensation logic", so this budget CONFORMS — it is that logic deciding reissue-vs-
        // escalate, not a divergence. The DECISION is pure (the count → disposition map); only the COUNT is
        // impure. Like the (AwaitCoreClearance, CompensationFailed) escalation it emits NO command — an
        // operator reconciles from the ops console (§S2). DISTINCT from CompensationFailed so the transition
        // log records that the BUDGET, not a failed compensation, drove the escalation.
        yield return ((States.AwaitCoreClearance, ReissueBudgetExhausted),
            TransitionOutcome.To(States.HumanInterventionRequired));
    }

    // ---- Optional machine hooks (ADR-IC-018 §P6) -------------------------------------------------
    // The two constitution-specific branches the multi-saga substrate (bd babelstone-mtto PR1) carried
    // as saga.SagaType == ConstitutionProcess.Type guards in SagaAdvanceHandler now live HERE, on the
    // family's machine. The substrate dispatches to them generically (IEventSubstitutor / IPostAdvanceHook)
    // and carries NO family branch (ADR-IC-018 §D2/§P6).

    /// <summary>
    /// <see cref="IEventSubstitutor"/> — the indeterminate-clearance reissue BUDGET substitution
    /// (bd babelstone-rq3e, a v1 LIVENESS backstop). A not-executed clearance normally REISSUES the debit
    /// (the RETRY_PERMITTED disposition; the <c>(AWAIT_CORE_CLEARANCE, DebitNotExecuted) → (APPROVED,
    /// ConfirmDebit)</c> edge). At v1 the AUTHORITATIVE bound (the ACL clearance job + the ADR-IC-012 §244
    /// backlog alert) is not built (the ACL is a WireMock shim), so a Core stuck on not-executed would
    /// reissue forever. This hook counts how many times the saga has PARKED in AWAIT_CORE_CLEARANCE and,
    /// once the budget is spent, substitutes the DISTINCT <see cref="ReissueBudgetExhausted"/> for
    /// <see cref="DebitNotExecuted"/> — the table then routes it to HUMAN_INTERVENTION_REQUIRED instead of
    /// another reissue (ADR-IC-003 §P4 — never a busy retry; §P6 — escalate, never strand). The COUNT is
    /// impure (it reads the log); the DECISION is pure (<see cref="ClearanceReissueBudget.Decide"/>), so
    /// the table stays the authority on what each event does (§P2).
    /// </summary>
    public async Task<string> SubstituteAsync(
        string currentState,
        string incomingEventType,
        SagaTransitionLog transitionLog,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        CancellationToken ct)
    {
        if (currentState == States.AwaitCoreClearance && incomingEventType == DebitNotExecuted)
        {
            var priorClearanceEntries = await transitionLog.CountEntriesIntoStateAsync(
                connection, transaction, processId, States.AwaitCoreClearance, ct);
            if (ClearanceReissueBudget.Decide(priorClearanceEntries) == ClearanceReissueDecision.Escalate)
            {
                return ReissueBudgetExhausted;
            }
        }

        return incomingEventType;
    }

    /// <summary>
    /// <see cref="IPostAdvanceHook"/> — the approval-fork SELF-EMIT (bd babelstone-t7o3.1). When this move
    /// landed the saga in VALIDATIONS_COMPLETE — both reversible validations have now completed — the
    /// saga DECIDES the approval fork (auto-approve vs route-to-workflow) on the edge-pinned business
    /// references and feeds the chosen event (<see cref="ConstitutionApproved"/> /
    /// <see cref="WorkflowApprovalRequired"/>) back into THIS SAME advance path, IN-PROCESS within this
    /// transaction (nothing on the durable bus, ADR-IC-003 §S2). That is what crosses the saga into
    /// APPROVED (the auto path) or AWAIT_WORKFLOW_APPROVAL without an external trigger. The DECISION stays
    /// pure (<see cref="ApprovalForkHandler.Decide"/> on the edge-pinned input); this hook only loads the
    /// pinned references and schedules the next step. For any state OTHER than VALIDATIONS_COMPLETE the
    /// hook is a no-op (it checks the landed state itself — the substrate names no family state).
    /// </summary>
    public async Task OnAdvancedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ISagaStateMachine machine,
        Guid processId,
        string newState,
        Guid? correlationId,
        Activity? span,
        SagaStateStore stateStore,
        SagaTransitionLog transitionLog,
        ISagaCommandSink commandSink,
        SagaInboxWriter inboxWriter,
        CancellationToken ct)
    {
        // The fork is ONLY decided when the saga lands in VALIDATIONS_COMPLETE — the hook checks the
        // landed state itself (the substrate dispatched here for every advance of this machine).
        if (newState != States.ValidationsComplete)
        {
            return;
        }

        var reference = await _businessReferenceStore.LoadAsync(connection, transaction, processId, ct)
            ?? throw new InvalidOperationException(
                $"Saga {processId} reached VALIDATIONS_COMPLETE with no pinned business references; " +
                $"every saga must be started at the edge (bd babelstone-t7o3.9). Cannot decide the approval fork.");

        // PURE decision (ADR-PC-010 §P5): auto-approve vs route-to-workflow on the edge-pinned amount /
        // threshold / client type — no clock, no I/O, no live-config dereference. NextEventType maps the
        // decision to the DISTINCT driver event the table accepts out of VALIDATIONS_COMPLETE.
        var decision = ApprovalForkHandler.Decide(States.ValidationsComplete, reference.ToApprovalInput());
        var forkEvent = ApprovalForkHandler.NextEventType(decision);

        // The self-emitted event's message id is DETERMINISTIC (derived from process id + event type,
        // never minted), so it dedups through the SAME inbox as an external advance: a re-drive of the
        // join derives the same id and the dedup row collides — the fork is emitted exactly once.
        var selfMessageId = SagaSelfEmit.MessageId(processId, forkEvent);

        // Apply the fork event through the SAME pure state machine + persistence path the external
        // advance uses, on this transaction. The saga is at VALIDATIONS_COMPLETE (version is current —
        // this is the same row this transaction just advanced).
        var saga = await stateStore.LoadAsync(connection, transaction, processId, ct);
        if (saga is null || saga.State != States.ValidationsComplete)
        {
            return; // raced/already-advanced — the optimistic-concurrency guard owns correctness.
        }

        if (!machine.TryAdvance(saga.State, forkEvent, out var outcome))
        {
            // The decider and the table agree by construction (the ApprovalForkHandler fitness test),
            // so this is unreachable; rejecting rather than inventing a move keeps the table the spec.
            return;
        }

        var won = await stateStore.TryAdvanceAsync(
            connection, transaction, saga.ProcessId, saga.Version, outcome.Next, ct);
        if (!won)
        {
            throw new SagaConcurrencyException(saga.ProcessId, saga.Version);
        }

        await transitionLog.AppendAsync(
            connection, transaction, saga.ProcessId, saga.State, outcome.Next,
            forkEvent, selfMessageId, note: outcome.Next, ct);

        // Record the self-emit in the inbox too, keyed on the deterministic id — so a re-drive of the
        // join short-circuits on the dedup SELECT before re-deciding the fork (effectively-once).
        await inboxWriter.WriteRowAsync(
            connection, transaction,
            new SagaInboxEvent(selfMessageId, processId, forkEvent, SelfEmitSourceTopic, correlationId),
            outcome.Next, ct);

        span?.SetTag("babelstone.saga.transition", $"{saga.State}->{outcome.Next}");

        var traceParent = SagaTraceContext.FormatTraceParent(span);
        foreach (var commandType in outcome.Commands)
        {
            await commandSink.EmitAsync(
                connection, transaction, saga.ProcessId, commandType,
                selfMessageId, correlationId, ct, traceParent);
        }
    }

    /// <summary>The synthetic source topic recorded on a self-emitted event's inbox dedup row. It is an
    /// INTERNAL marker — the self-emit never touches the durable bus (ADR-IC-003 §S2) — so the row's
    /// source is named distinctly from any real Redpanda topic.</summary>
    private const string SelfEmitSourceTopic = "saga.self-emit";
}
