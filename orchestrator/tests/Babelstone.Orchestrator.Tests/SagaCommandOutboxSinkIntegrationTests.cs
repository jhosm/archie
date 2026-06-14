using System.Text.Json;
using Babelstone.Orchestrator.Edge;
using Babelstone.Orchestrator.Handlers;
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
    private const long ThresholdCents = 1_000_00;

    private readonly ConstitutionProcess _machine = new();
    private readonly SagaStateStore _stateStore = new();
    private readonly SagaTransitionLog _transitionLog = new();
    private readonly SagaBusinessReferenceStore _businessRefStore = new();

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
        var correlationId = Guid.NewGuid();

        // The edge is the SOLE saga starter (bd babelstone-t7o3.9): it creates the STARTED row, pins
        // the business references, and drives STARTED + ConstitutionRequested → PARALLEL_VALIDATION,
        // emitting ReserveAccountBalance + ValidateProductLimits — all in one transaction.
        var processId = await StartSagaWithReferencesAsync(correlationId);

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
        // not on the list fails CLOSED. The bodies are now FULL business-reference payloads (the
        // SagaCommandPayloadFactory sets the structural refs), so the allow-list admits the PII-free
        // structural fields the factory writes — every one a token/reference, never a NIF/IBAN/name.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "$type", "ProcessId", "CommandType", "CausationMessageId", "CorrelationId",
            "AccountRef", "ReservationRef", "DepositRef", "ProductRef",
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

        // Seed the saga row so the FK is satisfied, pin the business references the full-payload
        // factory reads (mandatory now — bd babelstone-t7o3.9), then emit the same logical command
        // twice on two separate transactions (distinct delivery message ids).
        await StartBareSagaAsync(processId, correlationId);
        await PinReferencesAsync(processId);

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
        await PinReferencesAsync(processId);

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

    // Start the saga through the REAL edge starter (the sole start path, bd babelstone-t7o3.9): it
    // creates the STARTED row, pins the business references, and drives the first transition (emitting
    // the two parallel commands) — all atomic. Returns the minted internal process id.
    private async Task<Guid> StartSagaWithReferencesAsync(Guid correlationId)
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
                AmountMinorUnits: 100_00,
                SourceAccountRef: "acct-ref-001",
                InterestAccountRef: null,
                ClientType: ClientType.Existing,
                AutoApprovalThresholdMinorUnits: ThresholdCents),
            correlationId);

        Assert.Equal(SagaState.ParallelValidation, result.State);
        return result.ProcessId;
    }

    // Pin a PII-free business-reference row for a saga whose STARTED row was created directly (the
    // bare-start tests that then call sink.EmitAsync directly). References are mandatory for the
    // full-payload factory (bd babelstone-t7o3.9); the FK requires the saga_state row to exist first.
    private Task PinReferencesAsync(Guid processId) =>
        RunHelper((c, tx) => _businessRefStore.TryInsertAsync(
            c, tx,
            new SagaBusinessReference(
                ProcessId: processId,
                ProductRef: "TD-TRAD-12M",
                AmountMinorUnits: 100_00,
                SourceAccountRef: "acct-ref-001",
                InterestAccountRef: null,
                DepositRef: "DEP-" + processId.ToString("N"),
                ClientType: ClientType.Existing,
                AutoApprovalThresholdMinorUnits: ThresholdCents)));

    private async Task RunHelper(Func<NpgsqlConnection, NpgsqlTransaction, Task> body)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await body(connection, tx);
        await tx.CommitAsync();
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
