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
/// The end-to-end proof that the command-outcome → result-event bridge (bd babelstone-t7o3.8) makes the
/// constitution saga WALK to a terminal state and auto-compensate. The saga's state machine, consume
/// loop, and command dispatcher were all wired, but nothing produced the result events the saga consumes
/// — so the saga stalled at PARALLEL_VALIDATION and the post-debit compensation never fired. This bridge
/// synthesizes each result event from the command's own delivery outcome (the v1 Core ACL is a WireMock
/// shim with no event producer; DEF-1 / babelstone-ub9s replaces it) and self-advances the saga
/// IN-PROCESS in the SAME transaction as the saga_outbox status flip — nothing rides the durable bus
/// (the SAME pattern as the t7o3.1 approval-fork self-emit).
/// </summary>
/// <remarks>
/// <para>
/// These assert on saga STATE progression (the point of t7o3.8), driven entirely by the real dispatcher
/// loop + the real SagaAdvanceHandler + the real saga stores against a migrated PostgreSQL, with the
/// settlement target a WireMock-stubbed Core ACL and the engine command surface a minimal in-process
/// recording stub. The saga is started through the REAL EdgeSagaStarter (the I.1 front door), so the
/// whole path — start → parallel-validation join → approval fork → irreversible debit → activation →
/// (success | compensation) — runs as it would in production.
/// </para>
/// <para>
/// <b>Extraction-ready (ADR-PC-019 §P2).</b> WireMock.Net and the recording stub are TEST-only; no
/// engine-kernel reference. A DEDICATED Postgres container isolates this class's rows so the dispatcher
/// drains only what this class seeds.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SagaResultEventBridgeIntegrationTests : IAsyncLifetime
{
    private const long ThresholdCents = 25_000_00;

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private WireMockServer _acl = null!;

    private string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await new Migrations.MigrationRunner(ConnectionString).ApplyAsync();

        // The v1 Core-ACL settlement stub. The four settlement routes the production SagaCommandRouter
        // maps the settlement commands to — all accepting (the happy + compensation legs are 2xx).
        _acl = WireMockServer.Start();
        _acl.Given(Request.Create().WithPath("/v1/reservations").UsingPost())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.Created).WithBody("""{"reservation":"held"}"""));
        _acl.Given(Request.Create().WithPath("/v1/debits").UsingPost())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.OK).WithBody("""{"debit":"confirmed"}"""));
        _acl.Given(Request.Create().WithPath("/v1/reservations/release").UsingPost())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.OK).WithBody("{}"));
        _acl.Given(Request.Create().WithPath("/v1/debits/reverse").UsingPost())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.OK).WithBody("{}"));
    }

    public async Task DisposeAsync()
    {
        _acl.Stop();
        await _pg.DisposeAsync();
    }

    [Fact]
    public async Task Happy_path_drives_the_saga_to_COMPLETED_with_every_leg_published()
    {
        // The saga starts at PARALLEL_VALIDATION (ReserveAccountBalance + ValidateProductLimits pending).
        // The dispatcher drains both: ValidateProductLimits auto-passes → LimitsValidated [REVIEW-FLAG A];
        // ReserveAccountBalance hits the ACL (2xx) → BalanceReserved. The join completes → the approval
        // fork self-emits ConstitutionApproved → APPROVED emits ConfirmDebit → DebitConfirmed → APPROVED
        // emits ActivateDeposit → the engine stub 201 → ProcessConstituted → COMPLETED. Every step is the
        // bridge synthesizing the result event off the command outcome and self-advancing the saga.
        var processId = await StartSagaAsync(amountCents: 10_000_00);

        // The engine stub accepts the activation (201 Created) — the happy path.
        await using var engine = new RecordingHttpServer(_ => (HttpStatusCode.Created, "{}"));

        using var host = BuildHost(engineBaseUrl: engine.BaseUrl, settlementBaseUrl: _acl.Url!);
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () => await StateAsync(processId) == SagaState.Completed,
                TimeSpan.FromSeconds(60),
                "the saga did not reach COMPLETED on the happy path");
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.Equal(SagaState.Completed, await StateAsync(processId));

        // The whole command set was driven and every routed leg PUBLISHED (ValidateProductLimits is the
        // no-route auto-pass — also PUBLISHED via the synthetic-Applied carve-out).
        var rows = await OutboxStatusesAsync(processId);
        Assert.Equal("PUBLISHED", rows[ConstitutionProcess.ReserveAccountBalance]);
        Assert.Equal("PUBLISHED", rows[ConstitutionProcess.ValidateProductLimits]);
        Assert.Equal("PUBLISHED", rows[ConstitutionProcess.ConfirmDebit]);
        Assert.Equal("PUBLISHED", rows[ConstitutionProcess.ActivateDeposit]);

        // The activation reached the engine stub on its Pact-pinned route.
        Assert.Contains(engine.Requests, r => r.Path == "/v1/deposits");
    }

    [Fact]
    public async Task Post_debit_activation_refusal_auto_reverses_the_debit_to_CANCELLED_AFTER_DEBIT()
    {
        // THE HEADLINE. Reserve + ConfirmDebit succeed (the irreversible debit lands), then the engine
        // REFUSES the activation (4xx on /v1/deposits) → the bridge synthesizes ActivationFailed →
        // APPROVED → COMPENSATE_POST_DEBIT emitting ReverseCoreDebit → the dispatcher delivers it to the
        // ACL (2xx) → DebitReversed → CANCELLED_AFTER_DEBIT. The customer's already-debited money
        // auto-reverses — the compensation the saga existed to guarantee.
        var processId = await StartSagaAsync(amountCents: 10_000_00);

        // The engine stub REFUSES the activation (422) after the debit confirmed.
        await using var engine = new RecordingHttpServer(_ =>
            (HttpStatusCode.UnprocessableEntity, """{"title":"activation failed"}"""));

        using var host = BuildHost(engineBaseUrl: engine.BaseUrl, settlementBaseUrl: _acl.Url!);
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () => await StateAsync(processId) == SagaState.CancelledAfterDebit,
                TimeSpan.FromSeconds(60),
                "the saga did not auto-reverse the debit to CANCELLED_AFTER_DEBIT");
        }
        finally
        {
            await host.StopAsync();
        }

        // The saga compensated: the debit was reversed and the saga rests in the distinct terminal that
        // says money DID move and was returned.
        Assert.Equal(SagaState.CancelledAfterDebit, await StateAsync(processId));

        var rows = await OutboxStatusesAsync(processId);
        // ConfirmDebit succeeded (the irreversible debit landed); ActivateDeposit was REFUSED (FAILED);
        // the compensating ReverseCoreDebit was emitted and delivered (PUBLISHED).
        Assert.Equal("PUBLISHED", rows[ConstitutionProcess.ConfirmDebit]);
        Assert.Equal("FAILED", rows[ConstitutionProcess.ActivateDeposit]);
        Assert.Equal("PUBLISHED", rows[ConstitutionProcess.ReverseCoreDebit]);

        // The reversal genuinely reached the Core ACL's reverse route.
        Assert.Contains(AclRequests(), r => r.Path == "/v1/debits/reverse");
    }

    [Fact]
    public async Task Early_validation_rejection_releases_the_hold_to_CANCELLED()
    {
        // EARLY COMPENSATION (Scenario A). An injected LimitsRejected (the H.2 product-limits verdict
        // the v1 bridge does not itself synthesize — the bridge only auto-PASSES product limits today)
        // drives PARALLEL_VALIDATION → COMPENSATE_VALIDATIONS, emitting ReleaseBalanceReservation. The
        // dispatcher then delivers that compensation leg to the ACL (2xx) → the bridge synthesizes
        // ReservationReleased → CANCELLED. The reversible hold is released; no money moved irreversibly.
        var processId = await StartSagaAsync(amountCents: 10_000_00);

        // Inject LimitsRejected BEFORE the dispatcher runs, so the saga is deterministically in
        // COMPENSATE_VALIDATIONS (with ReleaseBalanceReservation queued) before any leg drains — avoiding
        // a race with the ValidateProductLimits auto-pass that would otherwise complete the success join.
        // Once in COMPENSATE_VALIDATIONS the still-pending Reserve/ValidateProductLimits legs synthesize
        // BalanceReserved/LimitsValidated, which have NO transition there → graceful no-ops.
        await InjectEventAsync(processId, ConstitutionProcess.LimitsRejected);
        Assert.Equal(SagaState.CompensateValidations, await StateAsync(processId));

        // No engine activation is reached on this path; an unreachable engine URL proves it.
        using var host = BuildHost(engineBaseUrl: "http://engine.invalid", settlementBaseUrl: _acl.Url!);
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () => await StateAsync(processId) == SagaState.Cancelled,
                TimeSpan.FromSeconds(60),
                "the saga did not release the hold and reach CANCELLED");
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.Equal(SagaState.Cancelled, await StateAsync(processId));

        var rows = await OutboxStatusesAsync(processId);
        Assert.Equal("PUBLISHED", rows[ConstitutionProcess.ReleaseBalanceReservation]);
        Assert.Contains(AclRequests(), r => r.Path == "/v1/reservations/release");
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

    // Start the saga through the REAL edge starter: STARTED → PARALLEL_VALIDATION, emitting the two
    // parallel commands (ReserveAccountBalance + ValidateProductLimits) to saga_outbox, references pinned.
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

    // Drive one event through the REAL advance handler in its own transaction (the early-compensation
    // test injects the H.2 LimitsRejected verdict the v1 bridge does not synthesize).
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
            // Last status per command_type wins (a command_type appears once per saga here).
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
