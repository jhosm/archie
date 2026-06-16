using System.Diagnostics;
using Babelstone.Orchestrator.Inbox;
using Npgsql;

namespace Babelstone.Orchestrator.Saga;

/// <summary>
/// Optional machine hook (ADR-IC-018 §P6): a saga's <see cref="ISagaStateMachine"/> MAY implement this
/// to run additional in-transaction logic AFTER a state transition is applied (but before the
/// transaction commits). The advance handler calls it on every accepted advance for a machine that
/// implements it; the implementation may self-advance the saga further within the SAME
/// connection/transaction (nothing rides the durable bus).
/// </summary>
/// <remarks>
/// This generalises the <c>saga.SagaType == ConstitutionProcess.Type</c> guard the multi-saga substrate
/// (bd babelstone-mtto PR1) carried in <c>SagaAdvanceHandler</c> for the constitution approval-fork
/// self-emit (the saga reaching VALIDATIONS_COMPLETE decides auto-approve vs route-to-workflow and
/// self-emits the chosen event). The substrate now carries NO family branch (ADR-IC-018 §D2/§P6); the
/// family's machine owns the post-advance decision and checks the landed state itself. The substrate
/// hands the hook the substrate ports it needs (the stores, the command sink) so the self-advance reuses
/// the SAME pure machine + persistence path the external advance uses (auditable from the table, §P2).
/// </remarks>
public interface IPostAdvanceHook
{
    /// <summary>
    /// Called after the state move to <paramref name="newState"/> is persisted (but before commit). The
    /// implementation may self-advance the saga further within the same connection/transaction. A
    /// concurrency loss surfaces as <see cref="SagaConcurrencyException"/>, which the caller propagates.
    /// </summary>
    /// <param name="machine">The routed state machine (this saga's own), so the hook self-advances
    /// through the SAME pure table the external advance used.</param>
    Task OnAdvancedAsync(
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
        Inbox.SagaInboxWriter inboxWriter,
        CancellationToken ct);
}
