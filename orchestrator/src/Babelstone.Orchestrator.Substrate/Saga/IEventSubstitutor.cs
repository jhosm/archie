using Npgsql;

namespace Babelstone.Orchestrator.Saga;

/// <summary>
/// Optional machine hook (ADR-IC-018 §P6): a saga's <see cref="ISagaStateMachine"/> MAY implement this
/// to substitute the EFFECTIVE event type before the substrate's transition-table lookup. The advance
/// handler calls it on every advance for a machine that implements it, passing the saga's current state,
/// the incoming event type, and async access to the transition log (so the hook can count prior entries
/// into a state). Most events return unchanged; a budget/rule may substitute a different event.
/// </summary>
/// <remarks>
/// This generalises the <c>saga.SagaType == ConstitutionProcess.Type</c> guard the multi-saga substrate
/// (bd babelstone-mtto PR1) carried in <c>SagaAdvanceHandler</c> for the constitution reissue-budget
/// substitution — the substrate now carries NO family branch (ADR-IC-018 §D2/§P6); the family's machine
/// owns the substitution decision. Pure-decision/impure-shell discipline is preserved: the COUNT is
/// impure (the hook reads the log on the caller's transaction), the DECISION is pure (the count →
/// substituted-event map), so the table stays the authority on what each event does (ADR-IC-003 §P2).
/// </remarks>
public interface IEventSubstitutor
{
    /// <summary>
    /// Given the saga's current state, the incoming event type, and async access to the transition log,
    /// return the EFFECTIVE event type to apply — usually <paramref name="incomingEventType"/> itself,
    /// or a substituted event when a budget/rule triggers. Runs on the caller's
    /// connection/transaction (under the advance's <c>FOR UPDATE</c> row lock).
    /// </summary>
    Task<string> SubstituteAsync(
        string currentState,
        string incomingEventType,
        SagaTransitionLog transitionLog,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        CancellationToken ct);
}
