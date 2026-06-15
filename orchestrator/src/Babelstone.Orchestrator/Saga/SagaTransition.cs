using System.Collections.Immutable;
using System.Linq;

namespace Babelstone.Orchestrator.Saga;

/// <summary>
/// The outcome of a state machine lookup: the next state plus the commands to emit
/// (ADR-IC-003 §P2 — a transition is <c>(current_state, event_type) → (next_state,
/// commands_to_emit)</c>). Pure data: the saga DECIDES the commands here (a pure fold over
/// current state + event), and the orchestrator DISPATCHES them through the outbox seam —
/// the decision carries no clock, no I/O, no randomness (ADR-PC-010 §P5).
/// </summary>
/// <param name="Next">The state the saga moves to.</param>
/// <param name="Commands">The command type names to emit on the move (ADR-IC-003 §P1
/// "the specific commands it emits"). Names only — the substrate proves the state machine;
/// the concrete command payloads are H.2/H.3's business logic. May be empty (a pure state
/// move with no fan-out, e.g. closing the saga).</param>
public readonly record struct TransitionOutcome(SagaState Next, ImmutableArray<string> Commands)
{
    /// <summary>A transition to <paramref name="next"/> that emits no commands.</summary>
    public static TransitionOutcome To(SagaState next) => new(next, ImmutableArray<string>.Empty);

    /// <summary>A transition to <paramref name="next"/> that emits the given command names.</summary>
    public static TransitionOutcome To(SagaState next, params string[] commands) =>
        new(next, [.. commands]);
}

/// <summary>
/// A hand-rolled saga state machine (ADR-IC-003 §P2 "the state machine is the
/// specification", ADR-PC-010 "no heavyweight framework that owns its own tables"). The
/// transition table is an explicit, inspectable <c>(current_state, event_type) →
/// (next_state, commands)</c> structure — not control flow buried in <c>if</c> statements
/// — so the saga's behaviour is auditable from the table alone.
/// </summary>
/// <remarks>
/// <b>Illegal transitions are impossible by construction (ADR-IC-003 §P2):</b> any
/// <c>(state, event)</c> pair NOT in the table is rejected (<see cref="TryAdvance"/>
/// returns false), never silently ignored. The saga advance handler turns that rejection
/// into a poison/error outcome rather than a no-op state move.
/// <para>
/// <b>Purity (ADR-PC-010 §P5):</b> <see cref="TryAdvance"/> is a pure function of
/// (current state, event type) — no clock, no I/O, no randomness. Time and side effects are
/// the orchestrator's concern, applied AFTER the decision.
/// </para>
/// </remarks>
public interface ISagaStateMachine
{
    /// <summary>The saga type this machine governs (e.g. <c>ConstitutionProcess</c>) — the
    /// value persisted in <c>saga_state.saga_type</c> and used to select the machine.</summary>
    string SagaType { get; }

    /// <summary>The state a freshly started saga enters. The edge creates the row in this
    /// state (Document 05 step 0); the machine treats it as the only legal start state.</summary>
    SagaState InitialState { get; }

    /// <summary>
    /// Look up the transition for <paramref name="current"/> on receiving
    /// <paramref name="eventType"/>. Returns true with the <paramref name="outcome"/> if the
    /// pair is in the table; false if it is not — an illegal transition the caller must
    /// reject, never apply (ADR-IC-003 §P2).
    /// </summary>
    bool TryAdvance(SagaState current, string eventType, out TransitionOutcome outcome);

    /// <summary>
    /// Whether <paramref name="state"/> is terminal for THIS machine — the saga accepts no
    /// further transitions (ADR-IC-003 §Context "Terminal"). Each saga type defines its OWN
    /// terminal set, so the multi-saga substrate asks the routed machine (NOT the
    /// ConstitutionProcess-scoped <see cref="SagaStateNames.IsTerminal"/> static) once it has
    /// keyed the machine by <c>saga_type</c>. A late event for a terminal saga is a no-op advance.
    /// </summary>
    bool IsTerminal(SagaState state);
}

/// <summary>
/// A table-driven <see cref="ISagaStateMachine"/>: the transition table is the entire
/// specification (ADR-IC-003 §P2). A concrete saga (the <see cref="ConstitutionProcess"/>)
/// is just this base populated with its <c>(state, event) → (next, commands)</c> rows.
/// </summary>
public abstract class TableStateMachine : ISagaStateMachine
{
    private readonly ImmutableDictionary<(SagaState, string), TransitionOutcome> _table;

    /// <param name="sagaType">The persisted <c>saga_type</c> discriminator.</param>
    /// <param name="initialState">The only legal start state.</param>
    /// <param name="table">The explicit transition table. A duplicate
    /// <c>(state, event)</c> key is a specification error and throws at construction —
    /// the table must be a function, not a multimap.</param>
    protected TableStateMachine(
        string sagaType,
        SagaState initialState,
        IEnumerable<((SagaState From, string Event) Key, TransitionOutcome Outcome)> table)
    {
        SagaType = sagaType;
        InitialState = initialState;

        var builder = ImmutableDictionary.CreateBuilder<(SagaState, string), TransitionOutcome>();
        foreach (var ((from, evt), outcome) in table)
        {
            if (builder.ContainsKey((from, evt)))
            {
                throw new InvalidOperationException(
                    $"Duplicate transition for ({from}, '{evt}') in saga '{sagaType}': the " +
                    "transition table must be a function (ADR-IC-003 §P2).");
            }

            builder[(from, evt)] = outcome;
        }

        _table = builder.ToImmutable();
    }

    /// <inheritdoc />
    public string SagaType { get; }

    /// <inheritdoc />
    public SagaState InitialState { get; }

    /// <inheritdoc />
    public bool TryAdvance(SagaState current, string eventType, out TransitionOutcome outcome)
        => _table.TryGetValue((current, eventType), out outcome);

    /// <summary>
    /// A state is terminal when NO transition leaves it — a pure inspection of the table
    /// itself, so the answer can never drift from the rows. For a machine whose terminal set
    /// IS exactly "has no outgoing edge" this is the right default; a machine with an
    /// escalation/parking state the table routes INTO but does not yet route OUT of (so the
    /// default would call it terminal even though it is operator-resolvable) MUST override.
    /// <para>
    /// <b><see cref="ConstitutionProcess"/> overrides this</b> precisely because of such a state:
    /// HUMAN_INTERVENTION_REQUIRED appears in its table only as a <c>To()</c> target, never as a
    /// <c>From</c>-key, so this default would report it terminal — but it is a production-reachable
    /// escalation state an operator resolves out of (the resolution edge arrives with PR2,
    /// bd babelstone-mtto). ConstitutionProcess.IsTerminal therefore delegates to
    /// <see cref="SagaStateNames.IsTerminal"/> (the pre-multi-saga predicate), keeping HIR
    /// NON-terminal and the refactor behaviour-preserving. So the substrate default and the
    /// ConstitutionProcess answer DIVERGE on HIR today, by design — the override is what reconciles
    /// them with pre-PR1 behaviour.
    /// </para></summary>
    public virtual bool IsTerminal(SagaState state) => !_table.Keys.Any(k => k.Item1 == state);

    /// <summary>The full transition table, for inspection and the §P2 fitness test (the
    /// table IS the documentation). Read-only; the saga's behaviour is auditable from here
    /// alone, without reading any surrounding code.</summary>
    public ImmutableDictionary<(SagaState From, string Event), TransitionOutcome> Transitions => _table;
}
