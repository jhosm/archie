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
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// MOVEMENT_CASH_LEG_IDEMPOTENT (bd babelstone-t7o3.15, ADR-PC-032 commitment 2 / feature-design
/// money-movement-settlement §10). In plain English: once a family records a money <c>Movement</c>, the
/// substrate-owned settlement saga effects the cash leg — and a retried cash leg must NOT double-move the
/// money. This proves it end-to-end against a WireMock Core ACL: the dispatcher delivers each settlement
/// leg with the outbox row's <c>message_id</c> as the <c>Idempotency-Key</c> (ADR-PC-029 slot 4) and the
/// body's STABLE, process-id-derived external reference (the ACL's <c>(operation_type, …, external_reference)</c>
/// key, ADR-IC-012 §P4); a PUBLISHED leg is never re-sent, so a re-drain cannot double-move; and a
/// reissue (the not-executed clearance path) presents the SAME reference, so even the worst case cannot
/// double-debit. The eager <c>SettleAsync</c> bypass of that guard is gone (ADR-PC-032 §Decision slot 4).
/// </summary>
/// <remarks>
/// <para>
/// <b>The substrate-owned settlement saga, not the constitution one.</b> Where
/// <see cref="GatedSettlementWireMockIntegrationTests"/> proves the constitution debit's relocated legs,
/// this proves the GENERIC <see cref="SettlementProcess"/> cash leg — both directions — against the same
/// WireMock ACL shape the dev stack runs (infra/wiremock). A DEDICATED Postgres container isolates this
/// class's <c>saga_outbox</c> rows so the dispatcher only ever sees the rows seeded here (the only way to
/// assert exact delivery counts).
/// </para>
/// <para>
/// <b>Extraction-ready (ADR-PC-019 §P2).</b> WireMock.Net is a TEST-only dependency; it does not widen the
/// runtime orchestrator's dependency graph.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class MovementCashLegIdempotentWireMockIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private WireMockServer _acl = null!;

    private string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.GatedStartAsync();
        await new Migrations.MigrationRunner(ConnectionString).ApplyAsync();

        // The v1 Core-ACL settlement stub — the SAME endpoint shapes the production SettlementCommandRouter
        // maps the settlement legs to, including the NEW credit surface (/v1/credits).
        _acl = WireMockServer.Start();

        // Debit legs: the reversible hold + the irreversible debit both accept.
        _acl.Given(Request.Create().WithPath("/v1/reservations").UsingPost())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.Created)
                .WithHeader("Content-Type", "application/json").WithBody("""{"reservation":"held"}"""));
        _acl.Given(Request.Create().WithPath("/v1/debits").UsingPost())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.OK)
                .WithHeader("Content-Type", "application/json").WithBody("""{"debit":"confirmed"}"""));

        // Credit leg: the confirmation-gated credit accepts (the new generic credit endpoint).
        _acl.Given(Request.Create().WithPath("/v1/credits").UsingPost())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.OK)
                .WithHeader("Content-Type", "application/json").WithBody("""{"credit":"confirmed"}"""));
    }

    public async Task DisposeAsync()
    {
        _acl.Stop();
        await _pg.DisposeAsync();
    }

    [Fact]
    public async Task A_debit_cash_leg_is_delivered_once_with_a_stable_idempotency_key_and_a_PUBLISHED_leg_is_never_re_sent()
    {
        // The settlement saga decided the debit leg (ConfirmDebit) into saga_outbox. Seed it PENDING; the
        // dispatcher must deliver it ONCE to the WireMock ACL, carrying the row's message_id as the
        // Idempotency-Key. A second drain must NOT re-send a PUBLISHED row (no double-move).
        var processId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        await StartSagaAsync(processId, SettlementProcess.States.ConfirmingDebit, correlationId);
        var confirmId = await SeedCommandAsync(processId, SettlementProcess.ConfirmDebit, correlationId);

        await DrainUntilPublishedAsync(confirmId);

        // Delivered exactly ONCE on the mapped route, carrying its row's message_id as the Idempotency-Key
        // (ADR-PC-029 slot 4) — the key the ACL dedups on.
        var debitHits = AclRequests().Where(r => r.Path == "/v1/debits").ToList();
        Assert.Single(debitHits);
        Assert.Equal(confirmId.ToString(), debitHits[0].IdempotencyKey);

        // A SECOND drain against the now-PUBLISHED row must NOT re-send it — the row status guards re-send,
        // so the cash leg cannot double-move on a redelivery.
        using (var host = BuildHost())
        {
            await host.StartAsync();
            await Task.Delay(500); // let the loop poll a few cycles against the PUBLISHED row.
            await host.StopAsync();
        }

        Assert.Single(AclRequests(), r => r.Path == "/v1/debits");
        Assert.Equal("PUBLISHED", await StatusAsync(confirmId));
    }

    [Fact]
    public async Task A_credit_cash_leg_is_delivered_once_with_a_stable_idempotency_key()
    {
        // The credit path (the NEW confirmation-gated surface): ConfirmCredit delivered once to /v1/credits,
        // keyed by its message_id, then never re-sent once PUBLISHED.
        var processId = Guid.NewGuid();
        await StartSagaAsync(processId, SettlementProcess.States.ConfirmingCredit, correlationId: null);
        var creditId = await SeedCommandAsync(processId, SettlementProcess.ConfirmCredit, correlationId: null);

        await DrainUntilPublishedAsync(creditId);

        var creditHits = AclRequests().Where(r => r.Path == "/v1/credits").ToList();
        Assert.Single(creditHits);
        Assert.Equal(creditId.ToString(), creditHits[0].IdempotencyKey);
        Assert.Equal("PUBLISHED", await StatusAsync(creditId));
    }

    [Fact]
    public async Task A_reissued_debit_presents_the_SAME_stable_external_reference_so_the_ACL_cannot_double_move()
    {
        // The no-double-move guarantee in the worst case (ADR-PC-032 slot 4 / ADR-IC-012 §P5/§332): a
        // not-executed clearance REISSUES the debit. Two ConfirmDebit emissions for the SAME process id (the
        // original + the reissue) carry DIFFERENT operational message_ids (per emission) but the SAME stable
        // process-id-derived external reference (CoreHoldRef) in the BODY — which IS the external_reference
        // the ACL folds into its idempotency key. So even if the original silently executed, the reissue is
        // deduped at the Core by that stable reference: the eager-SettleAsync double-move is impossible by
        // construction. We assert the bodies' external reference is identical across emissions.
        var processId = Guid.NewGuid();
        await StartSagaAsync(processId, SettlementProcess.States.ConfirmingDebit, correlationId: null);

        var first = await SeedCommandAsync(processId, SettlementProcess.ConfirmDebit, correlationId: null);
        var second = await SeedCommandAsync(processId, SettlementProcess.ConfirmDebit, correlationId: null);

        // The two emissions are distinct outbox rows (distinct operational message_ids) ...
        Assert.NotEqual(first, second);

        // ... and the STABLE external reference the ACL keys on — the process-id-derived CoreHoldRef — is
        // IDENTICAL across both bodies. That is the load-bearing invariant: the message_id (per-delivery)
        // and the causation reference (per-triggering-event) legitimately differ, but the external_reference
        // the Core dedups on does NOT, so even if the original silently executed, the reissue is deduped at
        // the Core by that stable reference (ADR-IC-012 §P4/§P5/§332) — the eager double-move is impossible.
        var expectedCoreHoldRef = "CORE-HOLD-" + processId.ToString("N");
        var firstBody = await PayloadAsync(first);
        var secondBody = await PayloadAsync(second);
        Assert.Contains(expectedCoreHoldRef, firstBody, StringComparison.Ordinal);
        Assert.Contains(expectedCoreHoldRef, secondBody, StringComparison.Ordinal);
    }

    // ---- Host wiring (the SETTLEMENT saga's bridge + router; engine target is never hit here) ---------

    private IHost BuildHost()
    {
        var options = new SagaCommandDispatcherOptions
        {
            ConnectionString = ConnectionString,
            EngineBaseUrl = "http://engine.invalid", // no settlement leg goes to the engine.
            SettlementBaseUrl = _acl.Url!,
            PollInterval = TimeSpan.FromMilliseconds(100),
        };
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ISagaCommandRouter>(new SettlementCommandRouter(options));
        builder.Services.AddSingleton<ICommandRouter>(sp =>
            new CompositeCommandRouter(sp.GetServices<ISagaCommandRouter>()));
        builder.Services.AddHttpClient();
        // The advance handler (the bridge self-advances the saga on a terminal delivery outcome).
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

    private async Task DrainUntilPublishedAsync(Guid messageId)
    {
        using var host = BuildHost();
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () => await StatusAsync(messageId) == "PUBLISHED",
                TimeSpan.FromSeconds(30),
                "the dispatcher did not deliver the settlement cash leg to the WireMock ACL");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private IReadOnlyList<AclRequest> AclRequests() =>
        _acl.LogEntries
            .Select(e => new AclRequest(
                Path: e.RequestMessage?.Path ?? string.Empty,
                Method: e.RequestMessage?.Method ?? string.Empty,
                IdempotencyKey: HeaderValue(e.RequestMessage?.Headers, "Idempotency-Key")))
            .ToList();

    private static string? HeaderValue(IDictionary<string, WireMock.Types.WireMockList<string>>? headers, string name) =>
        headers is not null && headers.TryGetValue(name, out var values) && values.Count > 0
            ? values[0]
            : null;

    private sealed record AclRequest(string Path, string Method, string? IdempotencyKey);

    // ---- Seed helpers -------------------------------------------------------------------------------

    private async Task StartSagaAsync(Guid processId, string state, Guid? correlationId)
    {
        var stateStore = new SagaStateStore();
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        // Start in the settlement saga's initial state, then advance the row to the in-flight state the leg
        // is decided from — the FK the saga_outbox row needs is the saga_state row (same transaction).
        await stateStore.TryStartAsync(
            connection, tx, processId, subjectId: processId, SettlementProcess.Type, SettlementProcess.States.SettlementStarted, correlationId);
        await stateStore.TryAdvanceAsync(connection, tx, processId, expectedVersion: 0, state, default);
        await tx.CommitAsync();
    }

    private async Task<Guid> SeedCommandAsync(Guid processId, string commandType, Guid? correlationId)
    {
        var sink = new SettlementCommandOutboxSink();
        var traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await sink.EmitAsync(
            connection, tx, processId, commandType, causationMessageId: Guid.NewGuid(),
            correlationId: correlationId, traceParent: traceParent);
        await tx.CommitAsync();

        await using var read = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT message_id FROM saga_outbox WHERE process_id = @p AND command_type = @t ORDER BY seq DESC LIMIT 1;",
            read);
        command.Parameters.AddWithValue("p", processId);
        command.Parameters.AddWithValue("t", commandType);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> PayloadAsync(Guid messageId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT convert_from(payload, 'UTF8') FROM saga_outbox WHERE message_id = @id;", connection);
        command.Parameters.AddWithValue("id", messageId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> StatusAsync(Guid messageId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM saga_outbox WHERE message_id = @id;", connection);
        command.Parameters.AddWithValue("id", messageId);
        return (string)(await command.ExecuteScalarAsync())!;
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
