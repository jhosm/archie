using System.Net;
using Babelstone.Orchestrator.Commands;
using Babelstone.Orchestrator.Dispatch;
using Babelstone.Orchestrator.Edge;
using Babelstone.Orchestrator.Handlers;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Outbox;
using Babelstone.Orchestrator.Saga;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// The end-to-end proof of Scenario C — indeterminate Core debit clearance (Document 05 §"Scenario C";
/// bd babelstone-t7o3.10). When the network drops after a ConfirmDebit, the Core ACL returns an EXPLICIT
/// INDETERMINATE signal (HTTP 202): the debit was accepted but it is UNKNOWN whether the Core executed
/// it. The saga must NOT blind-retry (that could double-debit) — it parks in the first-class waiting
/// state AWAIT_CORE_CLEARANCE (ADR-IC-003 §P4 — a long wait is a named state, never a busy retry),
/// emitting a single clearance QUERY (QueryCoreDebitStatus)
/// to the ACL. The clearance result then either RESUMES the happy path (the debit DID execute → a late
/// DebitConfirmed → APPROVED → COMPLETED) or FAILS the saga CLOSED (the debit did NOT execute →
/// DebitNotExecuted → DEPOSIT_CONSTITUTION_FAILED, no money moved, no reversal).
/// </summary>
/// <remarks>
/// <para>
/// These assert on saga STATE progression (read off <c>saga_state</c>), driven by the real dispatcher
/// loop + the real <see cref="SagaAdvanceHandler"/> + the real saga stores against a migrated PostgreSQL,
/// with the settlement target a WireMock-stubbed Core ACL. The saga is started through the REAL
/// <see cref="EdgeSagaStarter"/>, so the whole path runs as it would in production. The new
/// AWAIT_CORE_CLEARANCE entry/exit edges and the QueryCoreDebitStatus clearance leg are exercised
/// end-to-end, not just at the table level (which <c>ConstitutionProcessTests</c> already pins).
/// </para>
/// <para>
/// <b>Extraction-ready (ADR-PC-019 §P2).</b> WireMock.Net is TEST-only; no engine-kernel reference. A
/// DEDICATED Postgres container isolates this class's rows so the dispatcher drains only what it seeds.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SagaAwaitCoreClearanceIntegrationTests : IAsyncLifetime
{
    private const long ThresholdCents = 25_000_00;

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private WireMockServer _acl = null!;

    private string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await new Migrations.MigrationRunner(ConnectionString).ApplyAsync();
        _acl = WireMockServer.Start();

        // ReserveAccountBalance accepts (the reversible hold succeeds — the saga reaches APPROVED).
        _acl.Given(Request.Create().WithPath("/v1/reservations").UsingPost())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.Created).WithBody("""{"reservation":"held"}"""));

        // ConfirmDebit → the EXPLICIT INDETERMINATE signal (HTTP 202): the ACL accepted the debit but
        // cannot confirm Core execution (the network dropped). The dispatcher classifies 202 on a
        // ConfirmDebit as Indeterminate → the bridge synthesizes CoreDebitIndeterminate → AWAIT_CORE_CLEARANCE.
        _acl.Given(Request.Create().WithPath("/v1/debits").UsingPost())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.Accepted)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"status":"INDETERMINATE"}"""));

        // The clearance branch is configured per-test (executed → 200, not-executed → 422) in each Fact.
    }

    public async Task DisposeAsync()
    {
        _acl.Stop();
        await _pg.DisposeAsync();
    }

    [Fact]
    public async Task Indeterminate_debit_parks_in_AWAIT_CORE_CLEARANCE_then_RESUMES_to_COMPLETED_when_clearance_finds_it_executed()
    {
        // Scenario C, the resume branch. ConfirmDebit returns 202 INDETERMINATE → the saga parks in
        // AWAIT_CORE_CLEARANCE and emits QueryCoreDebitStatus. The clearance query finds the debit DID
        // execute (200) → a LATE DebitConfirmed resumes the happy path (APPROVED → ActivateDeposit) and
        // the engine event completes it.
        _acl.Given(Request.Create().WithPath("/v1/debits/clearance").UsingPost())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.OK)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"status":"EXECUTED","core_txn_id":"CT-CLEARED"}"""));

        var processId = await StartSagaAsync(amountCents: 10_000_00);

        // The engine stub accepts the (late) activation — the happy path after the resume.
        await using var engine = new RecordingHttpServer(_ => (HttpStatusCode.Created, "{}"));

        using var host = BuildHost(engineBaseUrl: engine.BaseUrl, settlementBaseUrl: _acl.Url!);
        await host.StartAsync();
        try
        {
            // The saga must first PARK in AWAIT_CORE_CLEARANCE (the indeterminate debit entry), with the
            // clearance query QueryCoreDebitStatus emitted (and then delivered) — never a blind retry.
            await WaitUntilAsync(
                async () => (await OutboxStatusesAsync(processId)).ContainsKey(ConstitutionProcess.QueryCoreDebitStatus),
                TimeSpan.FromSeconds(60),
                "the saga did not arm the clearance query on the indeterminate debit");

            // The clearance found it executed → the saga resumes to APPROVED and the engine activation
            // (201) lands; it then parks at APPROVED until the engine's ProcessConstituted event (slot 2),
            // which we inject to stand in for the consume loop.
            await WaitUntilAsync(
                async () => await StateAsync(processId) == SagaState.Approved
                    && (await OutboxStatusesAsync(processId)).TryGetValue(
                        ConstitutionProcess.ActivateDeposit, out var s) && s == "PUBLISHED",
                TimeSpan.FromSeconds(60),
                "the saga did not resume from AWAIT_CORE_CLEARANCE to APPROVED with ActivateDeposit delivered");

            await InjectEventAsync(processId, ConstitutionProcess.ProcessConstituted);
        }
        finally
        {
            await host.StopAsync();
        }

        // The saga resumed and completed — the late debit confirmation walked it home.
        Assert.Equal(SagaState.Completed, await StateAsync(processId));

        var rows = await OutboxStatusesAsync(processId);
        // The indeterminate ConfirmDebit row is terminal-as-delivered (PUBLISHED) — the command WAS
        // delivered; only the Core execution was unknown, which the clearance resolved.
        Assert.Equal("PUBLISHED", rows[ConstitutionProcess.ConfirmDebit]);
        // The clearance query was delivered (the executed 200 → late DebitConfirmed).
        Assert.Equal("PUBLISHED", rows[ConstitutionProcess.QueryCoreDebitStatus]);
        Assert.Equal("PUBLISHED", rows[ConstitutionProcess.ActivateDeposit]);

        // The clearance query genuinely reached the ACL's clearance route.
        Assert.Contains(AclRequests(), r => r.Path == "/v1/debits/clearance");
    }

    [Fact]
    public async Task Indeterminate_debit_fails_CLOSED_to_DEPOSIT_CONSTITUTION_FAILED_when_clearance_finds_it_not_executed()
    {
        // Scenario C, the fail branch. ConfirmDebit returns 202 INDETERMINATE → AWAIT_CORE_CLEARANCE,
        // QueryCoreDebitStatus emitted. The clearance query finds the debit did NOT execute (422) → the
        // saga fails CLOSED to DEPOSIT_CONSTITUTION_FAILED with NO reversal (no money moved).
        _acl.Given(Request.Create().WithPath("/v1/debits/clearance").UsingPost())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.UnprocessableEntity)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"status":"NOT_EXECUTED"}"""));

        var processId = await StartSagaAsync(amountCents: 10_000_00);

        // No engine activation is reached on this path; an unreachable engine URL proves it.
        using var host = BuildHost(engineBaseUrl: "http://engine.invalid", settlementBaseUrl: _acl.Url!);
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () => await StateAsync(processId) == SagaState.DepositConstitutionFailed,
                TimeSpan.FromSeconds(60),
                "the saga did not fail closed to DEPOSIT_CONSTITUTION_FAILED on a not-executed clearance");
        }
        finally
        {
            await host.StopAsync();
        }

        // The saga rests in the no-money-moved terminal — distinct from CANCELLED_AFTER_DEBIT (which
        // means money DID move and was reversed): here nothing was committed, so nothing is compensated.
        Assert.Equal(SagaState.DepositConstitutionFailed, await StateAsync(processId));

        var rows = await OutboxStatusesAsync(processId);
        Assert.Equal("PUBLISHED", rows[ConstitutionProcess.ConfirmDebit]);
        // The clearance query was a 422 → terminal FAILED (the dispatcher's slot-5 classification),
        // which the bridge mapped to DebitNotExecuted (REVIEW-FLAG C: a v1 stub convention).
        Assert.Equal("FAILED", rows[ConstitutionProcess.QueryCoreDebitStatus]);
        // NO reversal command was emitted — there is nothing to compensate.
        Assert.DoesNotContain(ConstitutionProcess.ReverseCoreDebit, rows.Keys);

        Assert.Contains(AclRequests(), r => r.Path == "/v1/debits/clearance");
    }

    // ---- Host wiring (the FULL production composition: dispatcher + bridge + saga stores) -----------

    private IHost BuildHost(string engineBaseUrl, string settlementBaseUrl)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(new SagaCommandDispatcherOptions
        {
            ConnectionString = ConnectionString,
            EngineBaseUrl = engineBaseUrl,
            SettlementBaseUrl = settlementBaseUrl,
            PollInterval = TimeSpan.FromMilliseconds(100),
        });
        builder.Services.AddSingleton<ICommandRouter, SagaCommandRouter>();
        builder.Services.AddHttpClient();

        builder.Services.AddSingleton<ISagaStateMachine, ConstitutionProcess>();
        builder.Services.AddSingleton<SagaStateStore>();
        builder.Services.AddSingleton<SagaTransitionLog>();
        builder.Services.AddSingleton<SagaBusinessReferenceStore>();
        builder.Services.AddSingleton<ISagaCommandSink>(sp =>
            new SagaCommandOutboxSink(sp.GetRequiredService<SagaBusinessReferenceStore>()));
        builder.Services.AddSingleton(sp => new SagaAdvanceHandler(
            sp.GetRequiredService<ISagaStateMachine>(),
            sp.GetRequiredService<SagaStateStore>(),
            sp.GetRequiredService<SagaTransitionLog>(),
            sp.GetRequiredService<ISagaCommandSink>(),
            sp.GetRequiredService<SagaBusinessReferenceStore>()));

        builder.Services.AddSingleton(sp => new SagaCommandDispatchDrainer(
            sp.GetRequiredService<SagaCommandDispatcherOptions>(),
            sp.GetRequiredService<ICommandRouter>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<SagaAdvanceHandler>()));
        builder.Services.AddHostedService<SagaCommandDispatcherService>();
        return builder.Build();
    }

    // ---- Seed / drive helpers ----------------------------------------------------------------------

    private async Task<Guid> StartSagaAsync(long amountCents)
    {
        var machine = new ConstitutionProcess();
        var stateStore = new SagaStateStore();
        var transitionLog = new SagaTransitionLog();
        var businessRefStore = new SagaBusinessReferenceStore();
        var sink = new SagaCommandOutboxSink(businessRefStore);
        var starter = new EdgeSagaStarter(machine, stateStore, transitionLog, sink, businessRefStore)
        {
            StartEventType = ConstitutionProcess.ConstitutionRequested,
        };

        var result = await starter.StartAsync(
            ConnectionString,
            owningClientId: "CLI-2026-007842",
            new EdgeBusinessFacts(
                ProductRef: "TD-TRAD-12M",
                AmountMinorUnits: amountCents,
                SourceAccountRef: "acct-ref-001",
                InterestAccountRef: null,
                ClientType: ClientType.Existing,
                AutoApprovalThresholdMinorUnits: ThresholdCents),
            correlationId: Guid.NewGuid());

        Assert.Equal(SagaState.ParallelValidation, result.State);
        return result.ProcessId;
    }

    private async Task InjectEventAsync(Guid processId, string eventType)
    {
        var machine = new ConstitutionProcess();
        var handler = new SagaAdvanceHandler(
            machine, new SagaStateStore(), new SagaTransitionLog(),
            new SagaCommandOutboxSink(new SagaBusinessReferenceStore()), new SagaBusinessReferenceStore());
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await handler.AdvanceAsync(connection, tx,
            new SagaInboxEvent(Guid.NewGuid(), processId, eventType, "test.injected", CorrelationId: null));
        await tx.CommitAsync();
    }

    private async Task<SagaState> StateAsync(Guid processId)
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var saga = await new SagaStateStore().LoadAsync(connection, tx, processId);
        await tx.RollbackAsync();
        return saga!.State;
    }

    private async Task<IReadOnlyDictionary<string, string>> OutboxStatusesAsync(Guid processId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT command_type, status FROM saga_outbox WHERE process_id = @p ORDER BY seq;", connection);
        command.Parameters.AddWithValue("p", processId);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            map[reader.GetString(0)] = reader.GetString(1);
        }

        return map;
    }

    private IReadOnlyList<AclRequest> AclRequests() =>
        _acl.LogEntries
            .Select(e => new AclRequest(
                Path: e.RequestMessage?.Path ?? string.Empty,
                Method: e.RequestMessage?.Method ?? string.Empty))
            .ToList();

    private sealed record AclRequest(string Path, string Method);

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string failureMessage)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out after {timeout.TotalSeconds}s: {failureMessage}.");
    }
}
