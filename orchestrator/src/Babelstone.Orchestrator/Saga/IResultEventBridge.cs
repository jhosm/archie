namespace Babelstone.Orchestrator.Saga;

/// <summary>
/// The command-outcome → result-event mapping seam for ONE saga type (bd babelstone-mtto PR1 —
/// the multi-saga substrate). When the dispatcher flips a <c>saga_outbox</c> row to its terminal
/// status, it asks the bridge keyed by the row's <c>saga_type</c> which result-event type the
/// saga should self-advance on, or <c>null</c> when the outcome drives no advance. Generalises
/// the single hardcoded <see cref="ConstitutionResultEvents"/> call into a registry the dispatcher
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
}
