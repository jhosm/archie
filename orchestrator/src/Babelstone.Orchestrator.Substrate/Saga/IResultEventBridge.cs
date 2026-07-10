namespace Babelstone.Orchestrator.Saga;

/// <summary>
/// The command-outcome → result-event mapping seam for ONE saga type (bd babelstone-mtto PR1 —
/// the multi-saga substrate). When the dispatcher flips a <c>saga_outbox</c> row to its terminal
/// status, it asks the bridge keyed by the row's <c>saga_type</c> which result-event type the
/// saga should self-advance on, or <c>null</c> when the outcome drives no advance. Generalises
/// the single hardcoded constitution result-event call into a registry the dispatcher
/// resolves by saga type, so a second saga (the H.3 renewal saga, PR2) registers its OWN bridge
/// alongside without touching the dispatcher.
/// </summary>
/// <remarks>
/// <b>Pure (ADR-PC-010 §P5):</b> a function of the command type and the delivery kind alone — no
/// clock, no I/O, no randomness. The impure dispatcher shell owns the connection, the HTTP call,
/// and the deterministic-id derivation; the bridge only decides WHICH event, never WHEN or HOW it
/// lands. Mirrors <see cref="ISagaStateMachine"/>: one implementation per <c>saga_type</c>, selected
/// by the persisted discriminator.
/// </remarks>
public interface IResultEventBridge
{
    /// <summary>The saga type this bridge serves — matches <see cref="ISagaStateMachine.SagaType"/>
    /// and the persisted <c>saga_state.saga_type</c> discriminator.</summary>
    string SagaType { get; }

    /// <summary>Map the terminal delivery outcome of <paramref name="commandType"/> to the
    /// result-event type the saga should self-advance on, or <c>null</c> when the outcome drives no
    /// advance (an unmapped pair is a graceful no-op, never an invented transition).</summary>
    string? ForOutcome(string commandType, CommandDeliveryKind kind);

    /// <summary>
    /// Whether a command with NO HTTP route is a synthetic <see cref="CommandDeliveryKind.Applied"/>
    /// AUTO-PASS for this saga (ADR-IC-018 §P6) — flip PUBLISHED + self-advance — rather than the
    /// substrate default for a no-route command (terminal FAILED). The constitution saga uses this for
    /// its in-aggregate <c>ValidateProductLimits</c> at v1. Default <c>false</c>: a no-route command is
    /// terminal FAILED unless the saga's bridge says otherwise — the substrate names no family.
    /// </summary>
    bool IsNoRouteAutoPass(string commandType) => false;

    /// <summary>
    /// Reinterpret a delivered response's HTTP status into a saga-specific terminal
    /// <see cref="CommandDeliveryKind"/>, or <c>null</c> to fall through to the substrate's default
    /// classification (2xx → Applied, 4xx → Refused, else → transient). The constitution saga uses this
    /// to read an HTTP 202 on <c>ConfirmDebit</c> as <see cref="CommandDeliveryKind.Indeterminate"/>
    /// (Scenario C) rather than a plain 2xx Applied. The substrate names no family (ADR-IC-018 §P6); a
    /// saga that needs no reinterpretation returns null (the default).
    /// </summary>
    CommandDeliveryKind? ClassifyResponse(string commandType, int httpStatusCode) => null;

    /// <summary>
    /// Whether a delivered 4xx is a RETRIABLE non-terminal outcome for this saga — the leg must stay
    /// <c>PENDING</c> under the SAME <c>process_id</c> and be re-driven later, NOT flipped terminal
    /// (neither PUBLISHED nor FAILED). The settlement saga uses this for a <c>422 SCA_REQUIRED</c> on a
    /// cash confirm: the money never moved, so a fresh SCA proof re-drives the SAME leg rather than
    /// dropping the payout (FAILED) or double-moving it (a fresh occurrence). Reads the response
    /// <paramref name="responseBody"/> because the disposition turns on the ProblemDetails error
    /// <c>code</c>, not the status alone (ADR-PC-043). Default <c>false</c>: the substrate names no
    /// family, so a saga with no retriable-4xx carve-out treats every 4xx as the default terminal
    /// refusal. Checked BEFORE <see cref="ClassifyResponse(string, int)"/>, so a retriable 4xx never
    /// reaches the terminal classification.
    /// </summary>
    bool IsRetriableStayPending(string commandType, int httpStatusCode, string? responseBody) => false;
}
