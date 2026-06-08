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
    private readonly ConstitutionProcess _machine = new();
    private readonly SagaStateStore _stateStore = new();
    private readonly SagaTransitionLog _transitionLog = new();

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
        var processId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var sink = new RecordingCommandSink();
        var handler = NewHandler(sink);

        // STARTED + ConstitutionRequested → PARALLEL_VALIDATION, emitting the two parallel commands.
        Assert.Equal(AdvanceOutcome.Started,
            await RunAsync(handler, Event(processId, ConstitutionProcess.ConstitutionRequested, correlationId)));
        Assert.Equal(SagaState.ParallelValidation, await StateAsync(processId));
        Assert.Equal(
            new[] { "ReserveAccountBalance", "ValidateProductLimits" },
            sink.Emitted.Select(c => c.CommandType).ToArray());
        // The identity trio rides the emission (ADR-IC-003 §P7): correlation carried through.
        Assert.All(sink.Emitted, c => Assert.Equal(correlationId, c.CorrelationId));

        // Both validations (balance first → AWAIT_LIMITS_VALIDATED → join), approval, debit,
        // activation, close.
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handler, Event(processId, ConstitutionProcess.BalanceReserved)));
        Assert.Equal(SagaState.AwaitLimitsValidated, await StateAsync(processId));
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handler, Event(processId, ConstitutionProcess.LimitsValidated)));
        Assert.Equal(SagaState.ValidationsComplete, await StateAsync(processId));
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handler, Event(processId, ConstitutionProcess.ConstitutionApproved)));
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handler, Event(processId, ConstitutionProcess.DebitConfirmed)));
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handler, Event(processId, ConstitutionProcess.ProcessConstituted)));

        Assert.Equal(SagaState.Completed, await StateAsync(processId));

        // The append-only transition history records every accepted move, in order.
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

        // Order A: balance first (was the only order the prior suite exercised).
        var procA = Guid.NewGuid();
        var handlerA = NewHandler(new RecordingCommandSink());
        await RunAsync(handlerA, Event(procA, ConstitutionProcess.ConstitutionRequested));
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handlerA, Event(procA, ConstitutionProcess.BalanceReserved)));
        Assert.Equal(SagaState.AwaitLimitsValidated, await StateAsync(procA));
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handlerA, Event(procA, ConstitutionProcess.LimitsValidated)));
        Assert.Equal(SagaState.ValidationsComplete, await StateAsync(procA));

        // Order B (the COMMON one): limits first — the previously-unexercised reverse order that
        // used to poison the later-arriving BalanceReserved. Same destination, no NoTransition.
        var procB = Guid.NewGuid();
        var handlerB = NewHandler(new RecordingCommandSink());
        await RunAsync(handlerB, Event(procB, ConstitutionProcess.ConstitutionRequested));
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handlerB, Event(procB, ConstitutionProcess.LimitsValidated)));
        Assert.Equal(SagaState.AwaitBalanceReserved, await StateAsync(procB));
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handlerB, Event(procB, ConstitutionProcess.BalanceReserved)));
        Assert.Equal(SagaState.ValidationsComplete, await StateAsync(procB));
    }

    [Fact]
    public async Task A_redelivered_message_id_is_a_no_op_effectively_once()
    {
        var processId = Guid.NewGuid();
        var handler = NewHandler(new RecordingCommandSink());

        var start = Event(processId, ConstitutionProcess.ConstitutionRequested);
        Assert.Equal(AdvanceOutcome.Started, await RunAsync(handler, start));

        // The SAME message_id redelivered: dedup short-circuits the advance — no second move.
        Assert.Equal(AdvanceOutcome.Duplicate, await RunAsync(handler, start));
        Assert.Equal(SagaState.ParallelValidation, await StateAsync(processId));

        // Exactly one START transition recorded — the redelivery added no history row.
        var history = await HistoryAsync(processId);
        Assert.Single(history);
    }

    [Fact]
    public async Task Early_compensation_path_persists_and_reaches_CANCELLED()
    {
        var processId = Guid.NewGuid();
        var sink = new RecordingCommandSink();
        var handler = NewHandler(sink);

        await RunAsync(handler, Event(processId, ConstitutionProcess.ConstitutionRequested));
        // A product-limit rejection drives the early compensation (Document 05 Scenario A).
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handler, Event(processId, ConstitutionProcess.LimitsRejected)));
        Assert.Equal(SagaState.CompensateValidations, await StateAsync(processId));
        // Compensation is a DOMAIN command, not a rollback (ADR-IC-003 §P6).
        Assert.Contains("ReleaseBalanceReservation", sink.Emitted.Select(c => c.CommandType));

        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handler, Event(processId, ConstitutionProcess.ReservationReleased)));
        Assert.Equal(SagaState.Cancelled, await StateAsync(processId));
    }

    [Fact]
    public async Task A_failed_compensation_escalates_to_HUMAN_INTERVENTION_REQUIRED()
    {
        var processId = Guid.NewGuid();
        var handler = NewHandler(new RecordingCommandSink());

        await RunAsync(handler, Event(processId, ConstitutionProcess.ConstitutionRequested));
        await RunAsync(handler, Event(processId, ConstitutionProcess.LimitsRejected));
        // The compensation itself fails (the ACL reported INDETERMINATE): the saga escalates
        // rather than swallowing the failure (ADR-IC-003 §P6).
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handler, Event(processId, ConstitutionProcess.CompensationFailed)));
        Assert.Equal(SagaState.HumanInterventionRequired, await StateAsync(processId));
    }

    [Fact]
    public async Task An_event_for_a_terminal_saga_is_a_no_op()
    {
        var processId = Guid.NewGuid();
        var handler = NewHandler(new RecordingCommandSink());

        await RunAsync(handler, Event(processId, ConstitutionProcess.ConstitutionRequested));
        await RunAsync(handler, Event(processId, ConstitutionProcess.LimitsRejected));
        await RunAsync(handler, Event(processId, ConstitutionProcess.ReservationReleased));
        Assert.Equal(SagaState.Cancelled, await StateAsync(processId));

        // A late event for the now-terminal saga: dedup'd, recorded as a no-op, state unchanged.
        Assert.Equal(AdvanceOutcome.Terminal, await RunAsync(handler, Event(processId, ConstitutionProcess.ProcessConstituted)));
        Assert.Equal(SagaState.Cancelled, await StateAsync(processId));
    }

    [Fact]
    public async Task An_illegal_transition_is_rejected_not_applied()
    {
        var processId = Guid.NewGuid();
        var handler = NewHandler(new RecordingCommandSink());

        await RunAsync(handler, Event(processId, ConstitutionProcess.ConstitutionRequested));
        // DebitConfirmed out of PARALLEL_VALIDATION is not in the table (§P2): rejected.
        Assert.Equal(AdvanceOutcome.NoTransition, await RunAsync(handler, Event(processId, ConstitutionProcess.DebitConfirmed)));
        // State unchanged — the illegal event never moved the saga.
        Assert.Equal(SagaState.ParallelValidation, await StateAsync(processId));
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
        var processId = Guid.NewGuid();
        var handler = NewHandler(new RecordingCommandSink());
        await RunAsync(handler, Event(processId, ConstitutionProcess.ConstitutionRequested)); // version → 1

        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();

        var saga = await _stateStore.LoadAsync(connection, tx, processId);
        Assert.NotNull(saga);

        // First advance against the read version wins.
        Assert.True(await _stateStore.TryAdvanceAsync(connection, tx, processId, saga!.Version, SagaState.ValidationsComplete));
        // A SECOND advance against the SAME (now stale) version matches zero rows — rejected.
        Assert.False(await _stateStore.TryAdvanceAsync(connection, tx, processId, saga.Version, SagaState.Approved));

        await tx.RollbackAsync();
    }

    // --- helpers -----------------------------------------------------------------------

    private SagaAdvanceHandler NewHandler(ISagaCommandSink sink) =>
        new(_machine, _stateStore, _transitionLog, sink)
        {
            StartEventType = ConstitutionProcess.ConstitutionRequested,
        };

    private static SagaInboxEvent Event(Guid processId, string eventType, Guid? correlationId = null) =>
        new(Guid.NewGuid(), processId, eventType, "deposits.process.events", correlationId);

    private async Task<AdvanceOutcome> RunAsync(SagaAdvanceHandler handler, SagaInboxEvent message)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var outcome = await handler.AdvanceAsync(connection, tx, message);
        await tx.CommitAsync();
        return outcome;
    }

    private async Task<SagaState> StateAsync(Guid processId)
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
