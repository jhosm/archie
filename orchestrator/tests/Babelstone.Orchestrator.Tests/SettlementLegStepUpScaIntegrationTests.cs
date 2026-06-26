using System.Net;
using Babelstone.Orchestrator.Dispatch;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Saga;
using Babelstone.Orchestrator.Saga.Settlement;
using Babelstone.TestFixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// SETTLEMENT_LEG_SCA_GATE_CANNOT_BYPASS (bd babelstone-t7o3.19; ADR-PC-032 §A7/§A8; ADR-IC-010 §A11–A12;
/// ADR-IC-006 §P2). In plain English: when a saga matures a deposit or pays a coupon — money the bank can
/// never claw back — it must confirm the customer recently passed strong authentication (SCA) right before the
/// cash actually moves. This proves the gate end-to-end against an in-process Core ACL stub (the lane's
/// sanctioned <c>RecordingHttpServer</c>, the same stand-in the saga SCA + dispatcher tests use) that
/// REPRODUCES the receiver's fail-closed SCA check: a saga-driven maturity money-mover with no / stale SCA is REFUSED at the settlement
/// leg (422 SCA_REQUIRED) before any cash moves and the leg is terminal FAILED; with FRESH attested SCA the
/// dispatcher forwarded the claims and the leg settled.
/// </summary>
/// <remarks>
/// <para>
/// <b>Attest-not-deny (ADR-IC-006 §P2; ADR-PC-019 §P2 / ADR-IC-018 §D2).</b> The substrate ATTESTS — it threads
/// the attested <c>acr</c> / <c>auth_time</c> from the auto-starting Movement-bearing event's CloudEvents
/// headers (the populate hop, bd babelstone-t7o3.20) onto the cash leg's <c>saga_outbox</c> row, and the
/// dispatcher re-emits them as <c>X-SCA-Acr</c> / <c>X-SCA-Auth-Time</c> on the cash-leg delivery. The RECEIVER
/// (the Core ACL settlement leg) is the DENY point, never the substrate. The ACL stub here reproduces that
/// receiver gate (the SAME fail-closed verdict the engine's <c>ScaPrecondition.Check</c> gives — 422 when the
/// proof is absent, non-numeric, in the future, or older than the window; it is reproduced, NOT referenced, so
/// the orchestrator subtree stays extraction-ready, ADR-PC-019 §P2).
/// </para>
/// <para>
/// <b>Re-checked at the settlement-dispatch instant (ADR-PC-032 §A7).</b> The receiver compares
/// <c>now − auth_time</c> against <c>SCA_MAX_AGE</c> (300 s) at the moment the cash leg is delivered — not
/// inherited from saga entry. A proof fresh at saga start but stale when the cash leg fires is refused. A
/// dedicated Postgres container isolates this class's rows so the dispatcher only ever sees what it seeded.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SettlementLegStepUpScaIntegrationTests : IAsyncLifetime
{
    // The receiver's SCA freshness window mirror (the SAME 300 s the engine-direct gate + Kong route gate use,
    // ADR-IC-006 §P2). The orchestrator subtree cannot reference an engine-side constant (extraction-ready), so
    // the test pins the shared value.
    private const long ScaMaxAgeSeconds = 300;

    // The ce_-stripped, lowercased projection of the attested claims as they ride a Movement-bearing event's
    // CloudEvents headers (the populate hop, bd babelstone-t7o3.20 / ADR-PC-032 §A8). The advance handler reads
    // these off message.ExtensionHeaders.
    private const string ScaAcrHeaderKey = "scaacr";
    private const string ScaAuthTimeHeaderKey = "scaauthtime";

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly SagaStateStore _stateStore = new();
    private readonly SagaTransitionLog _transitionLog = new();

    private string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.GatedStartAsync();
        await new Migrations.MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    [Fact]
    public async Task A_saga_maturity_with_NO_SCA_is_refused_at_the_settlement_leg_before_cash_moves()
    {
        // A maturity is an Originated CREDIT; auto-start the settlement saga off the Movement-bearing event but
        // with NO SCA claims on its headers (the absent-proof case). The leg emits ConfirmCredit carrying no
        // SCA; the receiver fail-closes 422 SCA_REQUIRED before the credit lands.
        var processId = Guid.NewGuid();
        await AutoStartMaturityAsync(processId, scaAcr: null, scaAuthTime: null);
        var creditId = await OutboxMessageIdAsync(processId, SettlementProcess.ConfirmCredit);

        await using var acl = new RecordingHttpServer(ScaGatedResponder);
        await DrainUntilTerminalAsync(acl, creditId);

        // Refused at the leg → terminal FAILED with the receiver's 422, the saga's compensation/escalation
        // trigger (ADR-PC-029 slot 5). The credit did NOT confirm.
        Assert.Equal("FAILED", await StatusAsync(creditId));
        Assert.Equal(422, await FailureStatusCodeAsync(creditId));

        // The cash leg reached the receiver's credit route carrying NEITHER SCA header — the gate saw an absent
        // proof, exactly as the engine-direct path does.
        var hit = Assert.Single(acl.Requests, r => r.Path == "/v1/credits");
        Assert.Null(hit.ScaAcr);
        Assert.Null(hit.ScaAuthTime);
    }

    [Fact]
    public async Task A_saga_maturity_with_a_STALE_SCA_is_refused_at_the_settlement_leg()
    {
        // SCA happened, but too long ago — auth_time beyond the window at the DISPATCH instant. The claims ARE
        // forwarded (the substrate attests); the RECEIVER re-checks freshness at dispatch and refuses.
        var processId = Guid.NewGuid();
        var stale = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (ScaMaxAgeSeconds + 60);
        await AutoStartMaturityAsync(processId, scaAcr: "urn:bank:sca:psd2", scaAuthTime: stale);
        var creditId = await OutboxMessageIdAsync(processId, SettlementProcess.ConfirmCredit);

        await using var acl = new RecordingHttpServer(ScaGatedResponder);
        await DrainUntilTerminalAsync(acl, creditId);

        Assert.Equal("FAILED", await StatusAsync(creditId));
        Assert.Equal(422, await FailureStatusCodeAsync(creditId));

        // The claims WERE forwarded (the gate saw them and judged auth_time stale) — proving the substrate
        // attests and the RECEIVER is the freshness authority at the dispatch instant (ADR-PC-032 §A7).
        var hit = Assert.Single(acl.Requests, r => r.Path == "/v1/credits");
        Assert.Equal("urn:bank:sca:psd2", hit.ScaAcr);
        Assert.Equal(stale.ToString(System.Globalization.CultureInfo.InvariantCulture), hit.ScaAuthTime);
    }

    [Fact]
    public async Task A_saga_maturity_with_FRESH_attested_SCA_forwards_the_claims_and_settles()
    {
        // Fresh gateway-attested SCA on the auto-starting event's headers: the substrate threads them onto the
        // cash leg's outbox row, the dispatcher re-emits them, and the receiver lets the credit through.
        var processId = Guid.NewGuid();
        var fresh = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await AutoStartMaturityAsync(processId, scaAcr: "urn:bank:sca:psd2", scaAuthTime: fresh);
        var creditId = await OutboxMessageIdAsync(processId, SettlementProcess.ConfirmCredit);

        await using var acl = new RecordingHttpServer(ScaGatedResponder);
        await DrainUntilTerminalAsync(acl, creditId);

        // Fresh attested SCA → the receiver let it through; the leg settled PUBLISHED.
        Assert.Equal("PUBLISHED", await StatusAsync(creditId));

        // The dispatcher forwarded BOTH gateway-attested claims to the receiver — the SAME X-SCA-Acr /
        // X-SCA-Auth-Time the engine-direct money-movers enforce (ADR-IC-010 §A11–A12).
        var hit = Assert.Single(acl.Requests, r => r.Path == "/v1/credits");
        Assert.Equal("urn:bank:sca:psd2", hit.ScaAcr);
        Assert.Equal(fresh.ToString(System.Globalization.CultureInfo.InvariantCulture), hit.ScaAuthTime);
    }

    // ---- The Core ACL's RECEIVER-side SCA gate (reproduces the deny point; NOT a reference, ADR-PC-019 §P2) -

    /// <summary>
    /// The Core ACL settlement leg's fail-closed SCA verdict, re-checked at the DISPATCH instant (ADR-PC-032
    /// §A7): <c>422 SCA_REQUIRED</c> when <c>X-SCA-Acr</c> is absent/empty, <c>X-SCA-Auth-Time</c> is
    /// absent/non-numeric, or <c>auth_time</c> is in the future or older than the freshness window; otherwise
    /// the credit confirms (200). This is the RECEIVER deny point — the substrate only attested the claims.
    /// Reproduced here (not referenced) so the orchestrator subtree stays extraction-ready. A non-money-mover
    /// path (e.g. /v1/reservations) is not SCA-gated; the credit leg is.
    /// </summary>
    private static (HttpStatusCode Status, string Body) ScaGatedResponder(RecordingHttpServer.RecordedRequest request)
    {
        if (request.Path != "/v1/credits")
        {
            // Any other settlement route is not SCA-gated by THIS test (the reserve/debit legs settle freely).
            return (HttpStatusCode.OK, """{"ok":true}""");
        }

        if (string.IsNullOrWhiteSpace(request.ScaAcr)
            || !long.TryParse(request.ScaAuthTime, System.Globalization.CultureInfo.InvariantCulture, out var authTime))
        {
            return (HttpStatusCode.UnprocessableEntity, """{"code":"SCA_REQUIRED","title":"SCA required"}""");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (authTime > now || (now - authTime) > ScaMaxAgeSeconds)
        {
            return (HttpStatusCode.UnprocessableEntity, """{"code":"SCA_REQUIRED","title":"SCA required"}""");
        }

        return (HttpStatusCode.OK, """{"credit":"confirmed"}""");
    }

    // ---- Host wiring (the SETTLEMENT saga's bridge + router; the cash leg goes to the ACL stub) -------

    private SagaAdvanceHandler NewHandler(ISagaCommandSink sink, string settlementBaseUrl)
    {
        var context = new SagaModuleContext(
            RuntimeConnectionString: ConnectionString,
            EngineBaseUrl: "http://engine.invalid",
            SettlementBaseUrl: settlementBaseUrl);
        var module = new SettlementSagaModule(context, consumeTopics: ["term_deposit"]);
        return new SagaAdvanceHandler(
            new ISagaStateMachine[] { module.StateMachine },
            _stateStore, _transitionLog, sink,
            new ISagaModule[] { module });
    }

    /// <summary>Auto-start the settlement saga off a maturity Movement-bearing event (an Originated CREDIT)
    /// carrying the attested SCA on its CloudEvents headers (the populate hop). The auto-start emits
    /// ConfirmCredit, whose outbox row the substrate stamps with the SCA claims read off the event headers.</summary>
    private async Task AutoStartMaturityAsync(Guid processId, string? scaAcr, long? scaAuthTime)
    {
        var sink = new SettlementCommandOutboxSink(new SagaOutboxWriter());
        // The settlement base URL is irrelevant for the SEED (no HTTP runs here — the advance only writes the
        // outbox row); the drain below points at the live ACL stub.
        var handler = NewHandler(sink, "http://settlement.invalid");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SettlementSagaModule.OriginHeader] = "Originated",
            [SettlementProcess.DirectionHeader] = "Credit",
        };
        if (scaAcr is not null)
        {
            headers[ScaAcrHeaderKey] = scaAcr;
        }

        if (scaAuthTime is { } authTime)
        {
            headers[ScaAuthTimeHeaderKey] = authTime.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var outcome = await handler.AdvanceAsync(connection, tx, new SagaInboxEvent(
            MessageId: Guid.NewGuid(), ProcessId: processId, EventType: "DepositMatured",
            SourceTopic: "term_deposit", CorrelationId: null,
            TraceParent: "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            ExtensionHeaders: headers));
        await tx.CommitAsync();

        Assert.Equal(AdvanceOutcome.Advanced, outcome);
    }

    private IHost BuildHost(string settlementBaseUrl)
    {
        var options = new SagaCommandDispatcherOptions
        {
            ConnectionString = ConnectionString,
            EngineBaseUrl = "http://engine.invalid",
            SettlementBaseUrl = settlementBaseUrl,
            PollInterval = TimeSpan.FromMilliseconds(100),
        };
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ISagaCommandRouter>(new SettlementCommandRouter(options));
        builder.Services.AddSingleton<ICommandRouter>(sp =>
            new CompositeCommandRouter(sp.GetServices<ISagaCommandRouter>()));
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<ISagaStateMachine, SettlementProcess>();
        builder.Services.AddSingleton<SagaStateStore>();
        builder.Services.AddSingleton<SagaTransitionLog>();
        builder.Services.AddSingleton<SagaOutboxWriter>();
        builder.Services.AddSingleton<ISagaTypedCommandSink>(sp =>
            new SettlementCommandOutboxSink(sp.GetRequiredService<SagaOutboxWriter>()));
        builder.Services.AddSingleton<ISagaCommandSink>(sp =>
            new CompositeSagaCommandSink(sp.GetServices<ISagaTypedCommandSink>()));
        builder.Services.AddSingleton(sp => new SagaAdvanceHandler(
            sp.GetServices<ISagaStateMachine>(),
            sp.GetRequiredService<SagaStateStore>(),
            sp.GetRequiredService<SagaTransitionLog>(),
            sp.GetRequiredService<ISagaCommandSink>()));
        builder.Services.AddSingleton<IResultEventBridge, SettlementResultEvents.Bridge>();
        builder.Services.AddSingleton(sp => new SagaCommandDispatchDrainer(
            sp.GetRequiredService<SagaCommandDispatcherOptions>(),
            sp.GetRequiredService<ICommandRouter>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<SagaAdvanceHandler>(),
            sp.GetServices<IResultEventBridge>()));
        builder.Services.AddHostedService<SagaCommandDispatcherService>();
        return builder.Build();
    }

    private async Task DrainUntilTerminalAsync(RecordingHttpServer acl, Guid messageId)
    {
        using var host = BuildHost(acl.BaseUrl);
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () =>
                {
                    var status = await StatusAsync(messageId);
                    return status is "PUBLISHED" or "FAILED";
                },
                TimeSpan.FromSeconds(30),
                "the dispatcher did not reach a terminal status for the settlement cash leg");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private async Task<Guid> OutboxMessageIdAsync(Guid processId, string commandType)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT message_id FROM saga_outbox WHERE process_id = @p AND command_type = @t ORDER BY seq DESC LIMIT 1;",
            connection);
        command.Parameters.AddWithValue("p", processId);
        command.Parameters.AddWithValue("t", commandType);
        return (Guid)(await command.ExecuteScalarAsync())!;
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
