using System.Net;
using Babelstone.Orchestrator.Dispatch;
using Babelstone.Families.TermDeposit.Orchestration;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Saga;
using Babelstone.TestFixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// The end-to-end proof that step-up SCA threads through the orchestrator SAGA money-mover path to the
/// engine's <c>422 SCA_REQUIRED</c> gate (bd babelstone-ls44; ADR-IC-010 §P8 A10, ADR-IC-006 §P2 A2).
/// </summary>
/// <remarks>
/// <para>
/// In plain English: maturing a deposit or paying a coupon must not settle for an AI agent unless the
/// bank just saw a fresh strong-authentication (SCA) proof. The engine-DIRECT money-movers already
/// enforce that (PR #274 / bd ziu3.5): the engine returns <c>422 SCA_REQUIRED</c> when the
/// gateway-attested <c>X-SCA-Acr</c> / <c>X-SCA-Auth-Time</c> are absent or stale. ADR-IC-010 §P8 A10
/// scoped that PR to the engine-direct path and named the saga-routed money-mover path as "its own
/// lane" — this test is that lane's proof. It drives a maturity money-mover THROUGH the saga dispatcher
/// (the saga decides the command, writes it to <c>saga_outbox</c> carrying the gateway-attested SCA
/// claims, and the dispatcher delivers it over HTTP) and asserts the SAME engine gate fires: absent or
/// stale SCA → the engine 422s and the row is terminal FAILED; fresh attested SCA → the dispatcher
/// forwarded the claims and the row is PUBLISHED.
/// </para>
/// <para>
/// <b>The stub engine reproduces the engine's <c>ScaPrecondition</c> gate (not a reference to it).</b>
/// The orchestrator subtree stays extraction-ready (ADR-PC-019 §P2 — no engine-kernel reference, even
/// in tests), so the stub engine here re-implements the same fail-closed verdict the engine's
/// <c>ScaPrecondition.Check</c> gives — 422 when <c>X-SCA-Acr</c> is absent/empty, when
/// <c>X-SCA-Auth-Time</c> is absent/non-numeric, or when <c>auth_time</c> is in the future or older than
/// the freshness window; 200 otherwise. The REAL engine gate is proven against the real engine in
/// <c>engine/tests/Babelstone.Engine.Api.Tests/DepositsApiIntegrationTests.cs</c>; what THIS lane adds,
/// and what this test pins, is that the saga forwards the SAME gateway-attested claims to that gate.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SagaStepUpScaIntegrationTests : IAsyncLifetime
{
    // The engine's SCA freshness window mirror (ScaPrecondition.MaxAgeSeconds = 300). The orchestrator
    // cannot reference that engine-side constant (extraction-readiness), so the test pins the same value
    // the engine and the Kong REST-route SCA gate use.
    private const long ScaMaxAgeSeconds = 300;

    // The engine's gateway-attested SCA headers (ScaPrecondition.AcrHeader / AuthTimeHeader). The
    // dispatcher forwards these for a money-mover; the engine gate reads them.
    private const string AcrHeader = "X-SCA-Acr";
    private const string AuthTimeHeader = "X-SCA-Auth-Time";

    // A maturity money-mover command name + its engine route. A real SCA-gated money-mover the saga lane
    // routes to the engine's route-group-gated endpoint POST /v1/deposits/{id}/maturity (the {process_id}
    // path token the dispatcher already substitutes for the renewal legs, bd babelstone-mtto PR2).
    private const string MatureDeposit = "MatureDeposit";

    // A DEDICATED Postgres container (not the shared collection fixture): the dispatcher's hosted drain
    // loop drains EVERY PENDING saga_outbox row, so an isolated DB means it only ever sees the rows this
    // class seeded — the only way to assert exact delivery counts/routes (mirrors
    // SagaCommandDispatcherIntegrationTests' rationale).
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.GatedStartAsync();
        await new Migrations.MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    [Fact]
    public async Task A_saga_maturity_run_with_NO_SCA_proof_is_422_SCA_REQUIRED_and_is_terminal_FAILED()
    {
        var processId = Guid.NewGuid();
        await StartSagaAsync(processId);
        // The saga decided a maturity money-mover but no fresh SCA was attested → the row carries NULL
        // SCA claims (the absent-proof case). The dispatcher sends neither SCA header.
        var messageId = await SeedMoneyMoverAsync(processId, scaAcr: null, scaAuthTime: null);

        await using var engine = new RecordingHttpServer(ScaGateResponder);
        using var host = BuildHost(engine.BaseUrl);
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () => await StatusAsync(messageId) == "FAILED",
                TimeSpan.FromSeconds(30),
                "the dispatcher did not mark the SCA-less maturity money-mover FAILED");
        }
        finally
        {
            await host.StopAsync();
        }

        // The engine 422'd (no SCA proof) → terminal FAILED with the engine's 422 recorded, the saga's
        // compensation/escalation trigger (ADR-PC-029 slot 5). The money-mover did NOT settle.
        Assert.Equal("FAILED", await StatusAsync(messageId));
        Assert.Equal(422, await FailureStatusCodeAsync(messageId));

        // The request reached the engine's maturity money-mover route carrying NEITHER SCA header — the
        // gate saw an absent proof, exactly as the engine-direct path does.
        var request = Assert.Single(engine.Requests, r => r.IdempotencyKey == messageId.ToString());
        Assert.Equal($"/v1/deposits/{processId}/maturity", request.Path);
        Assert.Null(request.ScaAcr);
        Assert.Null(request.ScaAuthTime);
    }

    [Fact]
    public async Task A_saga_maturity_run_with_a_STALE_SCA_auth_time_is_422_SCA_REQUIRED()
    {
        var processId = Guid.NewGuid();
        await StartSagaAsync(processId);
        // SCA happened, but too long ago — auth_time beyond the freshness window. The claims ARE
        // forwarded; the engine re-checks freshness at dispatch time and refuses (fail-closed).
        var stale = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (ScaMaxAgeSeconds + 60);
        var messageId = await SeedMoneyMoverAsync(processId, scaAcr: "urn:bank:sca:psd2", scaAuthTime: stale);

        await using var engine = new RecordingHttpServer(ScaGateResponder);
        using var host = BuildHost(engine.BaseUrl);
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () => await StatusAsync(messageId) == "FAILED",
                TimeSpan.FromSeconds(30),
                "the dispatcher did not mark the stale-SCA maturity money-mover FAILED");
        }
        finally
        {
            await host.StopAsync();
        }

        // Stale proof → the engine gate 422s exactly as the engine-direct path does; terminal FAILED.
        Assert.Equal("FAILED", await StatusAsync(messageId));
        Assert.Equal(422, await FailureStatusCodeAsync(messageId));

        // The claims WERE forwarded (the gate saw them and judged the auth_time stale) — proving the saga
        // threads the attestation and the ENGINE is the freshness authority, not this row.
        var request = Assert.Single(engine.Requests, r => r.IdempotencyKey == messageId.ToString());
        Assert.Equal("urn:bank:sca:psd2", request.ScaAcr);
        Assert.Equal(stale.ToString(System.Globalization.CultureInfo.InvariantCulture), request.ScaAuthTime);
    }

    [Fact]
    public async Task A_saga_maturity_run_with_FRESH_attested_SCA_forwards_the_claims_and_settles()
    {
        var processId = Guid.NewGuid();
        await StartSagaAsync(processId);
        // Fresh gateway-attested SCA: a non-empty acr + an auth_time inside the freshness window.
        var fresh = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var messageId = await SeedMoneyMoverAsync(processId, scaAcr: "urn:bank:sca:psd2", scaAuthTime: fresh);

        await using var engine = new RecordingHttpServer(ScaGateResponder);
        using var host = BuildHost(engine.BaseUrl);
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () => await StatusAsync(messageId) == "PUBLISHED",
                TimeSpan.FromSeconds(30),
                "the dispatcher did not settle the fresh-SCA maturity money-mover");
        }
        finally
        {
            await host.StopAsync();
        }

        // Fresh attested SCA → the engine gate let it through; the row settled PUBLISHED.
        Assert.Equal("PUBLISHED", await StatusAsync(messageId));

        // The dispatcher forwarded BOTH gateway-attested claims to the engine's maturity route — the SAME
        // X-SCA-Acr / X-SCA-Auth-Time the engine-direct money-movers enforce (ADR-IC-010 §P8 A10).
        var request = Assert.Single(engine.Requests, r => r.IdempotencyKey == messageId.ToString());
        Assert.Equal($"/v1/deposits/{processId}/maturity", request.Path);
        Assert.Equal("urn:bank:sca:psd2", request.ScaAcr);
        Assert.Equal(fresh.ToString(System.Globalization.CultureInfo.InvariantCulture), request.ScaAuthTime);
    }

    // ---- The stub engine's SCA gate (reproduces ScaPrecondition.Check; NOT a reference to it) ---------

    /// <summary>
    /// The stub engine's money-mover gate: the same fail-closed verdict the engine's
    /// <c>ScaPrecondition.Check</c> gives — <c>422</c> when the SCA proof is absent, non-numeric, in the
    /// future, or older than the freshness window; <c>200</c> when fresh. Reproduced here (not referenced)
    /// because the orchestrator subtree stays extraction-ready (ADR-PC-019 §P2). The real gate is proven
    /// against the real engine in the engine API test project; this lane proves the saga forwards the
    /// claims the gate reads.
    /// </summary>
    private static (HttpStatusCode Status, string Body) ScaGateResponder(RecordingHttpServer.RecordedRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ScaAcr)
            || !long.TryParse(request.ScaAuthTime, out var authTime))
        {
            return (HttpStatusCode.UnprocessableEntity, """{"code":"SCA_REQUIRED","title":"SCA required"}""");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (authTime > now || (now - authTime) > ScaMaxAgeSeconds)
        {
            return (HttpStatusCode.UnprocessableEntity, """{"code":"SCA_REQUIRED","title":"SCA required"}""");
        }

        return (HttpStatusCode.OK, """{"lifecycle":"Matured"}""");
    }

    // ---- Host wiring (mirrors the production dispatcher composition) ---------------------------------

    private IHost BuildHost(string engineBaseUrl)
    {
        var options = new SagaCommandDispatcherOptions
        {
            ConnectionString = ConnectionString,
            EngineBaseUrl = engineBaseUrl,
            SettlementBaseUrl = "http://settlement.invalid",
            PollInterval = TimeSpan.FromMilliseconds(100),
        };
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(options);
        // A test-local router mapping the maturity money-mover to the engine's route-group-gated endpoint
        // (the {process_id} token the dispatcher substitutes). The constitution router is unchanged in
        // production — it emits no money-mover today; this proves the substrate dispatch path forwards
        // SCA to a money-mover route the moment a saga routes one (ADR-IC-010 §P8 A10).
        builder.Services.AddSingleton<ICommandRouter>(new MoneyMoverRouter(options));
        builder.Services.AddHttpClient();
        AddSagaAdvanceHandler(builder.Services);
        builder.Services.AddSingleton<IResultEventBridge, ConstitutionResultEvents.Bridge>();
        builder.Services.AddSingleton(sp => new SagaCommandDispatchDrainer(
            sp.GetRequiredService<SagaCommandDispatcherOptions>(),
            sp.GetRequiredService<ICommandRouter>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<SagaAdvanceHandler>(),
            sp.GetServices<IResultEventBridge>()));
        builder.Services.AddHostedService<SagaCommandDispatcherService>();
        return builder.Build();
    }

    private static void AddSagaAdvanceHandler(IServiceCollection services)
    {
        services.AddSingleton<ISagaStateMachine, ConstitutionProcess>();
        services.AddSingleton<SagaStateStore>();
        services.AddSingleton<SagaTransitionLog>();
        services.AddSingleton<SagaBusinessReferenceStore>();
        services.AddSingleton<ISagaCommandSink>(sp =>
            new SagaCommandOutboxSink(sp.GetRequiredService<SagaBusinessReferenceStore>()));
        services.AddSingleton(sp => new SagaAdvanceHandler(
            sp.GetRequiredService<ISagaStateMachine>(),
            sp.GetRequiredService<SagaStateStore>(),
            sp.GetRequiredService<SagaTransitionLog>(),
            sp.GetRequiredService<ISagaCommandSink>()));
    }

    /// <summary>Maps the maturity money-mover to the engine's route-group-gated maturity endpoint,
    /// carrying the {process_id} path token the dispatcher substitutes. A test-local router (the
    /// production constitution router emits no money-mover today) — the point is the SUBSTRATE dispatch
    /// path forwards the SCA claims to whatever money-mover route a saga maps.</summary>
    private sealed class MoneyMoverRouter(SagaCommandDispatcherOptions options) : ICommandRouter
    {
        private readonly string _engineBaseUrl = options.EngineBaseUrl;

        public CommandRoute? Resolve(string commandType) => commandType switch
        {
            MatureDeposit => new CommandRoute(_engineBaseUrl, "/v1/deposits/{process_id}/maturity", HttpMethod.Post),
            _ => null,
        };

        public CommandRoute? Resolve(string commandType, string sagaType) => Resolve(commandType);
    }

    // ---- Seed helpers --------------------------------------------------------------------------------

    private async Task StartSagaAsync(Guid processId)
    {
        var stateStore = new SagaStateStore();
        var businessRefStore = new SagaBusinessReferenceStore();
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await stateStore.TryStartAsync(
            connection, tx, processId, ConstitutionProcess.Type, ConstitutionProcess.States.Started, correlationId: null);
        await businessRefStore.TryInsertAsync(
            connection, tx,
            new SagaBusinessReference(
                ProcessId: processId,
                ProductRef: "TD-TRAD-12M",
                AmountMinorUnits: 100_00,
                SourceAccountRef: "acct-ref-001",
                InterestAccountRef: null,
                DepositRef: "DEP-" + processId.ToString("N"),
                ClientType: ClientType.Existing,
                AutoApprovalThresholdMinorUnits: 1_000_00));
        await tx.CommitAsync();
    }

    /// <summary>Seed a PENDING maturity money-mover row carrying the gateway-attested SCA claims directly
    /// through the substrate's <see cref="SagaOutboxWriter"/> (bd babelstone-ls44 added the SCA columns).
    /// A byte-stable, PII-free placeholder body — the test asserts the FORWARDED HEADERS, not the body
    /// shape (the dispatcher↔engine body contract is pinned by the CDC tests).</summary>
    private async Task<Guid> SeedMoneyMoverAsync(Guid processId, string? scaAcr, long? scaAuthTime)
    {
        var writer = new SagaOutboxWriter();
        var traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var messageId = await writer.AppendAsync(
            connection, tx, processId, MatureDeposit,
            causationMessageId: Guid.NewGuid(), correlationId: null,
            payload: "{}"u8.ToArray(), traceParent: traceParent, ct: default,
            scaAcr: scaAcr, scaAuthTime: scaAuthTime);
        await tx.CommitAsync();
        return messageId;
    }

    private async Task<string> StatusAsync(Guid messageId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM saga_outbox WHERE message_id = @id;", connection);
        command.Parameters.AddWithValue("id", messageId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<int?> FailureStatusCodeAsync(Guid messageId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT failure_status_code FROM saga_outbox WHERE message_id = @id;", connection);
        command.Parameters.AddWithValue("id", messageId);
        var raw = await command.ExecuteScalarAsync();
        return raw is null or DBNull ? null : (int)raw;
    }

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
