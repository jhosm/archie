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
/// DebitConfirmed → APPROVED → COMPLETED) or REISSUES the debit (the debit did NOT execute →
/// DebitNotExecuted → RETRY_PERMITTED → back to APPROVED with a fresh ConfirmDebit → the reissue succeeds
/// → COMPLETED). The reissue conforms to ADR-IC-012 §D5 step 5 / §P5 (inherited by ADR-PC-016 §64): a
/// not-executed clearance is Core ground truth that nothing was committed, so reissuing the debit (with
/// the same idempotency_key, the ACL's machinery in DEF-1) cannot double-debit (§P5/§332).
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

        // The ConfirmDebit (/v1/debits) and the clearance (/v1/debits/clearance) stubs are configured
        // PER-TEST in each Fact: the resume branch keeps a single always-202 ConfirmDebit, while the
        // reissue branch needs a STATEFUL ConfirmDebit (202 INDETERMINATE on the first send, 201 SUCCESS
        // on the RETRY_PERMITTED reissue) — so they cannot share one stub set up here.
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
        // the engine event completes it. On this branch the debit is never reissued, so a single
        // always-202 ConfirmDebit stub is enough.
        _acl.Given(Request.Create().WithPath("/v1/debits").UsingPost())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.Accepted)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"status":"INDETERMINATE"}"""));

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
    public async Task Indeterminate_debit_REISSUES_the_debit_and_completes_when_clearance_finds_it_not_executed()
    {
        // Scenario C, the RETRY_PERMITTED reissue branch (ADR-IC-012 §D5 step 5 / §P5; conforming).
        // The FIRST ConfirmDebit returns 202 INDETERMINATE → AWAIT_CORE_CLEARANCE, QueryCoreDebitStatus
        // emitted. The clearance query finds the debit did NOT execute (422 → DebitNotExecuted) → the
        // saga REISSUES the debit (back to APPROVED with a fresh ConfirmDebit). The reissue is SAFE: the
        // not-executed clearance is Core ground truth that nothing was committed, so reissuing cannot
        // double-debit (§P5/§332). The reissued ConfirmDebit now succeeds (201) → DebitConfirmed →
        // ActivateDeposit → (engine ProcessConstituted) → COMPLETED.
        //
        // A STATEFUL ConfirmDebit stub makes the reissue observable. The FIRST mapping carries no
        // WhenStateIs, so it matches the scenario's initial (unset) state — the FIRST send answers 202
        // INDETERMINATE and flips the scenario to "Reissued". The SECOND mapping matches WhenStateIs
        // "Reissued" — the RETRY_PERMITTED reissue answers 201 Created → the dispatcher classifies it
        // Applied → the bridge synthesizes the (timely) DebitConfirmed that walks the saga home.
        const string scenario = "confirm-debit-reissue";
        _acl.Given(Request.Create().WithPath("/v1/debits").UsingPost())
            .InScenario(scenario)
            .WillSetStateTo("Reissued")
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.Accepted)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"status":"INDETERMINATE"}"""));
        _acl.Given(Request.Create().WithPath("/v1/debits").UsingPost())
            .InScenario(scenario)
            .WhenStateIs("Reissued")
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.Created)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"status":"EXECUTED","core_txn_id":"CT-REISSUED"}"""));

        // The clearance query finds the (first) debit did NOT execute (422 = not-executed) → RETRY_PERMITTED.
        _acl.Given(Request.Create().WithPath("/v1/debits/clearance").UsingPost())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.UnprocessableEntity)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"status":"NOT_EXECUTED"}"""));

        var processId = await StartSagaAsync(amountCents: 10_000_00);

        // The engine stub accepts the activation that follows the successful reissue.
        await using var engine = new RecordingHttpServer(_ => (HttpStatusCode.Created, "{}"));

        using var host = BuildHost(engineBaseUrl: engine.BaseUrl, settlementBaseUrl: _acl.Url!);
        await host.StartAsync();
        try
        {
            // The saga must first PARK in AWAIT_CORE_CLEARANCE (the indeterminate first debit) and arm
            // the clearance query — never a blind retry.
            await WaitUntilAsync(
                async () => (await OutboxStatusesAsync(processId)).ContainsKey(ConstitutionProcess.QueryCoreDebitStatus),
                TimeSpan.FromSeconds(60),
                "the saga did not arm the clearance query on the indeterminate debit");

            // The not-executed clearance reissues the debit (RETRY_PERMITTED): a SECOND ConfirmDebit row
            // is emitted, the reissue succeeds (201), and the saga resumes to APPROVED with the
            // (post-reissue) ActivateDeposit delivered. It then parks at APPROVED until the engine's
            // ProcessConstituted event (slot 2), which we inject to stand in for the consume loop.
            await WaitUntilAsync(
                async () => await ConfirmDebitRowCountAsync(processId) >= 2
                    && await StateAsync(processId) == SagaState.Approved
                    && (await OutboxStatusesAsync(processId)).TryGetValue(
                        ConstitutionProcess.ActivateDeposit, out var s) && s == "PUBLISHED",
                TimeSpan.FromSeconds(60),
                "the saga did not reissue the debit and resume to APPROVED with ActivateDeposit delivered");

            await InjectEventAsync(processId, ConstitutionProcess.ProcessConstituted);
        }
        finally
        {
            await host.StopAsync();
        }

        // The reissue resolved and the saga completed — NOT a no-money-moved terminal failure.
        Assert.Equal(SagaState.Completed, await StateAsync(processId));

        // EXACTLY TWO ConfirmDebit rows: the original indeterminate one, and the RETRY_PERMITTED reissue.
        Assert.Equal(2, await ConfirmDebitRowCountAsync(processId));

        var rows = await OutboxStatusesAsync(processId);
        // The clearance query was a 422 → terminal FAILED (the dispatcher's slot-5 classification),
        // which the bridge mapped to DebitNotExecuted (REVIEW-FLAG C: a v1 stub convention) → the reissue.
        Assert.Equal("FAILED", rows[ConstitutionProcess.QueryCoreDebitStatus]);
        // The (latest) ConfirmDebit row — the successful reissue — is delivered.
        Assert.Equal("PUBLISHED", rows[ConstitutionProcess.ConfirmDebit]);
        Assert.Equal("PUBLISHED", rows[ConstitutionProcess.ActivateDeposit]);
        // NO reversal: the reissue path never reverses a debit (there was no committed debit to reverse,
        // and the reissue is a forward retry, not a compensation).
        Assert.DoesNotContain(ConstitutionProcess.ReverseCoreDebit, rows.Keys);
        // The saga never failed closed — DEPOSIT_CONSTITUTION_FAILED is not its terminal.
        Assert.NotEqual(SagaState.DepositConstitutionFailed, await StateAsync(processId));

        // The clearance query genuinely reached the ACL, and the ACL saw TWO debit sends (the original
        // + the reissue) — the reissue actually went back over the wire.
        Assert.Contains(AclRequests(), r => r.Path == "/v1/debits/clearance");
        Assert.Equal(2, AclRequests().Count(r => r.Path == "/v1/debits"));
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

    private async Task<int> ConfirmDebitRowCountAsync(Guid processId)
    {
        // The RETRY_PERMITTED reissue emits a SECOND ConfirmDebit saga_outbox row, so the dictionary
        // keyed by command_type (which collapses duplicates) cannot prove the reissue — count the rows.
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM saga_outbox WHERE process_id = @p AND command_type = @c;", connection);
        command.Parameters.AddWithValue("p", processId);
        command.Parameters.AddWithValue("c", ConstitutionProcess.ConfirmDebit);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
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
