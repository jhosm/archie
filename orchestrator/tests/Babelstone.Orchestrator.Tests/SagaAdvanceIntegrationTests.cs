using Babelstone.Orchestrator.Edge;
using Babelstone.Families.TermDeposit.Orchestration;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Saga;
using Npgsql;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// Integration tests for the inbox-driven saga substrate against a real PostgreSQL: the
/// migration applies, the saga starts and advances under optimistic concurrency, the
/// transition history is persisted, compensation is a domain action, and a redelivered
/// message id is a no-op (effectively-once). These are the falsifiable invariants ADR-IC-003
/// §"Verifiable commitments" names for the in-house orchestrator (no Test ID wired yet — this
/// is the first wiring of those commitments to actual tests, per §P5 visibility).
/// </summary>
[Trait("Category", "Integration")]
[Collection(nameof(OrchestratorPostgresCollection))]
public sealed class SagaAdvanceIntegrationTests(OrchestratorPostgresFixture fixture)
{
    private const long ThresholdCents = 1_000_00;

    private readonly ConstitutionProcess _machine = new();
    private readonly SagaStateStore _stateStore = new();
    private readonly SagaTransitionLog _transitionLog = new();
    private readonly SagaBusinessReferenceStore _businessRefStore = new();

    [Fact]
    public async Task Migration_creates_the_saga_schema()
    {
        await using var connection = await OpenAsync();
        Assert.True(await TableExistsAsync(connection, "saga_state"));
        Assert.True(await TableExistsAsync(connection, "saga_transition"));
        Assert.True(await TableExistsAsync(connection, "inbox"));
    }

    [Fact]
    public async Task Start_then_full_happy_path_lands_in_COMPLETED_with_full_history()
    {
        var correlationId = Guid.NewGuid();
        var handler = NewHandler(new RecordingCommandSink());

        // The edge is the SOLE saga starter (bd babelstone-t7o3.9): STARTED + ConstitutionRequested →
        // PARALLEL_VALIDATION, pinning the references and emitting the two parallel commands. The
        // amount is well under the threshold (auto-approve path).
        var processId = await StartSagaWithReferencesAsync(correlationId, amountCents: 100_00);
        Assert.Equal(ConstitutionProcess.States.ParallelValidation, await StateAsync(processId));

        // Both validations (balance first → AWAIT_LIMITS_VALIDATED → join). When the join lands in
        // VALIDATIONS_COMPLETE the saga AUTO-self-emits ConstitutionApproved → APPROVED (emitting
        // ConfirmDebit) in-process — the test does NOT feed an external ConstitutionApproved.
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handler, Event(processId, ConstitutionProcess.BalanceReserved)));
        Assert.Equal(ConstitutionProcess.States.AwaitLimitsValidated, await StateAsync(processId));
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handler, Event(processId, ConstitutionProcess.LimitsValidated)));
        // The self-emit fork already crossed VALIDATIONS_COMPLETE → APPROVED on the completing join.
        Assert.Equal(ConstitutionProcess.States.Approved, await StateAsync(processId));
        // Debit confirmation arms activation, then activation closes the saga.
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handler, Event(processId, ConstitutionProcess.DebitConfirmed)));
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handler, Event(processId, ConstitutionProcess.ProcessConstituted)));

        Assert.Equal(ConstitutionProcess.States.Completed, await StateAsync(processId));

        // The append-only transition history records every accepted move, in order — including the
        // in-process VALIDATIONS_COMPLETE → APPROVED self-emit (no external approval event).
        var history = await HistoryAsync(processId);
        Assert.Equal(
            new[]
            {
                ("STARTED", "PARALLEL_VALIDATION"),
                ("PARALLEL_VALIDATION", "AWAIT_LIMITS_VALIDATED"),
                ("AWAIT_LIMITS_VALIDATED", "VALIDATIONS_COMPLETE"),
                ("VALIDATIONS_COMPLETE", "APPROVED"),
                ("APPROVED", "APPROVED"),
                ("APPROVED", "COMPLETED"),
            },
            history);
    }

    [Fact]
    public async Task Parallel_validations_reach_VALIDATIONS_COMPLETE_in_either_delivery_order()
    {
        // ADR-IC-003 §P2 / Document 05 §2c fitness function: the parallel-validation join is
        // order-INDEPENDENT. The two triggers have no delivery-ordering guarantee (BalanceReserved
        // is an async ~120ms Core round-trip; LimitsValidated is a synchronous in-aggregate calc,
        // so it frequently arrives first). Driven end-to-end through the real handler, BOTH
        // orderings must land in VALIDATIONS_COMPLETE — neither poisons via NoTransition.

        // The join completes into VALIDATIONS_COMPLETE, which AUTO-self-emits the auto-approve fork
        // (amount under threshold) → APPROVED in the SAME advance. So the observable post-join state is
        // APPROVED in BOTH orderings — the order-independence is on the JOIN itself (neither poisons).

        // Order A: balance first (was the only order the prior suite exercised).
        var handlerA = NewHandler(new RecordingCommandSink());
        var procA = await StartSagaWithReferencesAsync(Guid.NewGuid(), amountCents: 100_00);
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handlerA, Event(procA, ConstitutionProcess.BalanceReserved)));
        Assert.Equal(ConstitutionProcess.States.AwaitLimitsValidated, await StateAsync(procA));
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handlerA, Event(procA, ConstitutionProcess.LimitsValidated)));
        Assert.Equal(ConstitutionProcess.States.Approved, await StateAsync(procA));

        // Order B (the COMMON one): limits first — the previously-unexercised reverse order that
        // used to poison the later-arriving BalanceReserved. Same destination, no NoTransition.
        var handlerB = NewHandler(new RecordingCommandSink());
        var procB = await StartSagaWithReferencesAsync(Guid.NewGuid(), amountCents: 100_00);
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handlerB, Event(procB, ConstitutionProcess.LimitsValidated)));
        Assert.Equal(ConstitutionProcess.States.AwaitBalanceReserved, await StateAsync(procB));
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handlerB, Event(procB, ConstitutionProcess.BalanceReserved)));
        Assert.Equal(ConstitutionProcess.States.Approved, await StateAsync(procB));
    }

    [Fact]
    public async Task A_redelivered_message_id_is_a_no_op_effectively_once()
    {
        var handler = NewHandler(new RecordingCommandSink());

        // Edge-start the saga (the sole start path), then redeliver an ADVANCE event: the consume
        // loop's dedup is what effectively-once guards. The start itself contributes one history row.
        var processId = await StartSagaWithReferencesAsync(Guid.NewGuid(), amountCents: 100_00);

        var advance = Event(processId, ConstitutionProcess.BalanceReserved);
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handler, advance));
        Assert.Equal(ConstitutionProcess.States.AwaitLimitsValidated, await StateAsync(processId));

        // The SAME message_id redelivered: dedup short-circuits the advance — no second move.
        Assert.Equal(AdvanceOutcome.Duplicate, await RunAsync(handler, advance));
        Assert.Equal(ConstitutionProcess.States.AwaitLimitsValidated, await StateAsync(processId));

        // Two transition rows — the edge START and the single advance; the redelivery added none.
        var history = await HistoryAsync(processId);
        Assert.Equal(2, history.Length);
    }

    [Fact]
    public async Task Early_compensation_path_persists_and_reaches_CANCELLED()
    {
        var sink = new RecordingCommandSink();
        var handler = NewHandler(sink);

        var processId = await StartSagaWithReferencesAsync(Guid.NewGuid(), amountCents: 100_00);
        // A product-limit rejection drives the early compensation (Document 05 Scenario A).
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handler, Event(processId, ConstitutionProcess.LimitsRejected)));
        Assert.Equal(ConstitutionProcess.States.CompensateValidations, await StateAsync(processId));
        // Compensation is a DOMAIN command, not a rollback (ADR-IC-003 §P6).
        Assert.Contains("ReleaseBalanceReservation", sink.Emitted.Select(c => c.CommandType));

        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handler, Event(processId, ConstitutionProcess.ReservationReleased)));
        Assert.Equal(ConstitutionProcess.States.Cancelled, await StateAsync(processId));
    }

    [Fact]
    public async Task A_precondition_refusal_reaches_DEPOSIT_CONSTITUTION_FAILED_with_no_reversal()
    {
        // H.2: a PreconditionRefused during validation lands the saga in the terminal
        // DEPOSIT_CONSTITUTION_FAILED state, emitting NO reversal command — nothing reversible was
        // committed, so there is nothing to compensate (a fail-CLOSED before any effect). Driven
        // end-to-end through the real handler + the durable outbox sink.
        var handler = NewHandler(new SagaCommandOutboxSink(_businessRefStore));

        var processId = await StartSagaWithReferencesAsync(Guid.NewGuid(), amountCents: 100_00);
        Assert.Equal(ConstitutionProcess.States.ParallelValidation, await StateAsync(processId));

        Assert.Equal(
            AdvanceOutcome.Advanced,
            await RunAsync(handler, Event(processId, ConstitutionProcess.PreconditionRefused)));
        Assert.Equal(ConstitutionProcess.States.DepositConstitutionFailed, await StateAsync(processId));

        // The ONLY outbox rows are the two validation commands from the start — the refusal added
        // NO reversal (no ReleaseBalanceReservation, no ReverseCoreDebit, nothing).
        var commands = await OutboxCommandsAsync(processId);
        Assert.Equal(
            new[] { ConstitutionProcess.ReserveAccountBalance, ConstitutionProcess.ValidateProductLimits },
            commands);

        // Terminal: a late event for the failed saga is a no-op (dedup'd, state unchanged).
        Assert.Equal(
            AdvanceOutcome.Terminal,
            await RunAsync(handler, Event(processId, ConstitutionProcess.ProcessConstituted)));
        Assert.Equal(ConstitutionProcess.States.DepositConstitutionFailed, await StateAsync(processId));
    }

    [Fact]
    public async Task A_failed_compensation_escalates_to_HUMAN_INTERVENTION_REQUIRED()
    {
        var handler = NewHandler(new RecordingCommandSink());

        var processId = await StartSagaWithReferencesAsync(Guid.NewGuid(), amountCents: 100_00);
        await RunAsync(handler, Event(processId, ConstitutionProcess.LimitsRejected));
        // The compensation itself fails (the ACL reported INDETERMINATE): the saga escalates
        // rather than swallowing the failure (ADR-IC-003 §P6).
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handler, Event(processId, ConstitutionProcess.CompensationFailed)));
        Assert.Equal(ConstitutionProcess.States.HumanInterventionRequired, await StateAsync(processId));
    }

    [Fact]
    public async Task An_event_for_a_terminal_saga_is_a_no_op()
    {
        var handler = NewHandler(new RecordingCommandSink());

        var processId = await StartSagaWithReferencesAsync(Guid.NewGuid(), amountCents: 100_00);
        await RunAsync(handler, Event(processId, ConstitutionProcess.LimitsRejected));
        await RunAsync(handler, Event(processId, ConstitutionProcess.ReservationReleased));
        Assert.Equal(ConstitutionProcess.States.Cancelled, await StateAsync(processId));

        // A late event for the now-terminal saga: dedup'd, recorded as a no-op, state unchanged.
        Assert.Equal(AdvanceOutcome.Terminal, await RunAsync(handler, Event(processId, ConstitutionProcess.ProcessConstituted)));
        Assert.Equal(ConstitutionProcess.States.Cancelled, await StateAsync(processId));
    }

    [Fact]
    public async Task An_illegal_transition_is_rejected_not_applied()
    {
        var handler = NewHandler(new RecordingCommandSink());

        var processId = await StartSagaWithReferencesAsync(Guid.NewGuid(), amountCents: 100_00);
        // DebitConfirmed out of PARALLEL_VALIDATION is not in the table (§P2): rejected.
        Assert.Equal(AdvanceOutcome.NoTransition, await RunAsync(handler, Event(processId, ConstitutionProcess.DebitConfirmed)));
        // State unchanged — the illegal event never moved the saga.
        Assert.Equal(ConstitutionProcess.States.ParallelValidation, await StateAsync(processId));
    }

    [Fact]
    public async Task An_event_for_an_unknown_saga_is_rejected()
    {
        var handler = NewHandler(new RecordingCommandSink());
        // No saga was ever started for this process id; a non-start event has nothing to drive.
        Assert.Equal(AdvanceOutcome.UnknownSaga,
            await RunAsync(handler, Event(Guid.NewGuid(), ConstitutionProcess.BalanceReserved)));
    }

    [Fact]
    public async Task The_optimistic_concurrency_guard_rejects_a_stale_advance()
    {
        // ADR-IC-003 §P1 / §Residual "Concurrent writer race": the WHERE version = ? predicate
        // rejects a writer that read a now-stale version. Two transactions both read version 1;
        // the first advances it to 2, the second's advance-against-1 matches zero rows.
        var processId = await StartSagaWithReferencesAsync(Guid.NewGuid(), amountCents: 100_00); // version → 1

        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();

        var saga = await _stateStore.LoadAsync(connection, tx, processId);
        Assert.NotNull(saga);

        // First advance against the read version wins.
        Assert.True(await _stateStore.TryAdvanceAsync(connection, tx, processId, saga!.Version, ConstitutionProcess.States.ValidationsComplete));
        // A SECOND advance against the SAME (now stale) version matches zero rows — rejected.
        Assert.False(await _stateStore.TryAdvanceAsync(connection, tx, processId, saga.Version, ConstitutionProcess.States.Approved));

        await tx.RollbackAsync();
    }

    // --- helpers -----------------------------------------------------------------------

    private SagaAdvanceHandler NewHandler(ISagaCommandSink sink) =>
        new(_machine, _stateStore, _transitionLog, sink);

    private static SagaInboxEvent Event(Guid processId, string eventType, Guid? correlationId = null) =>
        new(Guid.NewGuid(), processId, eventType, "deposits.process.events", correlationId);

    // Start the saga through the REAL edge starter (the sole start path, bd babelstone-t7o3.9): creates
    // the STARTED row, pins the references, drives STARTED + ConstitutionRequested → PARALLEL_VALIDATION
    // (emitting the two parallel commands), all atomic. Returns the minted internal process id.
    private async Task<Guid> StartSagaWithReferencesAsync(Guid correlationId, long amountCents)
    {
        var sink = new SagaCommandOutboxSink(_businessRefStore);
        var starter = new EdgeSagaStarter(_machine, _stateStore, _transitionLog, sink, _businessRefStore)
        {
            StartEventType = ConstitutionProcess.ConstitutionRequested,
        };

        var result = await starter.StartAsync(
            fixture.ConnectionString,
            owningClientId: "CLI-2026-007842",
            new EdgeBusinessFacts(
                ProductRef: "TD-TRAD-12M",
                AmountMinorUnits: amountCents,
                SourceAccountRef: "acct-ref-001",
                InterestAccountRef: null,
                ClientType: ClientType.Existing,
                AutoApprovalThresholdMinorUnits: ThresholdCents),
            correlationId);

        Assert.Equal(ConstitutionProcess.States.ParallelValidation, result.State);
        return result.ProcessId;
    }

    private async Task<AdvanceOutcome> RunAsync(SagaAdvanceHandler handler, SagaInboxEvent message)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var outcome = await handler.AdvanceAsync(connection, tx, message);
        await tx.CommitAsync();
        return outcome;
    }

    private async Task<string> StateAsync(Guid processId)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var saga = await _stateStore.LoadAsync(connection, tx, processId);
        await tx.RollbackAsync();
        return saga!.State;
    }

    private async Task<(string From, string To)[]> HistoryAsync(Guid processId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT from_state, to_state FROM saga_transition WHERE process_id = @p ORDER BY id;", connection);
        command.Parameters.AddWithValue("p", processId);

        var rows = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return [.. rows];
    }

    private async Task<string[]> OutboxCommandsAsync(Guid processId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT command_type FROM saga_outbox WHERE process_id = @p ORDER BY seq;", connection);
        command.Parameters.AddWithValue("p", processId);

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return [.. rows];
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string table)
    {
        // ::text — Npgsql cannot read a bare regclass as object; the cast yields the
        // qualified table name (or NULL when the relation does not exist).
        await using var command = new NpgsqlCommand("SELECT to_regclass(@t)::text;", connection);
        command.Parameters.AddWithValue("t", table);
        return await command.ExecuteScalarAsync() is not (null or DBNull);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}

/// <summary>Shares one PostgreSQL container across the integration test class (xUnit
/// collection fixture).</summary>
[CollectionDefinition(nameof(OrchestratorPostgresCollection))]
public sealed class OrchestratorPostgresCollection : ICollectionFixture<OrchestratorPostgresFixture>;
