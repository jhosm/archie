using System.Text.Json;
using Babelstone.Orchestrator.Edge;
using Babelstone.Families.TermDeposit.Orchestration;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Saga;
using Npgsql;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// The bd babelstone-t7o3.1 invariants, driven against a real PostgreSQL: when the parallel
/// validations complete, the orchestrator SELF-EMITS the approval fork's chosen event into its own
/// advance loop — crossing the saga into APPROVED (the auto-approve path) or AWAIT_WORKFLOW_APPROVAL
/// (the route-to-workflow path) WITHOUT an external trigger — and the saga's outbox carries the FULL
/// typed command payloads (the real source account, deposit, and derived references from the pinned
/// business references), not the seam-level envelope.
/// </summary>
/// <remarks>
/// <para>
/// The self-emit rides nothing on the durable bus (ADR-IC-003 §S2): it is the impure shell feeding
/// the fork's chosen event back into the SAME advance transaction. The fork DECISION stays pure
/// (ApprovalForkHandler.Decide on the edge-pinned amount/threshold/client). The full payloads are
/// byte-stable and PII-free (ADR-PC-004 §P2 / ADR-PC-010 §P5).
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(nameof(OrchestratorPostgresCollection))]
public sealed class SagaForkSelfEmitIntegrationTests(OrchestratorPostgresFixture fixture)
{
    private const long ThresholdCents = 25_000_00;

    private readonly ConstitutionProcess _machine = new();
    private readonly SagaStateStore _stateStore = new();
    private readonly SagaTransitionLog _transitionLog = new();
    private readonly SagaBusinessReferenceStore _businessRefStore = new();

    [Fact]
    public async Task Migration_creates_the_saga_business_ref_table()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass('saga_business_ref')::text;", connection);
        Assert.False(await command.ExecuteScalarAsync() is null or DBNull);
    }

    [Fact]
    public async Task Both_validations_complete_self_emits_ConstitutionApproved_and_crosses_to_APPROVED()
    {
        // An EXISTING client well under the pinned threshold → the fork decides AUTO-APPROVE, so the
        // orchestrator self-emits ConstitutionApproved when both validations land and the saga crosses
        // VALIDATIONS_COMPLETE → APPROVED on its own (emitting ConfirmDebit) — NO external approval
        // event was delivered.
        var correlationId = Guid.NewGuid();
        var handler = NewHandler(new SagaCommandOutboxSink(_businessRefStore));

        // The edge starts the saga (STARTED → PARALLEL_VALIDATION) with the references pinned.
        var processId = await StartSagaWithReferencesAsync(correlationId, amountCents: 10_000_00, ClientType.Existing);

        // Both validations arrive (limits first, the common order). The SECOND one completes the join
        // into VALIDATIONS_COMPLETE — and the self-emit must, in that SAME advance, cross to APPROVED.
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handler, Event(processId, ConstitutionProcess.LimitsValidated)));
        Assert.Equal(ConstitutionProcess.States.AwaitBalanceReserved, await StateAsync(processId));
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handler, Event(processId, ConstitutionProcess.BalanceReserved)));

        // No external ConstitutionApproved was delivered — yet the saga is in APPROVED.
        Assert.Equal(ConstitutionProcess.States.Approved, await StateAsync(processId));

        // The transition history records the self-emitted fork crossing.
        var history = await HistoryAsync(processId);
        Assert.Contains(("VALIDATIONS_COMPLETE", "APPROVED"), history);

        // The auto-approve crossing emitted ConfirmDebit (the first irreversible command) — and the
        // outbox commands are the parallel validations + the self-emitted ConfirmDebit.
        var commands = await OutboxCommandTypesAsync(processId);
        Assert.Equal(
            new[]
            {
                ConstitutionProcess.ReserveAccountBalance,
                ConstitutionProcess.ValidateProductLimits,
                ConstitutionProcess.ConfirmDebit,
            },
            commands);
    }

    [Fact]
    public async Task The_emitted_payloads_carry_the_real_business_references_not_the_seam_envelope()
    {
        // The ReserveAccountBalance / ValidateProductLimits / ConfirmDebit payloads must carry the
        // FULL typed business references pinned at start — the real source account, the deposit
        // reference, derived reservation/hold references — NOT the minimal seam envelope (which has
        // only ProcessId/CommandType/identity-trio). This is the "full business-reference payloads"
        // half of bd babelstone-t7o3.1.
        var correlationId = Guid.NewGuid();
        const string sourceAccountRef = "acct-ref-XYZ-001";
        var handler = NewHandler(new SagaCommandOutboxSink(_businessRefStore));

        var processId = await StartSagaWithReferencesAsync(
            correlationId, amountCents: 10_000_00, ClientType.Existing, sourceAccountRef);

        await RunAsync(handler, Event(processId, ConstitutionProcess.LimitsValidated));
        await RunAsync(handler, Event(processId, ConstitutionProcess.BalanceReserved));
        Assert.Equal(ConstitutionProcess.States.Approved, await StateAsync(processId));

        var rows = await OutboxRowsAsync(processId);

        // ReserveAccountBalance carries the REAL source account + a derived reservation ref — fields
        // the seam envelope does NOT have.
        var reserve = ParseBody(rows.Single(r => r.CommandType == ConstitutionProcess.ReserveAccountBalance).Payload);
        // The funding-leg references serialize snake_case on the settlement/ingress wire.
        Assert.Equal(sourceAccountRef, reserve.GetProperty("account_ref").GetString());
        Assert.True(reserve.TryGetProperty("reservation_ref", out var reservationRef));
        Assert.StartsWith("RSV-", reservationRef.GetString());

        // ValidateProductLimits carries the deposit + product references.
        var validate = ParseBody(rows.Single(r => r.CommandType == ConstitutionProcess.ValidateProductLimits).Payload);
        Assert.StartsWith("DEP-", validate.GetProperty("DepositRef").GetString());
        Assert.Equal("TD-TRAD-12M", validate.GetProperty("ProductRef").GetString());

        // ConfirmDebit (the self-emitted irreversible command) carries the Core hold reference.
        var confirm = ParseBody(rows.Single(r => r.CommandType == ConstitutionProcess.ConfirmDebit).Payload);
        Assert.StartsWith("CORE-HOLD-", confirm.GetProperty("core_hold_ref").GetString());
        // This is a LEGACY funding account (a non-GUID token), so the engine-CA funding extras serialize as
        // explicit nulls and the leg is not settlement-target-tagged — the logical command and its
        // replay-stability are unchanged from the pre-ADR-PC-043 legacy path.
        Assert.True(confirm.GetProperty("account_ref").ValueKind == System.Text.Json.JsonValueKind.Null);
        Assert.True(confirm.GetProperty("settlement_target").ValueKind == System.Text.Json.JsonValueKind.Null);

        // No row is the bare seam envelope: every body carries at least one business-reference field
        // beyond the seam's {ProcessId, CommandType, CausationMessageId, CorrelationId}.
        var seamOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            "$type", "ProcessId", "CommandType", "CausationMessageId", "CorrelationId",
        };
        foreach (var row in rows)
        {
            var body = ParseBody(row.Payload);
            var hasBusinessField = body.EnumerateObject().Any(p => !seamOnly.Contains(p.Name));
            Assert.True(hasBusinessField, $"{row.CommandType} wrote the seam envelope, not a full payload.");
        }

        // No PII (ADR-PC-004): a positive allow-list over every property name in every body. The funding
        // legs serialize their references snake_case on the settlement/ingress wire and carry the engine-CA
        // funding extras (all STRUCTURAL — the promoted destination account_ref, the hold-linking intent
        // reference, the integer-cents amount, the counterparty discriminator); the legacy compensation/
        // clearance legs keep the PascalCase forms.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "$type", "ProcessId", "CommandType", "CausationMessageId", "CorrelationId",
            "account_ref", "reservation_ref", "core_hold_ref", "intent_reference", "amount_cents", "settlement_target",
            "AccountRef", "ReservationRef", "DepositRef", "ProductRef", "CoreHoldRef", "CoreTxnRef",
        };
        foreach (var row in rows)
        {
            foreach (var property in ParseBody(row.Payload).EnumerateObject())
            {
                Assert.Contains(property.Name, allowed);
            }
        }
    }

    [Fact]
    public async Task An_amount_over_the_threshold_self_emits_WorkflowApprovalRequired_and_waits()
    {
        // An amount ABOVE the pinned threshold → the fork decides ROUTE-TO-WORKFLOW, so the
        // orchestrator self-emits WorkflowApprovalRequired and the saga crosses VALIDATIONS_COMPLETE →
        // AWAIT_WORKFLOW_APPROVAL — a first-class wait, NOT APPROVED — without an external trigger.
        var correlationId = Guid.NewGuid();
        var handler = NewHandler(new SagaCommandOutboxSink(_businessRefStore));

        var processId = await StartSagaWithReferencesAsync(correlationId, amountCents: ThresholdCents + 1, ClientType.Existing);

        await RunAsync(handler, Event(processId, ConstitutionProcess.BalanceReserved));
        await RunAsync(handler, Event(processId, ConstitutionProcess.LimitsValidated));

        // The over-threshold amount routed to the workflow — the saga is WAITING, not APPROVED.
        Assert.Equal(ConstitutionProcess.States.AwaitWorkflowApproval, await StateAsync(processId));

        // No ConfirmDebit was emitted (the irreversible phase is gated behind the workflow approval).
        var commands = await OutboxCommandTypesAsync(processId);
        Assert.DoesNotContain(ConstitutionProcess.ConfirmDebit, commands);

        // An external approval event then resumes it into APPROVED (the workflow-approved path).
        Assert.Equal(
            AdvanceOutcome.Advanced,
            await RunAsync(handler, Event(processId, ConstitutionProcess.ConstitutionApproved)));
        Assert.Equal(ConstitutionProcess.States.Approved, await StateAsync(processId));
    }

    [Fact]
    public async Task The_self_emit_is_effectively_once_a_redelivered_join_does_not_re_fork()
    {
        // The self-emit dedups through the SAME inbox as an external advance (the deterministic
        // message id). A redelivered completing validation must NOT re-fork the saga or double-emit
        // ConfirmDebit.
        var correlationId = Guid.NewGuid();
        var handler = NewHandler(new SagaCommandOutboxSink(_businessRefStore));

        var processId = await StartSagaWithReferencesAsync(correlationId, amountCents: 10_000_00, ClientType.Existing);
        await RunAsync(handler, Event(processId, ConstitutionProcess.LimitsValidated));

        var completing = Event(processId, ConstitutionProcess.BalanceReserved);
        Assert.Equal(AdvanceOutcome.Advanced, await RunAsync(handler, completing));
        Assert.Equal(ConstitutionProcess.States.Approved, await StateAsync(processId));

        // Redeliver the SAME completing event id: dedup short-circuits — no re-fork, state unchanged.
        Assert.Equal(AdvanceOutcome.Duplicate, await RunAsync(handler, completing));
        Assert.Equal(ConstitutionProcess.States.Approved, await StateAsync(processId));

        // Exactly one ConfirmDebit was emitted (the fork fired once).
        var confirms = (await OutboxCommandTypesAsync(processId))
            .Count(t => t == ConstitutionProcess.ConfirmDebit);
        Assert.Equal(1, confirms);
    }

    // --- helpers -----------------------------------------------------------------------

    private SagaAdvanceHandler NewHandler(ISagaCommandSink sink) =>
        new(_machine, _stateStore, _transitionLog, sink);

    private static SagaInboxEvent Event(Guid processId, string eventType, Guid? correlationId = null) =>
        new(Guid.NewGuid(), processId, eventType, "deposits.process.events", correlationId);

    // Start the saga through the REAL edge starter (mirroring the I.1 front door): it creates the
    // STARTED row, pins the business references, and drives STARTED + ConstitutionRequested →
    // PARALLEL_VALIDATION (emitting the two parallel commands) — all in one transaction. Returns the
    // internal process id the validations then drive.
    private async Task<Guid> StartSagaWithReferencesAsync(
        Guid correlationId, long amountCents, ClientType clientType, string sourceAccountRef = "acct-ref-001")
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
                SourceAccountRef: sourceAccountRef,
                InterestAccountRef: null,
                ClientType: clientType,
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

    private async Task<string[]> OutboxCommandTypesAsync(Guid processId) =>
        (await OutboxRowsAsync(processId)).Select(r => r.CommandType).ToArray();

    private async Task<IReadOnlyList<OutboxRowRead>> OutboxRowsAsync(Guid processId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT command_type, payload FROM saga_outbox WHERE process_id = @p ORDER BY seq;", connection);
        command.Parameters.AddWithValue("p", processId);

        var rows = new List<OutboxRowRead>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new OutboxRowRead(reader.GetString(0), (byte[])reader[1]));
        }

        return rows;
    }

    private static JsonElement ParseBody(byte[] payload) => JsonDocument.Parse(payload).RootElement.Clone();

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private sealed record OutboxRowRead(string CommandType, byte[] Payload);
}
