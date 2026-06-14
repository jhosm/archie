namespace Babelstone.Orchestrator.Saga;

/// <summary>
/// The TERMINAL delivery outcome of a saga command, as the result-event bridge sees it (bd
/// babelstone-t7o3.8). The dispatcher classifies an HTTP response into the ADR-PC-029 §slot-5 error
/// model; only the two TERMINAL kinds reach the bridge (a 5xx/timeout stays PENDING and never maps to
/// a result event). The mapper is a pure function of <c>(command_type, kind)</c>, so it is keyed on
/// this closed enum, never a raw status code.
/// </summary>
public enum CommandDeliveryKind
{
    /// <summary>The target accepted the command (a 2xx — applied or an idempotent replay). The leg
    /// SUCCEEDED, so its corresponding success/result event is the one to synthesize.</summary>
    Applied,

    /// <summary>The target REFUSED the command (a 4xx — an illegal lifecycle transition or a
    /// validation reject). A terminal failure: the failure/compensation result event (if any) is the
    /// one to synthesize.</summary>
    Refused,

    /// <summary>The Core ACL returned an EXPLICIT INDETERMINATE settlement signal (HTTP 202 Accepted on a
    /// ConfirmDebit, bd babelstone-t7o3.10): it accepted the debit but cannot yet confirm whether the Core
    /// executed it (the network dropped after the debit was sent — Document 05 Scenario C). A TERMINAL
    /// delivery outcome distinct from <see cref="Applied"/> (2xx success) and <see cref="Refused"/> (4xx):
    /// the leg is neither confirmed nor refused, so the saga parks in <see cref="SagaState.AwaitCoreClearance"/>
    /// rather than advancing or compensating. NOT a timeout — a ConfirmDebit timeout stays a transient,
    /// idempotent retry; this is an explicit ACL signal that the row is terminally resolved-as-unknown.</summary>
    Indeterminate,
}

/// <summary>
/// The PURE command-outcome → result-event bridge (bd babelstone-t7o3.8). Every result event the
/// <see cref="ConstitutionProcess"/> saga consumes derives DETERMINISTICALLY from a command DELIVERY
/// OUTCOME: when the dispatcher flips a <c>saga_outbox</c> row to its terminal status, this maps
/// <c>(command_type, outcome-kind)</c> to the result-event TYPE the saga should self-advance on — or
/// to <c>null</c> when the outcome drives no advance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The constitution saga's state machine and consume loop are fully wired, but
/// nothing produced the <b>Core-ACL</b> result events the saga consumes (<c>BalanceReserved</c>,
/// <c>DebitConfirmed</c>, <c>ActivationFailed</c>, <c>ReservationReleased</c>, <c>DebitReversed</c>,
/// <c>LimitsValidated</c>), so end-to-end the saga stalled at <c>PARALLEL_VALIDATION</c> and the
/// post-debit compensation never fired. At v1 the real Core ACL is a WireMock shim (DEF-1 /
/// babelstone-ub9s replaces it); rather than stand up a Kafka producer for that shim, the orchestrator
/// SYNTHESIZES the ACL result event from the command's own delivery outcome and self-advances IN-PROCESS,
/// in the SAME transaction as the status flip — the SAME "rides nothing on the durable bus" pattern as
/// the t7o3.1 approval-fork self-emit (<see cref="SagaSelfEmit"/>). These result events are INTERNAL
/// orchestrator correlation signals: no Avro, no catalog entry, no schema-registry subject. The v1
/// in-process synthesis is the divergence the ADR-IC-003 Amendment 2026-06-14 (A1) records, backed out
/// when DEF-1 lands the real ACL producing these events on Redpanda.
/// </para>
/// <para>
/// <b>The ENGINE leg is excluded by design (ADR-PC-029 slot 2).</b> <c>ProcessConstituted</c> is NOT
/// synthesized here. The engine's <c>ActivateDeposit</c> HTTP 2xx confirms delivery but is "NOT the
/// saga's signal to advance" — the saga advances on the engine's resulting <c>DepositConstituted</c>
/// event off <c>deposits.process.events</c> via <see cref="Inbox.SagaConsumeLoop"/>. The engine relays
/// that event on the durable bus for real even at v1, so there is no shim to stand in for.
/// </para>
/// <para>
/// <b>Pure (ADR-PC-010 §P5).</b> No clock, no I/O, no randomness — a function of the command type and
/// the delivery kind alone. The impure shell (the dispatcher) owns the connection, the HTTP call, and
/// the deterministic-id derivation; this mapper only decides WHICH event, never WHEN or HOW it lands.
/// </para>
/// <para>
/// <b>No drift from the table.</b> Both the command names it matches and the event names it returns are
/// the SAME <see cref="ConstitutionProcess"/> string constants the transition table keys on — never a
/// re-typed literal — so the bridge and the table cannot diverge.
/// </para>
/// </remarks>
public static class ConstitutionResultEvents
{
    /// <summary>
    /// Map the terminal delivery outcome of <paramref name="commandType"/> to the result-event type
    /// the saga should self-advance on, or <c>null</c> when the outcome drives no advance (an
    /// unmapped pair is a graceful no-op, never an invented transition).
    /// </summary>
    /// <param name="commandType">The <c>command_type</c> of the flipped <c>saga_outbox</c> row — a
    /// <see cref="ConstitutionProcess"/> command-name constant.</param>
    /// <param name="kind">The terminal delivery kind the dispatcher classified the response into.</param>
    public static string? ForOutcome(string commandType, CommandDeliveryKind kind) => (commandType, kind) switch
    {
        // --- ACL settlement legs (2xx Applied → the leg's success result) -------------------------
        // These are the Core-ACL money legs. At v1 the ACL is a WireMock shim with NO event producer,
        // so the bridge SYNTHESIZES the result event from the command's own delivery outcome and
        // self-advances in-process (the v1 stand-in for an event off Redpanda; recorded in the
        // ADR-IC-003 Amendment 2026-06-14 A1, DEF-1 / babelstone-ub9s back-out). The engine leg
        // (ActivateDeposit) is DELIBERATELY NOT here — see below.
        // The reversible balance hold succeeded (Document 05 step 2a) — half the parallel-validation join.
        (ConstitutionProcess.ReserveAccountBalance, CommandDeliveryKind.Applied) => ConstitutionProcess.BalanceReserved,
        // The reversible hold became a real debit (Document 05 step 4a).
        (ConstitutionProcess.ConfirmDebit, CommandDeliveryKind.Applied) => ConstitutionProcess.DebitConfirmed,

        // --- The engine leg (ActivateDeposit) APPLIED is DELIBERATELY NOT bridged ------------------
        // ADR-PC-029 slot 2 (Accepted 2026-06-13) decides this verbatim: the engine's HTTP 2xx
        // "confirms the command was accepted and applied — it is NOT the saga's signal to advance. The
        // saga advances on the engine's resulting event (DepositConstituted), consumed via the
        // orchestrator's event-consume loop (bd babelstone-t7o3.2)." So an Applied ActivateDeposit flips
        // the saga_outbox row PUBLISHED (delivery confirmed) but synthesizes NO result event here — the
        // saga walks APPROVED → COMPLETED only when the real ProcessConstituted (engine DepositConstituted)
        // event arrives on deposits.process.events via SagaConsumeLoop (the path SagaConsumeTopics names).
        // Bridging it here would be a SECOND, contradicting advance producer for the (Approved,
        // ProcessConstituted) → Completed transition — exactly what slot 2 forbids. There is NO ACL
        // synthesis shortcut for the engine leg: the engine relays DepositConstituted on the bus for real.

        // --- Post-debit compensation trigger (the headline) ---------------------------------------
        // The engine REFUSED the activation AFTER the debit confirmed (Document 05 Scenario B): the
        // late-failure trigger that drives APPROVED → COMPENSATE_POST_DEBIT → ReverseCoreDebit, so the
        // customer's already-debited money auto-reverses.
        (ConstitutionProcess.ActivateDeposit, CommandDeliveryKind.Refused) => ConstitutionProcess.ActivationFailed,

        // --- Compensation legs (2xx Applied → the reversal completed) ------------------------------
        // The reversible Core hold was released — early compensation done (Document 05 Scenario A).
        (ConstitutionProcess.ReleaseBalanceReservation, CommandDeliveryKind.Applied) => ConstitutionProcess.ReservationReleased,
        // The committed debit was reversed by a compensating credit — late compensation done, money
        // returned (Document 05 Scenario B): drives COMPENSATE_POST_DEBIT → CANCELLED_AFTER_DEBIT.
        (ConstitutionProcess.ReverseCoreDebit, CommandDeliveryKind.Applied) => ConstitutionProcess.DebitReversed,

        // --- A compensation that itself FAILED (4xx Refused) escalates -----------------------------
        // Either reversal leg refused → the ACL could not complete the compensation: escalate to
        // HUMAN_INTERVENTION_REQUIRED, never a swallowed failure (ADR-IC-003 §P6).
        (ConstitutionProcess.ReleaseBalanceReservation, CommandDeliveryKind.Refused) => ConstitutionProcess.CompensationFailed,
        (ConstitutionProcess.ReverseCoreDebit, CommandDeliveryKind.Refused) => ConstitutionProcess.CompensationFailed,

        // --- ValidateProductLimits auto-pass [REVIEW-FLAG A] --------------------------------------
        // ValidateProductLimits has NO HTTP route (it is an in-aggregate validation; the router
        // returns null for it). At v1 it AUTO-PASSES to LimitsValidated so the parallel-validation join
        // completes and the happy path reaches COMPLETED. The dispatcher invokes this with a SYNTHETIC
        // Applied for the no-route ValidateProductLimits carve-out (every OTHER no-route command stays
        // terminal FAILED). The real product-limits verdict — including LimitsRejected — is H.2 /
        // babelstone-n55u. There is NO real HTTP "Applied" here; the carve-out names it Applied to reuse
        // this one mapping.
        (ConstitutionProcess.ValidateProductLimits, CommandDeliveryKind.Applied) => ConstitutionProcess.LimitsValidated,

        // --- Reserve refusal [REVIEW-FLAG B] ------------------------------------------------------
        // A 422 InsufficientBalance on the reserve means NO hold was placed, so there is nothing to
        // release: fail-CLOSED to PreconditionRefused → DEPOSIT_CONSTITUTION_FAILED. The DIRECTLY
        // governing decision is ADR-PC-016 §70/§127 ("InsufficientBalance from the Core compensates and
        // emits DepositConstitutionFailed with failure_reason: INSUFFICIENT_FUNDS") — a settlement
        // delivery outcome, not an in-engine precondition verdict. ADR-PC-024 §5 ("the deposit is never
        // constituted, so there is nothing to unwind") is cited only for the analogous NO-OP-TERMINAL
        // SHAPE: a refused reserve placed no hold, so this terminal genuinely has nothing to compensate
        // (the §P5 reversibility ordering holds — the refusal lands during the reversible validation
        // phase, before any irreversible effect). NOTE: the v1 bridge synthesizes only the event TYPE;
        // the §127 failure_reason taxonomy (INSUFFICIENT_FUNDS) is H.2's verdict (bd babelstone-n55u),
        // NOT carried here — a future reader must not assume the v1 terminal already satisfies §127's
        // reason contract.
        (ConstitutionProcess.ReserveAccountBalance, CommandDeliveryKind.Refused) => ConstitutionProcess.PreconditionRefused,

        // --- Scenario C: indeterminate Core debit clearance (bd babelstone-t7o3.10) ----------------
        // The ConfirmDebit returned INDETERMINATE (the ACL accepted the debit but the network dropped
        // before it could confirm execution — Document 05 Scenario C, signalled as HTTP 202). The saga
        // must NOT blind-retry (it could double-debit): synthesize CoreDebitIndeterminate to park it in
        // AWAIT_CORE_CLEARANCE, which arms the clearance QUERY (ADR-IC-003 §P4, a first-class wait).
        (ConstitutionProcess.ConfirmDebit, CommandDeliveryKind.Indeterminate) => ConstitutionProcess.CoreDebitIndeterminate,

        // The clearance QUERY resolved the indeterminate debit. The v1 ACL stub answers the clearance
        // POST with the outcome encoded as the HTTP status (DEF-1's real ACL will emit typed clearance
        // events instead — see [REVIEW-FLAG C]):
        //   • 2xx → the debit DID execute: a LATE DebitConfirmed resumes the happy path (back to APPROVED,
        //     arming ActivateDeposit), identical to a timely confirm.
        (ConstitutionProcess.QueryCoreDebitStatus, CommandDeliveryKind.Applied) => ConstitutionProcess.DebitConfirmed,
        //   • [REVIEW-FLAG C] 4xx → the debit did NOT execute: DebitNotExecuted fails the saga CLOSED
        //     (no money moved → no reversal). DIVERGENCE (ADR-IC-003 Amendment A6, 2026-06-14):
        //     ADR-IC-012 §D5/§P5 (inherited by ADR-PC-016 §64) decide the steady-state disposition as
        //     RETRY_PERMITTED (reissue with the same idempotency_key); v1 has no reissue producer, so the
        //     saga fails closed instead — DEF-1 (babelstone-ub9s) restores RETRY_PERMITTED.
        //     The 4xx=not-executed mapping is a v1 STUB CONVENTION: the
        //     dispatcher's slot-5 model classifies a 4xx as a terminal Refused, and the clearance stub
        //     reuses that to mean "the queried debit was not found / not executed". This is NOT the
        //     slot-5 "the engine refused an illegal/invalid command" semantics — it is a clearance VERDICT
        //     piggybacked on the HTTP status because the v1 ACL has no event channel. When DEF-1 lands, the
        //     real ACL emits a typed DebitNotExecuted clearance event off its own topic and this mapping is
        //     removed (recorded in the ADR-IC-003 Amendment 2026-06-14, extended for Scenario C).
        (ConstitutionProcess.QueryCoreDebitStatus, CommandDeliveryKind.Refused) => ConstitutionProcess.DebitNotExecuted,

        // Everything else drives no advance — a graceful no-op (the command WAS delivered; the bridge
        // simply synthesizes no correlation signal for it). NB: CommandDeliveryKind.Indeterminate is only
        // meaningful for ConfirmDebit; for any other command it falls through here to null.
        _ => null,
    };
}
