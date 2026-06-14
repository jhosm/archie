using System.Diagnostics;
using Babelstone.Orchestrator.Handlers;
using Babelstone.Orchestrator.Saga;
using Babelstone.Telemetry;
using Npgsql;

namespace Babelstone.Orchestrator.Inbox;

/// <summary>
/// The disposition of one <see cref="SagaAdvanceHandler.AdvanceAsync"/> call.
/// </summary>
public enum AdvanceOutcome
{
    /// <summary>The saga was started (a <c>saga_state</c> row created in its initial state).</summary>
    Started,

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

    /// <summary>The triggering event referenced a process id with no saga row (and the event
    /// is not a start event): there is nothing to advance. Rejected, not invented.</summary>
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
public sealed class SagaAdvanceHandler(
    ISagaStateMachine machine,
    SagaStateStore stateStore,
    SagaTransitionLog transitionLog,
    ISagaCommandSink commandSink,
    SagaBusinessReferenceStore? businessReferenceStore = null)
{
    private readonly ISagaStateMachine _machine = machine ?? throw new ArgumentNullException(nameof(machine));
    private readonly SagaStateStore _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    private readonly SagaTransitionLog _transitionLog = transitionLog ?? throw new ArgumentNullException(nameof(transitionLog));
    private readonly ISagaCommandSink _commandSink = commandSink ?? throw new ArgumentNullException(nameof(commandSink));
    private readonly SagaBusinessReferenceStore _businessReferenceStore = businessReferenceStore ?? new SagaBusinessReferenceStore();

    /// <summary>The event type that STARTS this saga type (ADR-IC-003 §P2): the only event
    /// that creates a fresh <c>saga_state</c> row rather than advancing an existing one.</summary>
    public required string StartEventType { get; init; }

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
        span?.SetTag(BabelstoneAttributes.SagaType, _machine.SagaType);
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

        // (2) Start vs advance. The start event creates the saga row; everything else
        // advances an existing one.
        if (message.EventType == StartEventType)
        {
            return await StartAsync(connection, transaction, message, span, ct);
        }

        var saga = await _stateStore.LoadAsync(connection, transaction, message.ProcessId, ct);
        if (saga is null)
        {
            // An advance event for a process that was never started: nothing to drive.
            // Record the dedup row so the offset can move past it, then reject.
            await WriteInboxRowAsync(connection, transaction, message, "unknown-saga", ct);
            return AdvanceOutcome.UnknownSaga;
        }

        // A saga that already reached a terminal state accepts no further transitions
        // (ADR-IC-003 §Context "Terminal"). The late event is a no-op advance — dedup it so
        // it does not redeliver forever, but do not move the state.
        if (SagaStateNames.IsTerminal(saga.State))
        {
            await WriteInboxRowAsync(connection, transaction, message, "terminal", ct);
            return AdvanceOutcome.Terminal;
        }

        // (3) The state machine is the specification (ADR-IC-003 §P2): a (state, event) pair
        // not in the table is REJECTED, never silently applied. The caller routes a
        // NoTransition to poison — an illegal transition cannot corrupt the saga.
        if (!_machine.TryAdvance(saga.State, message.EventType, out var outcome))
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
        // transaction as the move.
        await _transitionLog.AppendAsync(
            connection, transaction, saga.ProcessId, saga.State, outcome.Next,
            message.EventType, message.MessageId,
            note: SagaStateNames.ToName(outcome.Next), ct);

        await WriteInboxRowAsync(
            connection, transaction, message, SagaStateNames.ToName(outcome.Next), ct);

        // Tag the state move on the span (H.5 / Document 06 "each saga state transition"). The
        // state names are operational, never PII.
        span?.SetTag(
            BabelstoneAttributes.SagaTransition,
            $"{SagaStateNames.ToName(saga.State)}->{SagaStateNames.ToName(outcome.Next)}");

        // (6) Emit the decided commands through the outbox seam (ADR-IC-003 §P1, §P7). Each
        // carries the identity trio AND the OUTBOUND traceparent (H.5) — this span's context, so
        // the downstream consumer threads its spans under this saga's trace. All land atomically
        // with the state move.
        var traceParent = SagaTraceContext.FormatTraceParent(span);
        foreach (var commandType in outcome.Commands)
        {
            await _commandSink.EmitAsync(
                connection, transaction, saga.ProcessId, commandType,
                message.MessageId, saga.CorrelationId ?? message.CorrelationId, ct, traceParent);
        }

        // (7) Self-emit the approval fork (bd babelstone-t7o3.1). When this move landed the saga in
        // VALIDATIONS_COMPLETE — both reversible validations have now completed — the orchestrator
        // DECIDES the approval fork (auto-approve vs route-to-workflow) and feeds the chosen event
        // (ConstitutionApproved / WorkflowApprovalRequired) back into THIS SAME advance path,
        // IN-PROCESS within this transaction (nothing on the durable bus, ADR-IC-003 §S2). That is
        // what crosses the saga into APPROVED (the auto path) or AWAIT_WORKFLOW_APPROVAL without an
        // external trigger. The DECISION stays pure (ApprovalForkHandler.Decide on the edge-pinned
        // input); this shell only loads the pinned references and schedules the next step.
        if (outcome.Next == SagaState.ValidationsComplete)
        {
            await SelfEmitApprovalForkAsync(
                connection, transaction, saga.ProcessId,
                saga.CorrelationId ?? message.CorrelationId, span, ct);
        }

        return AdvanceOutcome.Advanced;
    }

    /// <summary>
    /// The impure shell of the approval fork (bd babelstone-t7o3.1): the saga has just reached
    /// VALIDATIONS_COMPLETE, so DECIDE the fork on the pinned business references and SELF-ADVANCE the
    /// saga with the chosen event — all on the SAME transaction the caller owns, so the fork's move +
    /// its emitted command commit atomically with the validation join that triggered it. Nothing rides
    /// the durable bus.
    /// </summary>
    /// <remarks>
    /// The fork can only be decided with pinned references (the edge wrote them at start). A saga
    /// started WITHOUT them (a consume-loop-started saga that never went through the I.1 edge) has no
    /// amount/threshold/client to decide on, so the self-emit is skipped — the saga rests in
    /// VALIDATIONS_COMPLETE awaiting an external approval event, exactly as the substrate did. With
    /// references present the DECISION is pure (ApprovalForkHandler.Decide) and the chosen event is
    /// self-advanced through the SAME state machine, so it is auditable from the transition table
    /// alone (§P2).
    /// </remarks>
    private async Task SelfEmitApprovalForkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        Guid? correlationId,
        Activity? span,
        CancellationToken ct)
    {
        var reference = await _businessReferenceStore.LoadAsync(connection, transaction, processId, ct);
        if (reference is null)
        {
            // No pinned references — the fork has nothing to decide on. Leave the saga in
            // VALIDATIONS_COMPLETE for an external approval event (the substrate's behaviour).
            return;
        }

        // PURE decision (ADR-PC-010 §P5): auto-approve vs route-to-workflow on the edge-pinned amount /
        // threshold / client type — no clock, no I/O, no live-config dereference. NextEventType maps the
        // decision to the DISTINCT driver event the table accepts out of VALIDATIONS_COMPLETE.
        var decision = ApprovalForkHandler.Decide(SagaState.ValidationsComplete, reference.ToApprovalInput());
        var forkEvent = ApprovalForkHandler.NextEventType(decision);

        // The self-emitted event's message id is DETERMINISTIC (derived from process id + event type,
        // never minted), so it dedups through the SAME inbox as an external advance: a re-drive of the
        // join derives the same id and the dedup row collides — the fork is emitted exactly once.
        var selfMessageId = SagaSelfEmit.MessageId(processId, forkEvent);

        // Apply the fork event through the SAME pure state machine + persistence path the external
        // advance uses, on this transaction. The saga is at VALIDATIONS_COMPLETE (version is current —
        // this is the same row this transaction just advanced).
        var saga = await _stateStore.LoadAsync(connection, transaction, processId, ct);
        if (saga is null || saga.State != SagaState.ValidationsComplete)
        {
            return; // raced/already-advanced — the optimistic-concurrency guard owns correctness.
        }

        if (!_machine.TryAdvance(saga.State, forkEvent, out var outcome))
        {
            // The decider and the table agree by construction (the ApprovalForkHandler fitness test),
            // so this is unreachable; rejecting rather than inventing a move keeps the table the spec.
            return;
        }

        var won = await _stateStore.TryAdvanceAsync(
            connection, transaction, saga.ProcessId, saga.Version, outcome.Next, ct);
        if (!won)
        {
            throw new SagaConcurrencyException(saga.ProcessId, saga.Version);
        }

        await _transitionLog.AppendAsync(
            connection, transaction, saga.ProcessId, saga.State, outcome.Next,
            forkEvent, selfMessageId, note: SagaStateNames.ToName(outcome.Next), ct);

        // Record the self-emit in the inbox too, keyed on the deterministic id — so a re-drive of the
        // join short-circuits on the dedup SELECT before re-deciding the fork (effectively-once).
        await WriteInboxRowAsync(
            connection, transaction,
            new SagaInboxEvent(selfMessageId, processId, forkEvent, SelfEmitSourceTopic, correlationId),
            SagaStateNames.ToName(outcome.Next), ct);

        span?.SetTag(
            BabelstoneAttributes.SagaTransition,
            $"{SagaStateNames.ToName(saga.State)}->{SagaStateNames.ToName(outcome.Next)}");

        var traceParent = SagaTraceContext.FormatTraceParent(span);
        foreach (var commandType in outcome.Commands)
        {
            await _commandSink.EmitAsync(
                connection, transaction, saga.ProcessId, commandType,
                selfMessageId, correlationId, ct, traceParent);
        }
    }

    /// <summary>The synthetic source topic recorded on a self-emitted event's inbox dedup row. It is
    /// an INTERNAL marker — the self-emit never touches the durable bus (ADR-IC-003 §S2) — so the
    /// row's source is named distinctly from any real Redpanda topic.</summary>
    private const string SelfEmitSourceTopic = "saga.self-emit";

    private async Task<AdvanceOutcome> StartAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, SagaInboxEvent message, Activity? span, CancellationToken ct)
    {
        var created = await _stateStore.TryStartAsync(
            connection, transaction, message.ProcessId, _machine.SagaType,
            _machine.InitialState, message.CorrelationId, ct);

        if (!created)
        {
            // A redelivered start for a saga that already exists: do not reset it. Dedup and
            // report a duplicate (effectively-once start).
            await WriteInboxRowAsync(connection, transaction, message, "already-started", ct);
            return AdvanceOutcome.Duplicate;
        }

        // The start event can itself drive the first transition (e.g. STARTED +
        // ConstitutionRequested → PARALLEL_VALIDATION, emitting the parallel commands). If
        // the table has no transition for (initial, start-event), the saga simply rests in
        // its initial state — a legitimate "created, awaiting first driver" shape.
        if (_machine.TryAdvance(_machine.InitialState, message.EventType, out var outcome))
        {
            var won = await _stateStore.TryAdvanceAsync(
                connection, transaction, message.ProcessId, 0, outcome.Next, ct);
            if (!won)
            {
                throw new SagaConcurrencyException(message.ProcessId, 0);
            }

            await _transitionLog.AppendAsync(
                connection, transaction, message.ProcessId, _machine.InitialState, outcome.Next,
                message.EventType, message.MessageId, note: SagaStateNames.ToName(outcome.Next), ct);

            // The start event drove the first transition: tag the move (H.5) and propagate THIS
            // span's context outbound on every command it emitted (ADR-IC-007 Layer 1).
            span?.SetTag(
                BabelstoneAttributes.SagaTransition,
                $"{SagaStateNames.ToName(_machine.InitialState)}->{SagaStateNames.ToName(outcome.Next)}");
            var traceParent = SagaTraceContext.FormatTraceParent(span);
            foreach (var commandType in outcome.Commands)
            {
                await _commandSink.EmitAsync(
                    connection, transaction, message.ProcessId, commandType,
                    message.MessageId, message.CorrelationId, ct, traceParent);
            }
        }
        else
        {
            // No first-transition: record the creation itself as the opening history row.
            await _transitionLog.AppendAsync(
                connection, transaction, message.ProcessId, _machine.InitialState, _machine.InitialState,
                message.EventType, message.MessageId, note: "started", ct);
        }

        await WriteInboxRowAsync(connection, transaction, message, "started", ct);
        return AdvanceOutcome.Started;
    }

    private static async Task<bool> IsDuplicateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid messageId, CancellationToken ct)
    {
        const string sql = "SELECT 1 FROM inbox WHERE message_id = @message_id;";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("message_id", messageId);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task WriteInboxRowAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        SagaInboxEvent message, string resultSummary, CancellationToken ct)
    {
        // result_summary stays operational-tier (the saga step taken) — NEVER PII
        // (ADR-PC-004 §P2), exactly like the engine inbox. A duplicate message_id throws
        // unique-violation here; the IsDuplicateAsync SELECT short-circuits the common case,
        // and a concurrent racer that slipped past it hits this constraint (the race-safe
        // backstop), rolling its transaction back.
        const string sql = """
            INSERT INTO inbox (message_id, source_topic, result_summary)
            VALUES (@message_id, @source_topic, @result_summary);
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("message_id", message.MessageId);
        command.Parameters.AddWithValue("source_topic", message.SourceTopic);
        command.Parameters.AddWithValue("result_summary", resultSummary);
        await command.ExecuteNonQueryAsync(ct);
    }
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
