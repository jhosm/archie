using System.Diagnostics;
using Babelstone.Orchestrator.Saga;
using Babelstone.Telemetry;
using Npgsql;

namespace Babelstone.Orchestrator.Inbox;

/// <summary>
/// The disposition of one <see cref="SagaAdvanceHandler.AdvanceAsync"/> call.
/// </summary>
public enum AdvanceOutcome
{
    /// <summary>An accepted transition: the state moved, the history row and commands were
    /// written, all committed.</summary>
    Advanced,

    /// <summary>A duplicate physical delivery (the <c>message_id</c> was already in the
    /// inbox): no effect ran (Document 04 dedup). Effectively-once.</summary>
    Duplicate,

    /// <summary>The event arrived for a saga already in a terminal state (COMPLETED /
    /// CANCELLED): a no-op advance, the dedup row written so the offset can move on.</summary>
    Terminal,

    /// <summary>No <c>(current_state, event_type)</c> transition exists (ADR-IC-003 §P2):
    /// the event is rejected, NOT silently applied. The handler signals it so the loop can
    /// route the record to its poison/dead-letter seam rather than wedging the partition.</summary>
    NoTransition,

    /// <summary>The triggering event referenced a process id with no saga row: there is nothing
    /// to advance. Sagas are started ONLY at the edge (<c>EdgeSagaStarter</c>), so an advance
    /// event for an unknown saga is rejected, not invented.</summary>
    UnknownSaga,
}

/// <summary>
/// Drives a saga forward from one inbox event (ADR-IC-003 §S2: "the saga resumes when the
/// triggering event arrives from Redpanda"). This is the substrate's idempotent,
/// inbox-driven advance: in ONE PostgreSQL transaction it dedups on the message id, loads
/// the saga, asks the state machine for the transition (ADR-IC-003 §P2), applies the state
/// move under optimistic concurrency (§P1), appends the audit transition (§F2), and emits
/// the decided commands through the outbox seam (§P1). The dedup row, the state move, the
/// history row, and the command rows commit together — effectively-once saga progression.
/// </summary>
/// <remarks>
/// <para>
/// This is the handler the engine's <c>IInboxMessageHandler</c> seam (G.2) was built to
/// receive — its doc-comment names "Epic H.1 (the saga state machine in PG)" as the plug
/// point. The real consume loop (the engine's <c>InboxPump</c>) owns the Kafka offset and
/// the transaction lifecycle; this handler contributes its effect on the supplied
/// connection/transaction. Kept decoupled from Confluent/Avro so the substrate is testable
/// against a bare PostgreSQL without standing up Redpanda.
/// </para>
/// <para>
/// <b>Determinism (ADR-PC-010 §P5):</b> the TRANSITION DECISION is a pure function of
/// (current state, event type) — the state machine carries no clock, no I/O, no randomness.
/// This handler is the impure shell that loads, persists, and emits; it passes no time into
/// the decision.
/// </para>
/// <para>
/// <b>Distributed trace coupling (H.5, ADR-IC-007 Layer 1 / ADR-IC-003 §P3):</b> this impure
/// shell — never the pure state machine — opens ONE manual OpenTelemetry span per advance on the
/// SHARED <see cref="BabelstoneTelemetry.ActivitySource"/> (the same <c>Babelstone.Engine</c>
/// scope the engine uses, not a competing source), PARENTED to the inbound event's W3C
/// <c>traceparent</c>, so a saga's work threads into one connected distributed trace. The span
/// carries the structural <c>babelstone.saga.*</c> identifiers (<c>process_id</c> +
/// <c>correlation_id</c> as ADR-IC-003 §P3 requires, plus the <c>transition</c> and
/// <c>outcome</c>) with NO PII (ADR-PC-004 §P2). The span's own context is injected back as the
/// OUTBOUND <c>traceparent</c> the emitted commands carry, so downstream services thread under
/// this saga's trace. With no tracer listening, <see cref="ActivitySource.StartActivity(string,
/// ActivityKind)"/> returns <c>null</c> and the whole path is a near-zero-cost no-op.
/// </para>
/// </remarks>
public sealed class SagaAdvanceHandler
{
    private readonly IReadOnlyDictionary<string, ISagaStateMachine> _machines;
    private readonly SagaStateStore _stateStore;
    private readonly SagaTransitionLog _transitionLog;
    private readonly ISagaCommandSink _commandSink;
    private readonly SagaInboxWriter _inboxWriter = new();

    /// <summary>
    /// The event-auto-start registry (ADR-IC-018 §P5/§D5), keyed by the module's
    /// <see cref="AutoStartRule.StartEventType"/> (the <c>ce_type</c> record name that starts a new
    /// instance). Empty when no <see cref="SagaStartMode.EventAutoStarted"/> module is registered (the
    /// edge-only host), in which case the <c>UnknownSaga</c> branch is unchanged. Built ONCE at
    /// construction from the registered modules' declared rules — the substrate reads only a module's
    /// declared <c>StartEventType</c>, its machine's <see cref="ISagaStateMachine.InitialState"/>, and the
    /// rule's header predicate; it names NO family (the rule and the predicate live in the family module).
    /// </summary>
    private readonly IReadOnlyDictionary<string, AutoStartEntry> _autoStartModules;

    /// <summary>One event-auto-start rule resolved to what the start needs (ADR-IC-018 §P5). PII-free,
    /// substrate-internal — NOT one of the three concrete saga interfaces the
    /// ORCHESTRATOR_SUBSTRATE_NO_CONCRETE_SAGA gate forbids (it is a plain projection of a module's
    /// declared rule). The predicate is the family's; the substrate only invokes it.</summary>
    private sealed record AutoStartEntry(
        string SagaType,
        string InitialState,
        Func<IReadOnlyDictionary<string, string>, bool>? HeaderPredicate);

    /// <summary>
    /// Host N saga state machines keyed by <c>saga_type</c> (bd babelstone-mtto PR1 — the multi-saga
    /// substrate). On each advance the handler routes by the loaded saga's
    /// <see cref="SagaInstance.SagaType"/> to the right machine; an unknown saga type is a fail-closed
    /// error (the saga_state row names a machine that was never registered). A duplicate
    /// <see cref="ISagaStateMachine.SagaType"/> is a wiring error and throws at construction — the
    /// registry must be a function (the same stance <see cref="TableStateMachine"/> takes on a
    /// duplicate transition).
    /// <para>
    /// The handler carries NO family-specific dependency (ADR-IC-018 §D2/§P6): the constitution-only
    /// reissue-budget substitution and approval-fork self-emit it used to guard on
    /// <c>saga.SagaType == ConstitutionProcess.Type</c> are now optional machine hooks
    /// (<see cref="IEventSubstitutor"/> / <see cref="IPostAdvanceHook"/>) the family's machine
    /// implements — the substrate dispatches to them generically and names no family.
    /// </para>
    /// </summary>
    public SagaAdvanceHandler(
        IEnumerable<ISagaStateMachine> machines,
        SagaStateStore stateStore,
        SagaTransitionLog transitionLog,
        ISagaCommandSink commandSink,
        IEnumerable<ISagaModule>? modules = null)
    {
        ArgumentNullException.ThrowIfNull(machines);

        var map = new Dictionary<string, ISagaStateMachine>(StringComparer.Ordinal);
        foreach (var m in machines)
        {
            if (!map.TryAdd(m.SagaType, m))
            {
                throw new InvalidOperationException(
                    $"Duplicate ISagaStateMachine for saga_type '{m.SagaType}': the saga-type → machine " +
                    "registry must be a function (bd babelstone-mtto PR1).");
            }
        }

        if (map.Count == 0)
        {
            throw new ArgumentException("At least one ISagaStateMachine must be registered.", nameof(machines));
        }

        _machines = map;
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _transitionLog = transitionLog ?? throw new ArgumentNullException(nameof(transitionLog));
        _commandSink = commandSink ?? throw new ArgumentNullException(nameof(commandSink));

        // Build the event-auto-start registry (ADR-IC-018 §P5/§D5) from the EventAutoStarted modules the
        // host passed (null for an edge-only host → no auto-start). The substrate reads only a module's
        // DECLARED rule + its machine's InitialState; it names no family. A module whose StartMode is
        // EventAutoStarted MUST carry an AutoStartRule (the contract pairs them) — a null rule there is a
        // wiring error, surfaced fail-closed. Keyed by StartEventType; a duplicate start event across two
        // auto-start modules is a wiring error (the registry must be a function), the same stance the
        // machine registry takes on a duplicate saga_type.
        var autoStart = new Dictionary<string, AutoStartEntry>(StringComparer.Ordinal);
        if (modules is not null)
        {
            foreach (var module in modules)
            {
                if (module.StartMode != SagaStartMode.EventAutoStarted)
                {
                    continue;
                }

                var rule = module.AutoStartRule
                    ?? throw new InvalidOperationException(
                        $"Module '{module.SagaType}' is EventAutoStarted but declares no AutoStartRule " +
                        "(ADR-IC-018 §P5: the start mode and the rule are paired).");

                if (!map.TryGetValue(module.SagaType, out var machine))
                {
                    throw new InvalidOperationException(
                        $"Auto-start module '{module.SagaType}' has no registered ISagaStateMachine; " +
                        "register the machine in the host (ADR-IC-018 §P4).");
                }

                if (!autoStart.TryAdd(rule.StartEventType,
                        new AutoStartEntry(module.SagaType, machine.InitialState, rule.HeaderPredicate)))
                {
                    throw new InvalidOperationException(
                        $"Duplicate auto-start StartEventType '{rule.StartEventType}': two modules cannot " +
                        "auto-start on the same event type — the registry must be a function (ADR-IC-018 §P5).");
                }
            }
        }

        _autoStartModules = autoStart;
    }

    /// <summary>
    /// Convenience constructor for a single-saga host (the prior signature, kept so every existing
    /// call site stays behaviour-preserving). Delegates to the N-machine constructor with a one-element
    /// registry — the substrate path is identical, the saga simply routes to its only machine.
    /// </summary>
    public SagaAdvanceHandler(
        ISagaStateMachine machine,
        SagaStateStore stateStore,
        SagaTransitionLog transitionLog,
        ISagaCommandSink commandSink)
        : this(
            [machine ?? throw new ArgumentNullException(nameof(machine))],
            stateStore, transitionLog, commandSink)
    {
    }

    /// <summary>
    /// Process one inbox event end-to-end in its own transaction: dedup, start-or-advance,
    /// persist the transition, emit commands, commit. Idempotent — a redelivered message id
    /// is a no-op (<see cref="AdvanceOutcome.Duplicate"/>). A transient DB failure throws
    /// (the transaction rolls back, the caller redelivers); a structurally-impossible event
    /// returns a non-throwing outcome the caller routes to poison.
    /// </summary>
    public async Task<AdvanceOutcome> AdvanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SagaInboxEvent message,
        CancellationToken ct = default)
    {
        // (0) Open the saga-advance span (H.5) on the SHARED Babelstone.Engine source, PARENTED to
        // the inbound event's W3C trace context so this step joins the upstream trace as a child
        // (ADR-IC-007 Layer 1). Kind = Consumer: the orchestrator is a Redpanda consumer driving a
        // saga off a consumed event (ADR-IC-003 §S2). With no tracer listening, StartActivity
        // returns null and the using-block + every span?.* below is a no-op. The span carries only
        // structural babelstone.saga.* identifiers — process_id + correlation_id (ADR-IC-003 §P3),
        // never PII (ADR-PC-004 §P2). The current Activity is ambient for the duration, so the
        // outbound traceparent the sink injects is THIS span's context (the saga's commands thread
        // under this trace).
        var parentContext = SagaTraceContext.ParseTraceParent(message.TraceParent);
        using var span = BabelstoneTelemetry.ActivitySource.StartActivity(
            BabelstoneAttributes.SpanSagaAdvance, ActivityKind.Consumer, parentContext);
        span?.SetTag(BabelstoneAttributes.SagaProcessId, message.ProcessId.ToString());
        // The saga_type tag is set in AdvanceCoreAsync once the saga row is loaded and the machine is
        // routed (bd babelstone-mtto PR1 — the handler hosts N machines, so the type comes from the
        // loaded saga, not a single pre-bound machine).
        span?.SetTag(BabelstoneAttributes.SagaEventType, message.EventType);
        span?.SetTag(BabelstoneAttributes.SagaCausationId, message.MessageId.ToString());
        if (message.CorrelationId is { } correlation)
        {
            span?.SetTag(BabelstoneAttributes.SagaCorrelationId, correlation.ToString());
        }

        var outcome = await AdvanceCoreAsync(connection, transaction, message, span, ct);
        span?.SetTag(BabelstoneAttributes.SagaOutcome, outcome.ToString());
        return outcome;
    }

    private async Task<AdvanceOutcome> AdvanceCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SagaInboxEvent message,
        Activity? span,
        CancellationToken ct)
    {
        // (1) Dedup FIRST (Document 04 / ADR-IC-003 §P1 "Inbox deduplication … applied to
        // saga event consumption"). If the message_id is already in the inbox, this is a
        // physical redelivery: skip the whole advance — the effect already committed once.
        // The INSERT below (step 5) is the race-safe backstop; this SELECT short-circuits
        // the common sequential redelivery.
        if (await IsDuplicateAsync(connection, transaction, message.MessageId, ct))
        {
            return AdvanceOutcome.Duplicate;
        }

        // (2) Load-or-auto-start. EDGE-started sagas (the constitution saga) already have a saga_state
        // row by the time their advance events arrive — the consume loop only resumes them. But an
        // EVENT-AUTO-STARTED saga (the renewal saga, ADR-IC-018 §P5) is BORN here: its start event is a
        // bus fact (DepositMatured) with no prior row, so a LoadAsync miss is NOT necessarily "unknown".
        var saga = await _stateStore.LoadAsync(connection, transaction, message.ProcessId, ct);
        if (saga is null)
        {
            // Does any EventAutoStarted module declare THIS event type as its start trigger, with its
            // header predicate satisfied (ADR-IC-018 §P5/§D5)? The substrate evaluates only the DECLARED
            // rule + the record's CloudEvents extension HEADERS (never the Avro payload) — it names no
            // family. The predicate sees the extension-attribute map the consume loop projected
            // (autorenewalpolicy, …); a null map is an empty one so a predicate that reads a missing key
            // simply fails. A non-auto-start event for an unknown process is the existing rejection path.
            //
            // GUARD: an EMPTY process id is never a startable subject. The consume loop falls back to
            // Guid.Empty when a record's ce_subject is absent/unparseable (intended for the edge saga's
            // UnknownSaga reject). For an auto-start event a malformed relay record must NOT mint an
            // empty-keyed saga instance — the engine relay always stamps ce_subject = aggregate_id, so a
            // missing/garbled subject is a producer defect, fail-closed to the existing UnknownSaga reject
            // (the dedup row still advances the offset). Defensive depth on the §P5 auto-start branch.
            if (message.ProcessId == Guid.Empty
                || !_autoStartModules.TryGetValue(message.EventType, out var autoStart)
                || !PredicatePasses(autoStart.HeaderPredicate, message.ExtensionHeaders))
            {
                // An advance event for a process that was never started (and no auto-start rule matches):
                // nothing to drive. Record the dedup row so the offset can move past it, then reject.
                await WriteInboxRowAsync(connection, transaction, message, "unknown-saga", ct);
                return AdvanceOutcome.UnknownSaga;
            }

            // Auto-start (ADR-IC-018 §P5): create the saga_state row (process_id = ce_subject, the saga's
            // InitialState, its saga_type) and fall through to advance it with this SAME start event — all
            // in the loop's ONE transaction (start + first transition + dedup row commit atomically). The
            // INSERT is idempotent on process_id: a redelivered start (a replayed DepositMatured) collides
            // on the PK and TryStartAsync returns false, so we reload the existing row rather than reset a
            // running/terminal saga (Risk R4 — the existing terminal guard then no-ops a late re-start).
            var created = await _stateStore.TryStartAsync(
                connection, transaction, message.ProcessId,
                autoStart.SagaType, autoStart.InitialState, message.CorrelationId, ct);

            saga = created
                ? new SagaInstance(
                    message.ProcessId, autoStart.SagaType, autoStart.InitialState,
                    Version: 0, message.CorrelationId)
                : await _stateStore.LoadAsync(connection, transaction, message.ProcessId, ct);

            if (saga is null)
            {
                // A racer started it then the row vanished — structurally impossible under the FK/PK, but
                // fail-closed rather than NPE: redeliver (the transaction rolls back, nothing committed).
                throw new InvalidOperationException(
                    $"Auto-start of saga {message.ProcessId} ('{autoStart.SagaType}') created no row and the " +
                    "reload found none — the start INSERT and reload are inconsistent (ADR-IC-018 §P5).");
            }
        }

        // (2a) Route to the machine for THIS saga's type (bd babelstone-mtto PR1 — the multi-saga
        // substrate). The saga_state.saga_type the edge persisted at start selects the state machine;
        // an unrecognised type is a fail-closed error (the row names a machine that was never
        // registered), never a silent skip — the substrate cannot advance a saga it cannot decide.
        if (!_machines.TryGetValue(saga.SagaType, out var machine))
        {
            throw new InvalidOperationException(
                $"Saga {saga.ProcessId} has saga_type '{saga.SagaType}' but no ISagaStateMachine is " +
                "registered for it. Register the machine in the host (bd babelstone-mtto PR1).");
        }

        // The saga type is operational, never PII — tag it now that the machine is routed.
        span?.SetTag(BabelstoneAttributes.SagaType, saga.SagaType);

        // A saga that already reached a terminal state accepts no further transitions
        // (ADR-IC-003 §Context "Terminal"). Each machine defines its OWN terminal set, so ask the
        // routed machine (ISagaStateMachine.IsTerminal), not the ConstitutionProcess-scoped static.
        // The late event is a no-op advance — dedup it so it does not redeliver forever, but do not
        // move the state.
        if (machine.IsTerminal(saga.State))
        {
            await WriteInboxRowAsync(connection, transaction, message, "terminal", ct);
            return AdvanceOutcome.Terminal;
        }

        // (2b) Optional EVENT SUBSTITUTION hook (ADR-IC-018 §P6). A machine MAY substitute the EFFECTIVE
        // event before the table lookup — the substrate dispatches to the hook generically and names no
        // family (the constitution-only reissue-budget substitution it used to guard on
        // saga.SagaType == ConstitutionProcess.Type now lives in the family's machine). The hook's COUNT
        // is impure (it reads the log on this transaction); its DECISION is pure, so the table stays the
        // authority on what each event does (§P2) — the hook only chooses WHICH event applies. For a
        // machine that does not implement the hook this is a no-op: the effective event is the incoming one.
        var effectiveEventType = message.EventType;
        if (machine is IEventSubstitutor substitutor)
        {
            effectiveEventType = await substitutor.SubstituteAsync(
                saga.State, message.EventType, _transitionLog,
                connection, transaction, saga.ProcessId, message.ExtensionHeaders, ct);
        }

        // (3) The state machine is the specification (ADR-IC-003 §P2): a (state, event) pair
        // not in the table is REJECTED, never silently applied. The caller routes a
        // NoTransition to poison — an illegal transition cannot corrupt the saga. The lookup uses
        // effectiveEventType so the budget's escalate decision (2b) is honoured by the SAME pure table.
        if (!machine.TryAdvance(saga.State, effectiveEventType, out var outcome))
        {
            await WriteInboxRowAsync(connection, transaction, message, "no-transition", ct);
            return AdvanceOutcome.NoTransition;
        }

        // (4) Apply the move under optimistic concurrency (ADR-IC-003 §P1 / §Residual). The
        // WHERE version = saga.Version predicate is the concurrent-writer guard: if a racing
        // orchestrator advanced this row between our LoadAsync and here, this matches zero
        // rows and we throw — the caller's transaction rolls back and the record redelivers,
        // re-reading the now-current state. The losing writer never clobbers.
        var won = await _stateStore.TryAdvanceAsync(
            connection, transaction, saga.ProcessId, saga.Version, outcome.Next, ct);
        if (!won)
        {
            throw new SagaConcurrencyException(saga.ProcessId, saga.Version);
        }

        // (5) Persist the audit transition (ADR-IC-003 §F2) and the dedup row in the SAME
        // transaction as the move. The transition records the EFFECTIVE event the state machine applied
        // (effectiveEventType) — for a budget escalation that is ReissueBudgetExhausted, so the audit
        // trail names exactly WHY the saga went to HUMAN_INTERVENTION_REQUIRED. The dedup row below still
        // keys on the PHYSICAL message (its real id + source topic), so the not-executed delivery dedups
        // on its own id. This mirrors the self-emit fork, which logs the derived ConstitutionApproved /
        // WorkflowApprovalRequired rather than the sibling validation that triggered it.
        await _transitionLog.AppendAsync(
            connection, transaction, saga.ProcessId, saga.State, outcome.Next,
            effectiveEventType, message.MessageId,
            note: outcome.Next, ct);

        await WriteInboxRowAsync(
            connection, transaction, message, outcome.Next, ct);

        // Tag the state move on the span (H.5 / Document 06 "each saga state transition"). The
        // state names are operational, never PII. The states ARE the wire strings (ADR-IC-018 §D3).
        span?.SetTag(
            BabelstoneAttributes.SagaTransition,
            $"{saga.State}->{outcome.Next}");

        // (6) Emit the decided commands through the outbox seam (ADR-IC-003 §P1, §P7). Each
        // carries the identity trio AND the OUTBOUND traceparent (H.5) — this span's context, so
        // the downstream consumer threads its spans under this saga's trace. All land atomically
        // with the state move. The sink is routed to THIS saga's type (bd babelstone-mtto PR2 —
        // each family assembles its OWN command bodies); a single-saga host's sink is used as-is.
        var sink = SinkFor(saga.SagaType);
        var traceParent = SagaTraceContext.FormatTraceParent(span);
        foreach (var commandType in outcome.Commands)
        {
            await sink.EmitAsync(
                connection, transaction, saga.ProcessId, commandType,
                message.MessageId, saga.CorrelationId ?? message.CorrelationId, ct, traceParent);
        }

        // (7) Optional POST-ADVANCE hook (ADR-IC-018 §P6). A machine MAY run additional in-transaction
        // logic after the state move — e.g. the constitution machine self-emits the approval fork when
        // it lands in VALIDATIONS_COMPLETE, deciding auto-approve vs route-to-workflow and feeding the
        // chosen event back into THIS SAME advance path in-process (nothing on the durable bus,
        // ADR-IC-003 §S2). The substrate dispatches to the hook generically and names no family (the
        // constitution-only fork it used to guard on saga.SagaType == ConstitutionProcess.Type now lives
        // in the family's machine, which checks the landed state itself). The hook self-advances through
        // the SAME pure machine + persistence path, so it is auditable from the table (§P2). For a
        // machine that does not implement the hook this is a no-op.
        if (machine is IPostAdvanceHook hook)
        {
            await hook.OnAdvancedAsync(
                connection, transaction, machine, saga.ProcessId, outcome.Next,
                saga.CorrelationId ?? message.CorrelationId, span,
                _stateStore, _transitionLog, sink, _inboxWriter, ct);
        }

        return AdvanceOutcome.Advanced;
    }

    /// <summary>
    /// Resolve the command sink for <paramref name="sagaType"/> (bd babelstone-mtto PR2). A multi-saga
    /// host injects a <see cref="CompositeSagaCommandSink"/>, which routes to the family typed sink for
    /// this saga; a single-saga host (and every existing test) injects one concrete sink directly, used
    /// as-is. Keeps the substrate family-agnostic — it routes by the persisted saga_type, names no family.
    /// </summary>
    private ISagaCommandSink SinkFor(string sagaType) =>
        _commandSink is CompositeSagaCommandSink composite
            ? composite.For(sagaType)
            : _commandSink;

    /// <summary>
    /// Evaluate an auto-start rule's optional header predicate (ADR-IC-018 §P5/§D5) against the record's
    /// extension attributes. A null predicate means "every event of this type starts an instance". A null
    /// header map is treated as empty, so a predicate that reads a missing key (e.g. <c>autorenewalpolicy
    /// != NONE</c> on a record that carried no such header) simply returns false — the saga does not start.
    /// </summary>
    private static bool PredicatePasses(
        Func<IReadOnlyDictionary<string, string>, bool>? predicate,
        IReadOnlyDictionary<string, string>? headers)
        => predicate is null
            || predicate(headers ?? EmptyHeaders);

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static async Task<bool> IsDuplicateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid messageId, CancellationToken ct)
    {
        const string sql = "SELECT 1 FROM inbox WHERE message_id = @message_id;";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("message_id", messageId);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    // result_summary stays operational-tier (the saga step taken) — NEVER PII (ADR-PC-004 §P2),
    // exactly like the engine inbox. Delegates to the shared SagaInboxWriter (the same writer the
    // post-advance hook uses for its self-emit dedup row). A duplicate message_id throws
    // unique-violation; the IsDuplicateAsync SELECT short-circuits the common case, and a concurrent
    // racer that slipped past it hits the constraint (the race-safe backstop), rolling back.
    private Task WriteInboxRowAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        SagaInboxEvent message, string resultSummary, CancellationToken ct)
        => _inboxWriter.WriteRowAsync(connection, transaction, message, resultSummary, ct);
}

/// <summary>
/// Thrown when an optimistic-concurrency advance loses the version race (ADR-IC-003 §P1 /
/// §Residual "Concurrent writer race"): another orchestrator instance advanced the saga
/// between this handler's load and its update. The transaction rolls back and the inbox
/// record redelivers, re-reading the now-current state — the loser retries, never clobbers.
/// </summary>
public sealed class SagaConcurrencyException(Guid processId, long expectedVersion)
    : Exception($"Saga {processId} was advanced concurrently (expected version {expectedVersion}); retrying.")
{
    /// <summary>The saga instance whose version was stale.</summary>
    public Guid ProcessId { get; } = processId;

    /// <summary>The version the losing writer read before the race.</summary>
    public long ExpectedVersion { get; } = expectedVersion;
}
