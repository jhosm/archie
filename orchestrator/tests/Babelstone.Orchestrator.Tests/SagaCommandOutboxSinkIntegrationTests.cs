using System.Text.Json;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Outbox;
using Babelstone.Orchestrator.Saga;
using Npgsql;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// Integration tests for the REAL <see cref="SagaCommandOutboxSink"/> (H.2, babelstone-n55u)
/// against a real PostgreSQL: a command the saga decides becomes a durable <c>saga_outbox</c>
/// row, committed in the SAME transaction as the state move (ADR-IC-003 §P1). These prove the
/// substrate's recorder is replaced by a durable writer whose row is PII-free (a positive
/// allow-list, ADR-PC-004 §P2), whose logical payload is byte-stable across two emissions of the
/// same command (no minted GUID/timestamp inside the body, ADR-PC-010 §P5), and whose ONE freshly
/// minted value — the delivery message id — lives in an OPERATIONAL column, never the body.
/// </summary>
[Trait("Category", "Integration")]
[Collection(nameof(OrchestratorPostgresCollection))]
public sealed class SagaCommandOutboxSinkIntegrationTests(OrchestratorPostgresFixture fixture)
{
    private readonly ConstitutionProcess _machine = new();
    private readonly SagaStateStore _stateStore = new();
    private readonly SagaTransitionLog _transitionLog = new();

    [Fact]
    public async Task Migration_creates_the_saga_outbox_table()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass('saga_outbox')::text;", connection);
        Assert.False(await command.ExecuteScalarAsync() is null or DBNull);
    }

    [Fact]
    public async Task A_started_saga_writes_its_commands_as_durable_PII_free_outbox_rows()
    {
        var processId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var sink = new SagaCommandOutboxSink();
        var handler = NewHandler(sink);

        // STARTED + ConstitutionRequested emits ReserveAccountBalance + ValidateProductLimits.
        Assert.Equal(
            AdvanceOutcome.Started,
            await RunAsync(handler, Event(processId, ConstitutionProcess.ConstitutionRequested, correlationId)));

        var rows = await OutboxRowsAsync(processId);
        Assert.Equal(
            new[] { ConstitutionProcess.ReserveAccountBalance, ConstitutionProcess.ValidateProductLimits },
            rows.Select(r => r.CommandType).ToArray());

        // The identity trio rides every row (ADR-IC-003 §P7): correlation carried through, and the
        // causation id is the triggering event's message id (a reference, not a minted value).
        Assert.All(rows, r => Assert.Equal(correlationId, r.CorrelationId));

        // Each row carries a DISTINCT freshly minted delivery message id — the ONE operational
        // mint, in the COLUMN, not the body.
        Assert.Equal(rows.Count, rows.Select(r => r.MessageId).Distinct().Count());

        // Positive no-PII allow-list (ADR-PC-004 §P2) over the persisted payload bytes: every
        // property name in the durable body must be on the known-PII-free set. A field that is
        // not on the list fails CLOSED.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "ProcessId", "CommandType", "CausationMessageId", "CorrelationId",
        };
        foreach (var row in rows)
        {
            using var document = JsonDocument.Parse(row.Payload);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                Assert.Contains(property.Name, allowed);
            }
        }
    }

    [Fact]
    public async Task The_same_logical_command_emitted_twice_has_byte_identical_payloads()
    {
        // Byte-stability (ADR-PC-010 §P5): two emissions of the SAME logical command (same process
        // id, same command type, same identity trio) produce IDENTICAL payload BYTES — only the
        // operational delivery message id and the created_at column differ. Proven against the
        // persisted rows, NOT a clock_timestamp() column or delivery timing (the must-not).
        var processId = Guid.NewGuid();
        var causationId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var sink = new SagaCommandOutboxSink();

        // Seed the saga row so the FK is satisfied, then emit the same logical command twice on
        // two separate transactions (distinct delivery message ids).
        await StartBareSagaAsync(processId, correlationId);

        await EmitOnceAsync(sink, processId, ConstitutionProcess.ConfirmDebit, causationId, correlationId);
        await EmitOnceAsync(sink, processId, ConstitutionProcess.ConfirmDebit, causationId, correlationId);

        var rows = await OutboxRowsAsync(processId);
        Assert.Equal(2, rows.Count);

        // Two DISTINCT delivery message ids (the operational mint differs)...
        Assert.NotEqual(rows[0].MessageId, rows[1].MessageId);
        // ...but byte-IDENTICAL logical payloads (the body carries no minted value).
        Assert.Equal(rows[0].Payload, rows[1].Payload);
    }

    [Fact]
    public async Task A_rolled_back_transaction_emits_no_outbox_row()
    {
        // The outbox row is written ON the saga transaction (ADR-IC-003 §P1): if the transaction
        // rolls back, no command escapes — atomic with the state move.
        var processId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        await StartBareSagaAsync(processId, correlationId);

        var sink = new SagaCommandOutboxSink();
        await using (var connection = await OpenAsync())
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await sink.EmitAsync(connection, tx, processId, ConstitutionProcess.ConfirmDebit, Guid.NewGuid(), correlationId);
            await tx.RollbackAsync();
        }

        Assert.Empty(await OutboxRowsAsync(processId));
    }

    // --- helpers -----------------------------------------------------------------------

    private SagaAdvanceHandler NewHandler(ISagaCommandSink sink) =>
        new(_machine, _stateStore, _transitionLog, sink)
        {
            StartEventType = ConstitutionProcess.ConstitutionRequested,
        };

    private static SagaInboxEvent Event(Guid processId, string eventType, Guid? correlationId = null) =>
        new(Guid.NewGuid(), processId, eventType, "deposits.process.events", correlationId);

    private async Task RunHelper(Func<NpgsqlConnection, NpgsqlTransaction, Task> body)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await body(connection, tx);
        await tx.CommitAsync();
    }

    private async Task<AdvanceOutcome> RunAsync(SagaAdvanceHandler handler, SagaInboxEvent message)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var outcome = await handler.AdvanceAsync(connection, tx, message);
        await tx.CommitAsync();
        return outcome;
    }

    private Task StartBareSagaAsync(Guid processId, Guid correlationId) =>
        RunHelper((c, tx) => _stateStore.TryStartAsync(
            c, tx, processId, _machine.SagaType, _machine.InitialState, correlationId));

    private Task EmitOnceAsync(SagaCommandOutboxSink sink, Guid processId, string commandType, Guid causationId, Guid correlationId) =>
        RunHelper((c, tx) => sink.EmitAsync(c, tx, processId, commandType, causationId, correlationId));

    private async Task<IReadOnlyList<OutboxRowRead>> OutboxRowsAsync(Guid processId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT message_id, command_type, causation_id, correlation_id, payload FROM saga_outbox " +
            "WHERE process_id = @p ORDER BY seq;", connection);
        command.Parameters.AddWithValue("p", processId);

        var rows = new List<OutboxRowRead>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new OutboxRowRead(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                (byte[])reader[4]));
        }

        return rows;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private sealed record OutboxRowRead(Guid MessageId, string CommandType, Guid CausationId, Guid? CorrelationId, byte[] Payload);
}
