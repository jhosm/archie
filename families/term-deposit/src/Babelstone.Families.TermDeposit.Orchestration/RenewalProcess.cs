using Babelstone.Orchestrator.Saga;

namespace Babelstone.Families.TermDeposit.Orchestration;

/// <summary>
/// The <c>RenewalProcess</c> saga state machine (ADR-IC-003 §Context; 02 §2.4.4 "Automatic renewal";
/// bd babelstone-mtto). The transition table below IS the specification (ADR-IC-003 §P2): every legal
/// advance the renewal saga can make, as explicit <c>(from_state, event_type) → (next_state, commands)</c>
/// rows. Anything not in the table is rejected, so an illegal transition is impossible by construction.
/// </summary>
/// <remarks>
/// <para>
/// <b>The renewal saga is the SECOND saga on the family-agnostic substrate (ADR-IC-018 §Consequences).</b>
/// It is <see cref="SagaStartMode.EventAutoStarted"/>: no edge call starts it — it is BORN when the
/// engine's <c>DepositMatured</c> fact arrives on the <c>term_deposit</c> family integration topic with
/// a non-NONE <c>ce_autorenewalpolicy</c> header (the auto-start rule on <see cref="RenewalSagaModule"/>).
/// It runs in its OWN Kafka consumer group over the SAME family topic the constitution saga reads — a new
/// module, zero substrate diff.
/// </para>
/// <para>
/// <b>The two forward legs are the idempotent engine endpoints PR B shipped.</b> <c>ConstituteRenewal</c>
/// opens the new stream (the engine's <c>POST /v1/deposits/{closing_id}/constitute-renewal</c>);
/// <c>LinkRenewal</c> folds the closing stream Matured → Renewed (<c>POST
/// /v1/deposits/{closing_id}/renewal-link</c>), where <c>{closing_id}</c> = the saga's process_id. The
/// saga consumes the <c>term_deposit</c> topic ONLY for the <c>DepositMatured</c> START trigger; the two
/// forward advances (<see cref="NewDepositConstituted"/>, <see cref="RenewalLinkConfirmed"/>) are
/// INTERNAL signals the result-event bridge SYNTHESIZES from each command's HTTP 2xx and feeds back
/// in-process (<see cref="RenewalResultEvents"/>) — they are NOT bus events. They MUST be synthesized,
/// not read off the bus: the engine's <c>DepositConstituted</c> for the new stream carries
/// <c>ce_subject = newDepositId ≠ process_id</c>, so it cannot correlate to this saga off the bus — exactly
/// why the constitution saga synthesizes its ACL legs from command outcomes (ADR-PC-029 slot 2 reasoning,
/// applied here to the new-stream fact whose subject differs from the saga).
/// </para>
/// <para>
/// <b>Failures NEVER compensate (ADR-IC-003 §P6).</b> The payout already moved at maturity (the autonomous
/// maturity leg ran before the saga). A refused <c>ConstituteRenewal</c> / <c>LinkRenewal</c>, or an
/// explicit <see cref="RenewalEscalated"/>, lands the saga in HUMAN_INTERVENTION_REQUIRED — an operator
/// reconciles; the saga emits NO reversal command. HIR is NON-terminal BY TABLE: the
/// <c>(HUMAN_INTERVENTION_REQUIRED, OperatorResolved) → RENEWAL_COMPLETED</c> edge exists, so the substrate
/// default <see cref="TableStateMachine.IsTerminal"/> already reports HIR non-terminal — no override needed
/// (unlike <c>ConstitutionProcess</c>, whose resolution edge does not yet exist).
/// </para>
/// <para>
/// <b>Crash recovery (§P6).</b> The saga PG state coordinates the cross-stream sequence; a crash between
/// <c>ConstituteRenewal</c> and <c>LinkRenewal</c> resumes from the persisted state and re-issues the
/// idempotent command (PR B's endpoints dedup on the <c>saga_outbox</c> row id as the Idempotency-Key),
/// and the new deposit id is DETERMINISTICALLY derived from the process id so the reissue targets the same
/// stream (<see cref="RenewalCommandPayloadFactory"/>).
/// </para>
/// </remarks>
public sealed partial class RenewalProcess : TableStateMachine
{
    /// <summary>The persisted <c>saga_type</c> discriminator for renewal sagas.</summary>
    public const string Type = "RenewalProcess";

    // --- Triggering events. Event TYPE names only — the substrate keys the table on the type, never a
    // (PII-free) payload (ADR-IC-003 §P2). ------------------------------------------------------------

    /// <summary>Engine integration fact (off the <c>term_deposit</c> topic): the closing deposit matured.
    /// The START trigger — the only event the renewal saga consumes off the bus (ADR-IC-018 §P5). The
    /// auto-start rule additionally gates on the <c>ce_autorenewalpolicy</c> header being non-NONE.</summary>
    public const string DepositMatured = "DepositMatured";

    /// <summary>INTERNAL synthesized signal (NOT a bus event): the <c>ConstituteRenewal</c> command's HTTP
    /// 2xx opened the new stream. Synthesized by <see cref="RenewalResultEvents"/> from the command outcome
    /// and fed back in-process — the new stream's real <c>DepositConstituted</c> bus fact carries a
    /// different <c>ce_subject</c> (the new deposit id) and so cannot correlate to this saga off the bus.</summary>
    public const string NewDepositConstituted = "NewDepositConstituted";

    /// <summary>INTERNAL synthesized signal (NOT a bus event): the <c>LinkRenewal</c> command's HTTP 2xx
    /// folded the closing stream to Renewed. Synthesized from the command outcome; closes the saga.</summary>
    public const string RenewalLinkConfirmed = "RenewalLinkConfirmed";

    /// <summary>The engine REFUSED <c>ConstituteRenewal</c> (4xx — e.g. closing deposit not Matured, an
    /// unpriced renewal rate). Escalates to HIR; no compensation (the rollover has not opened, but money
    /// already moved at maturity — §P6 says escalate, never strand).</summary>
    public const string ConstituteRenewalFailed = "ConstituteRenewalFailed";

    /// <summary>The engine REFUSED <c>LinkRenewal</c> (4xx). Escalates to HIR — the new stream is already
    /// open and the payout already moved, so this is NEVER compensated (ADR-IC-003 §P6); an operator
    /// reconciles the dangling new stream.</summary>
    public const string LinkRenewalFailed = "LinkRenewalFailed";

    /// <summary>An explicit operational escalation reachable from any non-terminal forward state — a
    /// catch-all that parks the saga in HIR for manual reconciliation (e.g. an ops-driven hold). DISTINCT
    /// from the per-leg failures so the transition log records exactly WHY the saga escalated.</summary>
    public const string RenewalEscalated = "RenewalEscalated";

    /// <summary>An operator resolved a HIR-parked renewal saga (reconciled the dangling stream / forced
    /// completion). Drives HIR → RENEWAL_COMPLETED — the edge that makes HIR NON-terminal BY TABLE.</summary>
    public const string OperatorResolved = "OperatorResolved";

    // --- Commands the saga emits (ADR-IC-003 §P1). Names are the contract the outbox seam dispatches; the
    // concrete payloads are RenewalCommandPayloadFactory's, keyed on these names. Public so the payload
    // factory, the router, and the unit test reference the SAME constant, never a re-typed literal. ----

    /// <summary>Engine: open the renewed instance off the Matured closing deposit — the engine's
    /// idempotent <c>POST /v1/deposits/{process_id}/constitute-renewal</c> leg (PR B).</summary>
    public const string ConstituteRenewal = "ConstituteRenewal";

    /// <summary>Engine: link the renewal, folding the closing stream Matured → Renewed — the engine's
    /// idempotent <c>POST /v1/deposits/{process_id}/renewal-link</c> leg (PR B).</summary>
    public const string LinkRenewal = "LinkRenewal";

    public RenewalProcess()
        : base(Type, States.RenewalStarted, BuildTable())
    {
    }

    // No IsTerminal override (UNLIKE ConstitutionProcess): HIR has an outgoing OperatorResolved edge in
    // BuildTable, so the substrate default TableStateMachine.IsTerminal (no-outgoing-edge inspection)
    // already reports HIR NON-terminal and RENEWAL_COMPLETED terminal — the table and the default agree.

    private static IEnumerable<((string, string), TransitionOutcome)> BuildTable()
    {
        // --- Happy path (02 §2.4.4) ----------------------------------------------------------------
        // Auto-started on DepositMatured → emit ConstituteRenewal (open the new stream).
        yield return ((States.RenewalStarted, DepositMatured),
            TransitionOutcome.To(States.RenewalConstituting, ConstituteRenewal));
        // The constitute leg's 2xx (synthesized NewDepositConstituted) → emit LinkRenewal (fold the close).
        yield return ((States.RenewalConstituting, NewDepositConstituted),
            TransitionOutcome.To(States.RenewalLinking, LinkRenewal));
        // The link leg's 2xx (synthesized RenewalLinkConfirmed) → done (terminal, no commands).
        yield return ((States.RenewalLinking, RenewalLinkConfirmed),
            TransitionOutcome.To(States.RenewalCompleted));

        // --- Failure → HIR (ADR-IC-003 §P6: NEVER compensate after the maturity payout moved) -------
        // A refused leg, or an explicit escalation, parks the saga in HIR with NO reversal command.
        yield return ((States.RenewalConstituting, ConstituteRenewalFailed),
            TransitionOutcome.To(States.HumanInterventionRequired));
        yield return ((States.RenewalLinking, LinkRenewalFailed),
            TransitionOutcome.To(States.HumanInterventionRequired));
        // RenewalEscalated is reachable from EVERY non-terminal forward state (the catch-all hold).
        yield return ((States.RenewalStarted, RenewalEscalated),
            TransitionOutcome.To(States.HumanInterventionRequired));
        yield return ((States.RenewalConstituting, RenewalEscalated),
            TransitionOutcome.To(States.HumanInterventionRequired));
        yield return ((States.RenewalLinking, RenewalEscalated),
            TransitionOutcome.To(States.HumanInterventionRequired));

        // --- HIR → resolved (the edge that makes HIR NON-terminal BY TABLE) --------------------------
        yield return ((States.HumanInterventionRequired, OperatorResolved),
            TransitionOutcome.To(States.RenewalCompleted));
    }
}
