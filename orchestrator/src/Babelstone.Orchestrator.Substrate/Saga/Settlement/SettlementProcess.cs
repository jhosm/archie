using Npgsql;

namespace Babelstone.Orchestrator.Saga.Settlement;

/// <summary>
/// The substrate-owned, FAMILY-AGNOSTIC <c>settlement</c> saga (ADR-PC-032 slot 5 / feature-design
/// money-movement-settlement §4). In plain English: this is the ONE place that actually moves the cash
/// once any family records a money <c>Movement</c>. A family's money-moving event records its
/// <c>Movement</c>(s) append-first; this saga is auto-started by that event and effects the cash leg,
/// gated, parking on failure — so no family ever hand-codes a settlement leg again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it lives in the substrate (ADR-IC-018 Amendment 2026-06-24).</b> Every OTHER saga
/// (<c>ConstitutionProcess</c>, <c>RenewalProcess</c>) names a product family and lives in its family
/// <c>.Orchestration</c> module. This saga names NO family: it keys only on the ADR-PC-032 <c>Movement</c>
/// atom's generic <c>direction</c> / opaque <c>account_ref</c> / generic settlement-command vocabulary, so
/// it is the saga-level analog of a substrate store — the one shared home the design mandates, not a
/// per-family copy. The narrowed ORCH-2 gate allow-lists exactly this family-agnostic case.
/// </para>
/// <para>
/// <b>Parameterised by DIRECTION (the gating asymmetry of ADR-PC-016 slot 5).</b> The saga is
/// event-auto-started (ADR-IC-018 §P5) on a <c>Movement</c>-bearing event whose promoted
/// <c>ce_movementdirections</c> / <c>ce_movementorigin</c> CloudEvents headers the substrate reads — never
/// the payload. The single start event resolves, via <see cref="IEventSubstitutor"/> on the start
/// headers (this leg's single-entry <c>movementdirections</c>), to a direction-specific effective event:
/// <list type="bullet">
///   <item><b><c>Debit</c></b> → FUNDS-GATED: <c>ReserveAccountBalance</c> (the reversible hold) then
///   <c>ConfirmDebit</c> (the irreversible debit). A refused reserve (<c>InsufficientBalance</c>) parks;
///   an indeterminate confirm enters debit clearance (ADR-IC-012 §P5).</item>
///   <item><b><c>Credit</c></b> → CONFIRMATION-GATED ONLY: a single <c>ConfirmCredit</c> (the legacy Core
///   always accepts a credit, but it must confirm for reconciliation flow 1, ADR-PC-016 slot 5). A
///   non-confirm enters credit clearance — NEVER silent.</item>
/// </list>
/// </para>
/// <para>
/// <b>Multi-<c>Movement</c> events + per-account FIFO.</b> A renewal event carries a rollover-debit AND an
/// interest-credit <c>Movement</c>. Each is effected by its OWN settlement instance gated by its own
/// direction; per-account ordering rides the dispatcher's per-<c>process_id</c> FIFO (ADR-IC-004 outbox
/// per-aggregate order; bd babelstone-t7o3.7), preserved end-to-end here.
/// </para>
/// <para>
/// <b>Unrecoverable failures PARK, never compensate (ADR-IC-003 §P6).</b> The fact is durable append-first
/// (ADR-PC-032 slot 5 — recording the <c>Movement</c> is part of the local append and cannot be gated); the
/// cash leg is a downstream consequence. If it cannot be effected, the saga lands in
/// HUMAN_INTERVENTION_REQUIRED with NO reversal command — the money either moved or did not; the saga never
/// invents an undo. HIR is reachable from every in-flight state; its operator-resolution edge keeps it
/// NON-terminal by table (no <see cref="TableStateMachine.IsTerminal"/> override needed, unlike
/// <c>ConstitutionProcess</c>).
/// </para>
/// </remarks>
public sealed partial class SettlementProcess : TableStateMachine, IEventSubstitutor
{
    /// <summary>The persisted <c>saga_type</c> discriminator for settlement sagas.</summary>
    public const string Type = "SettlementProcess";

    // --- Triggering events. Event TYPE names only — the substrate keys the table on the type, never the
    // (PII-free) payload (ADR-IC-003 §P2). The settlement saga's result events are SYNTHESIZED from each
    // settlement command's delivery outcome and fed back in-process (the SAME bridge pattern as the
    // constitution/renewal sagas; at v1 the ACL is a WireMock stub with no event producer). ------------

    /// <summary>The GENERIC start event the substrate auto-starts the saga on — a <c>Movement</c>-bearing
    /// family event, admitted by the module's header predicate (<c>movementorigin == Originated</c>). The
    /// <see cref="SubstituteAsync"/> hook resolves it to a direction-specific effective event from the
    /// leg's single-entry <c>ce_movementdirections</c> list, so the table branches by direction WITHOUT the
    /// substrate reading the payload (ADR-IC-018 §D5). It is the <c>ce_type</c> the engine relay stamps as the
    /// generic money-movement start marker; a family event with no Originated movement never matches.</summary>
    public const string MovementOriginated = "MovementOriginated";

    /// <summary>Effective start event (substituted from <see cref="MovementOriginated"/> when the promoted
    /// direction is <c>Debit</c>): a debit <c>Movement</c> is funds-gated, so the saga emits the reversible
    /// hold then the irreversible debit.</summary>
    public const string DebitMovementOriginated = "DebitMovementOriginated";

    /// <summary>Effective start event (substituted when the promoted direction is <c>Credit</c>): a credit
    /// <c>Movement</c> is confirmation-gated only, so the saga emits the single confirm.</summary>
    public const string CreditMovementOriginated = "CreditMovementOriginated";

    /// <summary>Core ACL: the reversible balance hold succeeded (the debit path's first leg).</summary>
    public const string BalanceReserved = "BalanceReserved";

    /// <summary>Core ACL: the reversible hold could not be placed — refused (e.g. InsufficientBalance). No
    /// hold exists, so there is nothing to release: park fail-closed in HUMAN_INTERVENTION_REQUIRED
    /// (ADR-IC-003 §P6 — no compensation; the cash never moved).</summary>
    public const string ReserveRefused = "ReserveRefused";

    /// <summary>Core ACL: the hold became a real debit — the debit leg cleared (the happy terminal of the
    /// debit path).</summary>
    public const string DebitConfirmed = "DebitConfirmed";

    /// <summary>Core ACL: the credit was confirmed — the credit leg cleared (the happy terminal of the
    /// credit path). The legacy Core always accepts a credit, but it must confirm for reconciliation
    /// flow 1 (ADR-PC-016 slot 5).</summary>
    public const string CreditConfirmed = "CreditConfirmed";

    /// <summary>Core ACL: the ConfirmDebit returned INDETERMINATE (HTTP 202) — the network dropped after
    /// the debit was sent, so the ACL cannot yet confirm whether the Core executed it (ADR-IC-012 §P5). The
    /// saga must NOT blind-retry (it could double-debit); it parks in the first-class wait
    /// AWAIT_DEBIT_CLEARANCE and emits the clearance query (ADR-IC-003 §P4).</summary>
    public const string DebitIndeterminate = "DebitIndeterminate";

    /// <summary>Core ACL: the ConfirmCredit returned INDETERMINATE (HTTP 202) — the credit was sent but its
    /// execution is unconfirmed. The credit path's indeterminate-clearance trigger (the NEW credit surface
    /// ADR-PC-032 / feature-design §10 flags): a non-confirmed credit enters clearance, NEVER silent. Parks
    /// in AWAIT_CREDIT_CLEARANCE and emits the credit clearance query (ADR-IC-003 §P4).</summary>
    public const string CreditIndeterminate = "CreditIndeterminate";

    /// <summary>Core ACL clearance: the indeterminate DEBIT was resolved as EXECUTED — the debit DID land
    /// (a late <see cref="DebitConfirmed"/>). Resumes the debit happy terminal.</summary>
    public const string DebitClearedExecuted = "DebitClearedExecuted";

    /// <summary>Core ACL clearance: the indeterminate DEBIT was resolved as NOT executed — no money moved.
    /// Reissues the debit (the RETRY_PERMITTED disposition; ADR-IC-012 §D5 step 5 / §P5). The reissue cannot
    /// double-debit — not-executed is Core ground truth that nothing was committed (§P5/§332).</summary>
    public const string DebitClearedNotExecuted = "DebitClearedNotExecuted";

    /// <summary>Core ACL clearance: the indeterminate CREDIT was resolved as EXECUTED — the credit DID land
    /// (a late <see cref="CreditConfirmed"/>). Resumes the credit happy terminal.</summary>
    public const string CreditClearedExecuted = "CreditClearedExecuted";

    /// <summary>Core ACL clearance: the indeterminate CREDIT was resolved as NOT executed — no money moved.
    /// Reissues the credit (the RETRY_PERMITTED disposition). The reissue cannot double-credit — not-executed
    /// is Core ground truth.</summary>
    public const string CreditClearedNotExecuted = "CreditClearedNotExecuted";

    /// <summary>A clearance query that itself could not resolve, or any other unrecoverable settlement
    /// failure: park in HUMAN_INTERVENTION_REQUIRED, never strand (ADR-IC-003 §P6). Reachable from the two
    /// clearance waits.</summary>
    public const string ClearanceFailed = "ClearanceFailed";

    /// <summary>An operator resolved a HIR-parked settlement saga (reconciled the leg manually). Drives HIR
    /// → SETTLEMENT_COMPLETED — the edge that makes HIR NON-terminal BY TABLE.</summary>
    public const string OperatorResolved = "OperatorResolved";

    // --- Commands the saga emits (ADR-IC-003 §P1). The account-generic settlement command vocabulary
    // RELOCATED from the term-deposit ConstitutionProcess into this substrate home (feature-design §8), plus
    // the NEW generic ConfirmCredit + credit-clearance commands. Names only — the concrete payloads are the
    // SettlementCommandPayloadFactory's, keyed on these constants. Public so the factory, the router, and
    // the tests reference the SAME constant, never a re-typed literal. ----------------------------------

    /// <summary>Core ACL: place the reversible balance hold (the debit path's reversible leg).</summary>
    public const string ReserveAccountBalance = "ReserveAccountBalance";

    /// <summary>Core ACL: convert the hold into a real debit — the IRREVERSIBLE debit leg.</summary>
    public const string ConfirmDebit = "ConfirmDebit";

    /// <summary>Core ACL: confirm the credit — the IRREVERSIBLE, confirmation-gated credit leg (the NEW
    /// generic credit command ADR-PC-032 / feature-design §8 adds; today only debit commands existed).</summary>
    public const string ConfirmCredit = "ConfirmCredit";

    /// <summary>Core ACL: query the Core for the actual outcome of an INDETERMINATE debit — the v1
    /// clearance mechanism (ADR-IC-012 §P5; a single event-driven query, never a poll loop).</summary>
    public const string QueryCoreDebitStatus = "QueryCoreDebitStatus";

    /// <summary>Core ACL: query the Core for the actual outcome of an INDETERMINATE credit — the credit
    /// counterpart of <see cref="QueryCoreDebitStatus"/> (the new credit-clearance surface).</summary>
    public const string QueryCoreCreditStatus = "QueryCoreCreditStatus";

    public SettlementProcess()
        : base(Type, States.SettlementStarted, BuildTable())
    {
    }

    // No IsTerminal override: HIR has an outgoing OperatorResolved edge in BuildTable, so the substrate
    // default TableStateMachine.IsTerminal (no-outgoing-edge inspection) already reports HIR NON-terminal
    // and SETTLEMENT_COMPLETED terminal — the table and the default agree (the RenewalProcess posture, not
    // ConstitutionProcess's override-needed one).

    private static IEnumerable<((string, string), TransitionOutcome)> BuildTable()
    {
        // === DEBIT path — funds-gated: Reserve -> Confirm (ADR-PC-016 slot 5) =======================
        // Auto-started on a debit Movement -> emit the reversible hold.
        yield return ((States.SettlementStarted, DebitMovementOriginated),
            TransitionOutcome.To(States.Reserving, ReserveAccountBalance));
        // The reversible hold succeeded -> emit the irreversible debit (the §P5 reversible-first ordering).
        yield return ((States.Reserving, BalanceReserved),
            TransitionOutcome.To(States.ConfirmingDebit, ConfirmDebit));
        // The hold could NOT be placed (refused) -> park: no hold exists, nothing to release (§P6 no-op).
        yield return ((States.Reserving, ReserveRefused),
            TransitionOutcome.To(States.HumanInterventionRequired));
        // The irreversible debit cleared -> done.
        yield return ((States.ConfirmingDebit, DebitConfirmed),
            TransitionOutcome.To(States.SettlementCompleted));
        // The debit returned INDETERMINATE -> park in the first-class wait + emit the clearance query.
        yield return ((States.ConfirmingDebit, DebitIndeterminate),
            TransitionOutcome.To(States.AwaitDebitClearance, QueryCoreDebitStatus));
        // Clearance EXECUTED -> the debit DID land (late confirm) -> done.
        yield return ((States.AwaitDebitClearance, DebitClearedExecuted),
            TransitionOutcome.To(States.SettlementCompleted));
        // Clearance NOT executed -> RETRY_PERMITTED reissue of the debit (same idempotency key; cannot
        // double-debit — not-executed is Core ground truth, ADR-IC-012 §P5/§332).
        yield return ((States.AwaitDebitClearance, DebitClearedNotExecuted),
            TransitionOutcome.To(States.ConfirmingDebit, ConfirmDebit));
        // A clearance that itself cannot resolve -> escalate (never strand, §P6).
        yield return ((States.AwaitDebitClearance, ClearanceFailed),
            TransitionOutcome.To(States.HumanInterventionRequired));

        // === CREDIT path — confirmation-gated only: a single Confirm (ADR-PC-016 slot 5) ============
        // Auto-started on a credit Movement -> emit the single confirm (no reserve leg — a credit is always
        // accepted by the legacy Core, but it must confirm for reconciliation flow 1).
        yield return ((States.SettlementStarted, CreditMovementOriginated),
            TransitionOutcome.To(States.ConfirmingCredit, ConfirmCredit));
        // The credit confirmed -> done.
        yield return ((States.ConfirmingCredit, CreditConfirmed),
            TransitionOutcome.To(States.SettlementCompleted));
        // The credit returned INDETERMINATE -> park + emit the credit clearance query (NEVER silent — the
        // new credit-clearance surface, feature-design §10).
        yield return ((States.ConfirmingCredit, CreditIndeterminate),
            TransitionOutcome.To(States.AwaitCreditClearance, QueryCoreCreditStatus));
        // Credit clearance EXECUTED -> the credit DID land (late confirm) -> done.
        yield return ((States.AwaitCreditClearance, CreditClearedExecuted),
            TransitionOutcome.To(States.SettlementCompleted));
        // Credit clearance NOT executed -> RETRY_PERMITTED reissue of the credit.
        yield return ((States.AwaitCreditClearance, CreditClearedNotExecuted),
            TransitionOutcome.To(States.ConfirmingCredit, ConfirmCredit));
        yield return ((States.AwaitCreditClearance, ClearanceFailed),
            TransitionOutcome.To(States.HumanInterventionRequired));

        // === HIR -> resolved (the edge that makes HIR NON-terminal BY TABLE) =========================
        yield return ((States.HumanInterventionRequired, OperatorResolved),
            TransitionOutcome.To(States.SettlementCompleted));
    }

    /// <summary>
    /// <see cref="IEventSubstitutor"/> — resolve the GENERIC <see cref="MovementOriginated"/> start event to
    /// a direction-specific effective event from the promoted <c>movementdirections</c> CloudEvents header
    /// (ADR-IC-018 §D5 — the substrate reads headers, never the payload). This is how the saga parameterises
    /// by direction WITHOUT a payload read: by the time the table sees a Movement-bearing event the fan-out has
    /// reduced it to a SINGLE-direction leg, so its <c>movementdirections</c> list carries exactly one entry
    /// (Debit | Credit), and this PURE map (the header → effective-event function) picks the debit or credit
    /// branch the table then drives. For every other (state, event) — and for a start event whose
    /// <c>movementdirections</c> is absent/unknown or (not-yet-fanned-out) carries MORE THAN ONE entry — the
    /// incoming event is returned unchanged (the table then rejects an unstartable event as NoTransition,
    /// fail-closed, never a guessed direction).
    /// </summary>
    /// <remarks>
    /// The COUNT-style impurity the constitution substitutor has (reading the transition log) is absent
    /// here — this substitution is a pure function of the incoming event type and the start headers, with no
    /// clock and no I/O (ADR-PC-010 §P5). The connection/transaction/log are unused; they are part of the
    /// generic hook contract.
    /// </remarks>
    public Task<string> SubstituteAsync(
        string currentState,
        string incomingEventType,
        SagaTransitionLog transitionLog,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        IReadOnlyDictionary<string, string>? extensionHeaders,
        CancellationToken ct)
    {
        if (incomingEventType != MovementOriginated)
        {
            return Task.FromResult(incomingEventType);
        }

        // The lone direction this leg's movementdirections list declares — the engine relay promoted the
        // ordered list onto the Movement-bearing event's CloudEvents headers, and the fan-out reduced this leg
        // to a single entry (lowercased, ce_-stripped by the consume loop). Debit -> the funds-gated branch;
        // Credit -> the confirmation-gated branch. Null (absent/unknown, or a not-yet-fanned-out multi-entry
        // list) returns the un-substituted start event, which has no transition out of SETTLEMENT_STARTED ->
        // NoTransition (fail-closed, never a guess).
        var direction = SettlementMovementFanout.SingleDirection(extensionHeaders);

        var effective = direction switch
        {
            DebitDirectionValue => DebitMovementOriginated,
            CreditDirectionValue => CreditMovementOriginated,
            _ => incomingEventType,
        };

        return Task.FromResult(effective);
    }

    /// <summary>The promoted direction value for a debit movement (ADR-PC-032 <c>SettlementDirection.Debit</c>
    /// — value leaves the named account). The relay promotes the enum's <c>ToString()</c> verbatim.</summary>
    private const string DebitDirectionValue = "Debit";

    /// <summary>The promoted direction value for a credit movement (ADR-PC-032
    /// <c>SettlementDirection.Credit</c> — value enters the named account).</summary>
    private const string CreditDirectionValue = "Credit";
}
