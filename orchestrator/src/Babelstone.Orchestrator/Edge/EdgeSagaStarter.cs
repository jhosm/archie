using Babelstone.Families.TermDeposit.Orchestration;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Saga;
using Npgsql;

namespace Babelstone.Orchestrator.Edge;

/// <summary>
/// The result of starting a saga at the edge — the references the 202 carries (Document 05 §Step 0).
/// </summary>
/// <param name="ProcessId">The internal saga aggregate key (the durable UUID <c>process_id</c>).</param>
/// <param name="PublicProcessId">The client-facing <c>PROC-…</c> reference the 202 returns.</param>
/// <param name="DepositId">The client-facing <c>DEP-…</c> deposit reference the 202 returns.</param>
/// <param name="State">The state the saga rests in after the start drove its first transition.</param>
public readonly record struct EdgeStartResult(
    Guid ProcessId, string PublicProcessId, string DepositId, string State);

/// <summary>
/// The PII-free business facts the edge pins onto the saga at start (bd babelstone-t7o3.1) — the
/// resolved inputs the approval fork and the command-payload assembly read later. Assembled by the
/// edge HTTP shell from the request body (the amount + account/product references) and the
/// edge-pinned policy (the client standing + the auto-approval threshold in force at admission).
/// </summary>
/// <remarks>
/// Every field is a structural reference or an integer-cents scalar — NO PII (ADR-PC-004 §P2). The
/// threshold and client type are PINNED at the edge so the fork decides replay-stably, never
/// re-dereferencing live config at decision time (ADR-PC-010 §P5).
/// </remarks>
/// <param name="ProductRef">The product catalogue reference being constituted (request body).</param>
/// <param name="AmountMinorUnits">The deposit principal in integer cents (request body).</param>
/// <param name="SourceAccountRef">The opaque source-account token (request body).</param>
/// <param name="InterestAccountRef">The opaque interest-account token, or null (request body).</param>
/// <param name="ClientType">The client's standing, resolved at the edge — the fork's client input.</param>
/// <param name="AutoApprovalThresholdMinorUnits">The auto-approval ceiling in integer cents, pinned
/// at the edge from the policy in force at admission.</param>
/// <remarks>
/// <b>No product-family knowledge at the edge (Fork B rework, bd t7o3.11 / 3k10 / c8d8).</b> The
/// structural product facts (term / interest variant / renewal policy / coupon cadence / role / start
/// date) the rejected v1 stand-in resolved here are GONE — the engine resolves them from the product
/// code at constitution (the maintainer's Q2 choice, ADR-PC-009). The edge pins only the amount +
/// account/product references + the approval-fork inputs; <see cref="ProductRef"/> is the product code.
/// </remarks>
public sealed record EdgeBusinessFacts(
    string ProductRef,
    long AmountMinorUnits,
    string SourceAccountRef,
    string? InterestAccountRef,
    ClientType ClientType,
    long AutoApprovalThresholdMinorUnits);

/// <summary>
/// Starts the <see cref="ConstitutionProcess"/> saga from the EDGE (I.1, ADR-IC-006 §P4 / Document
/// 05 §Step 0). This is the upstream front door: the 202 means the SAGA STARTED, not a direct engine
/// append (PR #149's rejected anti-pattern). In ONE PostgreSQL transaction it creates the durable
/// <c>ConstitutionProcess</c> STARTED row (with the edge identity — the public <c>PROC-…</c>
/// reference + the owning client), drives the first transition (STARTED + ConstitutionRequested →
/// PARALLEL_VALIDATION) through the PURE state machine, appends the transition history, and emits the
/// two parallel validation commands through the outbox seam. The existing consume loop (#167) then
/// advances the saga on the validation result events.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bus stays events-only (no-PII / bus-is-events-only).</b> The edge produces NOTHING onto the
/// durable bus: it starts the saga IN-PROCESS (Document 05 §Step 0 "Creates the ConstitutionProcess
/// aggregate … persists everything … in the same local transaction"). The decided commands ride the
/// existing <c>saga_outbox</c> → HTTP dispatcher (commands ride HTTP point-to-point, ADR-PC-029);
/// only the engine, not the orchestrator, ever publishes the <c>ConstitutionRequested</c> integration
/// event. Starting in-process is the path consistent with #167's loop: re-publishing
/// <c>ConstitutionRequested</c> to <c>deposits.process.events</c> would have the loop re-drive the
/// SAME start (a redundant round-trip the inbox would then dedup), so the edge owns the start
/// directly — the loop owns the SUBSEQUENT advances.
/// </para>
/// <para>
/// <b>Impure shell over a pure core (ADR-PC-010 §P5).</b> This shell mints the saga GUID, derives the
/// references, opens the connection/transaction, and persists — the TRANSITION DECISION
/// (<see cref="ISagaStateMachine.TryAdvance"/>) is a pure function of (state, event). The minting and
/// I/O live here, never in the decider.
/// </para>
/// <para>
/// <b>No PII on any row (ADR-PC-004 §P2).</b> The persisted edge identity is two structural references
/// — the <c>PROC-…</c> handle and the opaque <c>client_id</c>. The request's amount and account
/// references never land on a saga row; the emitted commands carry only seam references (the
/// <c>SagaCommandOutboxSink</c> byte-stable body).
/// </para>
/// </remarks>
public sealed class EdgeSagaStarter(
    ISagaStateMachine machine,
    SagaStateStore stateStore,
    SagaTransitionLog transitionLog,
    ISagaCommandSink commandSink,
    SagaBusinessReferenceStore businessReferenceStore)
{
    private readonly ISagaStateMachine _machine = machine ?? throw new ArgumentNullException(nameof(machine));
    private readonly SagaStateStore _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    private readonly SagaTransitionLog _transitionLog = transitionLog ?? throw new ArgumentNullException(nameof(transitionLog));
    private readonly ISagaCommandSink _commandSink = commandSink ?? throw new ArgumentNullException(nameof(commandSink));
    private readonly SagaBusinessReferenceStore _businessReferenceStore =
        businessReferenceStore ?? throw new ArgumentNullException(nameof(businessReferenceStore));

    /// <summary>The event type that STARTS this saga type — the synthetic start signal the edge
    /// applies in-process (the same start event the consume loop would otherwise carry).</summary>
    public required string StartEventType { get; init; }

    /// <summary>
    /// Start a fresh saga for <paramref name="owningClientId"/> carrying the request's
    /// <paramref name="businessFacts"/>. Mints the saga GUID + the public references, then in one
    /// transaction creates the STARTED row, PERSISTS the per-saga business references (so the approval
    /// fork and the command-payload assembly can read them — bd babelstone-t7o3.1), drives the first
    /// transition, and emits its commands — all atomic (the edge's local transaction, Document 05
    /// §Step 0). Returns the references the 202 carries.
    /// </summary>
    public async Task<EdgeStartResult> StartAsync(
        string connectionString,
        string owningClientId,
        EdgeBusinessFacts businessFacts,
        Guid? correlationId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(owningClientId);
        ArgumentNullException.ThrowIfNull(businessFacts);

        // The impure shell mints the durable saga key and derives the client-facing references from
        // it (ADR-PC-010 §P5: GUID minting is a shell concern, never inside the decider).
        var processId = Guid.NewGuid();
        var publicProcessId = EdgeReferences.ProcessReference(processId);
        var depositId = EdgeReferences.DepositReference(processId);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // (1) Create the STARTED row with the edge identity. A minted GUID never collides in
        // practice; the idempotent INSERT is the same shape the consume-loop start uses.
        var created = await _stateStore.TryStartWithEdgeIdentityAsync(
            connection, transaction, processId, _machine.SagaType, _machine.InitialState,
            correlationId, publicProcessId, owningClientId, ct);
        if (!created)
        {
            throw new InvalidOperationException(
                $"Saga {processId} already exists; the edge minted a colliding process id.");
        }

        // (1b) Pin the per-saga business references (bd babelstone-t7o3.1). Written ONCE here, in the
        // SAME transaction as the STARTED row, so the fork (at VALIDATIONS_COMPLETE) and the command
        // assembly read the SAME facts the request carried. PII-free: integer cents + opaque
        // references + a closed client-type code (ADR-PC-004 §P2). The deposit reference is the
        // edge-derived DEP-… handle, paired with the process id.
        await _businessReferenceStore.TryInsertAsync(
            connection, transaction,
            new SagaBusinessReference(
                ProcessId: processId,
                ProductRef: businessFacts.ProductRef,
                AmountMinorUnits: businessFacts.AmountMinorUnits,
                SourceAccountRef: businessFacts.SourceAccountRef,
                InterestAccountRef: businessFacts.InterestAccountRef,
                DepositRef: depositId,
                ClientType: businessFacts.ClientType,
                AutoApprovalThresholdMinorUnits: businessFacts.AutoApprovalThresholdMinorUnits),
            ct);

        var state = _machine.InitialState;

        // (2) Drive the first transition through the PURE state machine (STARTED +
        // ConstitutionRequested → PARALLEL_VALIDATION, emitting the two parallel commands). If the
        // table had no (initial, start) transition the saga would simply rest in its initial state.
        // The start signal's causation id is the saga's own process id (the edge is the origin —
        // there is no upstream message that caused this start), a stable, PII-free reference.
        var causationId = processId;
        if (_machine.TryAdvance(_machine.InitialState, StartEventType, out var outcome))
        {
            var won = await _stateStore.TryAdvanceAsync(
                connection, transaction, processId, 0, outcome.Next, ct);
            if (!won)
            {
                throw new SagaConcurrencyException(processId, 0);
            }

            await _transitionLog.AppendAsync(
                connection, transaction, processId, _machine.InitialState, outcome.Next,
                StartEventType, causationId, note: outcome.Next, ct);

            foreach (var commandType in outcome.Commands)
            {
                await _commandSink.EmitAsync(
                    connection, transaction, processId, commandType, causationId, correlationId, ct);
            }

            state = outcome.Next;
        }
        else
        {
            await _transitionLog.AppendAsync(
                connection, transaction, processId, _machine.InitialState, _machine.InitialState,
                StartEventType, causationId, note: "started", ct);
        }

        await transaction.CommitAsync(ct);
        return new EdgeStartResult(processId, publicProcessId, depositId, state);
    }
}
