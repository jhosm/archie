using System.Net;
using System.Text;
using System.Text.Json;
using Babelstone.Orchestrator.Dispatch;
using Babelstone.Families.TermDeposit.Orchestration;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Saga;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// The end-to-end proof that the saga's command outbox actually DRIVES the engine (bd
/// babelstone-t7o3.3, ADR-PC-029). The saga decides commands and writes them to <c>saga_outbox</c>
/// (the H.2 write side); these tests prove the new DISPATCHER drains that outbox and delivers each
/// command to the engine over idempotent HTTP — the row's <c>message_id</c> becomes the
/// <c>Idempotency-Key</c> the engine dedups on (ADR-PC-029 slot 1/4), the row's W3C
/// <c>traceparent</c> propagates (H.5 / ADR-IC-007 Layer 1), and the dispatcher applies the slot-5
/// error model: a 2xx flips the row PUBLISHED, a 4xx is a TERMINAL FAILED (surfaced, never dropped),
/// and a 5xx/timeout leaves the row PENDING for an idempotency-safe retry.
/// </summary>
/// <remarks>
/// The engine is stood up here as a MINIMAL in-process test HTTP server (the lane's sanctioned
/// stand-in) rather than the engine's <c>WebApplicationFactory&lt;Program&gt;</c>: the orchestrator
/// subtree must stay extraction-ready (ADR-PC-019 §P2 — no engine-kernel reference, even in tests
/// that would widen the build graph), and the dispatcher only needs a real HTTP endpoint that
/// records the request and returns a chosen status. The dispatcher↔engine CONTRACT itself (the
/// snake_case body, the mandatory key, the 201 shape, replay) is pinned by the Pact-style CDC tests
/// — the consumer side here-adjacent and the provider-verification side against the REAL engine in
/// the engine API test project.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SagaCommandDispatcherIntegrationTests : IAsyncLifetime
{
    // A DEDICATED PostgreSQL container, NOT the shared OrchestratorPostgresCollection fixture: the
    // dispatcher's hosted drain loop drains EVERY PENDING saga_outbox row in the database, so sharing
    // a DB with the other outbox-writing test classes would let their seeded rows leak into this
    // dispatcher's deliveries (and vice-versa). An isolated database means the dispatcher only ever
    // sees the rows this class seeded — the only way to assert exact request counts/routes.
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await new Migrations.MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    [Fact]
    public async Task The_dispatcher_drains_a_pending_ActivateDeposit_and_POSTs_it_with_the_message_id_as_the_idempotency_key()
    {
        var processId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        await StartSagaAsync(processId, correlationId);

        // Seed a PENDING ActivateDeposit command row (the saga decided it; the dispatcher must deliver it).
        var (messageId, traceParent) = await SeedCommandAsync(
            processId, ConstitutionProcess.ActivateDeposit, correlationId);

        // A stub engine that records the request and returns 201 (applied) — the contract is pinned
        // separately by the Pact-style CDC tests; here we only assert the dispatcher's delivery.
        await using var engine = new RecordingHttpServer(_ => (HttpStatusCode.Created, "{}"));

        using var host = BuildHost(engineBaseUrl: engine.BaseUrl, settlementBaseUrl: "http://settlement.invalid");
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () => await StatusAsync(messageId) == "PUBLISHED",
                TimeSpan.FromSeconds(30),
                "the dispatcher did not flip the ActivateDeposit row to PUBLISHED");
        }
        finally
        {
            await host.StopAsync();
        }

        // The row flipped to PUBLISHED on the 2xx.
        Assert.Equal("PUBLISHED", await StatusAsync(messageId));

        // THIS row's request reached the engine on the Pact-pinned route, carrying the row's
        // message_id as the Idempotency-Key (ADR-PC-029 slot 1) and the row's traceparent (H.5).
        // Matched by the row's own key so a sibling test's leftover PENDING row in the shared class
        // database cannot perturb the assertion.
        var request = Assert.Single(engine.Requests, r => r.IdempotencyKey == messageId.ToString());
        Assert.Equal("/v1/deposits", request.Path);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(traceParent, request.TraceParent);
    }

    [Fact]
    public async Task A_4xx_refusal_marks_the_row_FAILED_terminally_and_does_not_retry()
    {
        var processId = Guid.NewGuid();
        await StartSagaAsync(processId, correlationId: null);
        var (messageId, _) = await SeedCommandAsync(processId, ConstitutionProcess.ActivateDeposit, correlationId: null);

        // The engine REFUSES (422 — an illegal lifecycle transition / validation reject). Slot 5:
        // a terminal failure the dispatcher surfaces, never silently drops and never retries forever.
        // Count only the calls for THIS row's key so a sibling test's leftover row cannot perturb it.
        var calls = 0;
        var key = messageId.ToString();
        await using var engine = new RecordingHttpServer(request =>
        {
            if (request.IdempotencyKey == key)
            {
                Interlocked.Increment(ref calls);
            }

            return (HttpStatusCode.UnprocessableEntity, """{"title":"illegal transition"}""");
        });

        using var host = BuildHost(engineBaseUrl: engine.BaseUrl, settlementBaseUrl: "http://settlement.invalid");
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () => await StatusAsync(messageId) == "FAILED",
                TimeSpan.FromSeconds(30),
                "the dispatcher did not mark the refused row FAILED");
        }
        finally
        {
            await host.StopAsync();
        }

        // Terminal FAILED with the engine's status recorded — the saga's compensation path can react.
        Assert.Equal("FAILED", await StatusAsync(messageId));
        Assert.Equal(422, await FailureStatusCodeAsync(messageId));

        // Terminal means terminal: the dispatcher does not re-POST a refused command. Exactly one call
        // for this row's key, and it never climbed after the row went terminal.
        var settled = calls;
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.Equal(settled, calls);
        Assert.Equal(1, settled);
    }

    [Fact]
    public async Task A_5xx_leaves_the_row_PENDING_for_an_idempotency_safe_retry()
    {
        var processId = Guid.NewGuid();
        await StartSagaAsync(processId, correlationId: null);
        var (messageId, _) = await SeedCommandAsync(processId, ConstitutionProcess.ActivateDeposit, correlationId: null);

        // The engine is transiently unavailable (503). Slot 5: 5xx/timeout is TRANSIENT — leave the
        // row PENDING and retry; idempotency (the engine's command_dedup keyed on message_id) makes
        // the retry safe. The row must NEVER reach FAILED on a 5xx.
        await using var engine = new RecordingHttpServer(_ => (HttpStatusCode.ServiceUnavailable, "{}"));

        using var host = BuildHost(engineBaseUrl: engine.BaseUrl, settlementBaseUrl: "http://settlement.invalid");
        await host.StartAsync();
        try
        {
            // Let the loop attempt (and re-attempt) the delivery a few times. Count only THIS row's
            // key so a sibling test's leftover row cannot satisfy the retry condition vacuously.
            var key = messageId.ToString();
            await WaitUntilAsync(
                async () => engine.Requests.Count(r => r.IdempotencyKey == key) >= 2
                    && await StatusAsync(messageId) == "PENDING",
                TimeSpan.FromSeconds(30),
                "the dispatcher did not retry the 5xx while keeping the row PENDING");
        }
        finally
        {
            await host.StopAsync();
        }

        // Still PENDING (never FAILED) — a transient failure is retried, not surfaced as terminal.
        Assert.Equal("PENDING", await StatusAsync(messageId));
    }

    [Fact]
    public async Task A_settlement_command_routes_to_the_configured_settlement_target_not_the_engine()
    {
        var processId = Guid.NewGuid();
        await StartSagaAsync(processId, correlationId: null);
        var (messageId, _) = await SeedCommandAsync(processId, ConstitutionProcess.ReserveAccountBalance, correlationId: null);

        // TWO stub servers: the engine and the configurable settlement/ACL target. A settlement
        // command (ReserveAccountBalance) must hit the SETTLEMENT target, never the engine — the
        // routing seam (bd babelstone-t7o3.3; the real ACL is DEF-1/bd ub9s, a WireMock stub at v1).
        await using var engine = new RecordingHttpServer(_ => (HttpStatusCode.Created, "{}"));
        await using var settlement = new RecordingHttpServer(_ => (HttpStatusCode.OK, "{}"));

        using var host = BuildHost(engineBaseUrl: engine.BaseUrl, settlementBaseUrl: settlement.BaseUrl);
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () => await StatusAsync(messageId) == "PUBLISHED",
                TimeSpan.FromSeconds(30),
                "the dispatcher did not deliver the settlement command");
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.Equal("PUBLISHED", await StatusAsync(messageId));
        // THIS command (keyed by its message_id) reached the SETTLEMENT target and NEVER the engine —
        // asserted by the row's own Idempotency-Key, robust against any leftover PENDING row a sibling
        // test left in this class's shared database.
        Assert.Contains(settlement.Requests, r => r.IdempotencyKey == messageId.ToString());
        Assert.DoesNotContain(engine.Requests, r => r.IdempotencyKey == messageId.ToString());
    }

    // ---- Host wiring (mirrors the production Program.cs dispatcher composition) -----------------

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
        // The command-outcome → result-event bridge (bd babelstone-t7o3.8) injects the SagaAdvanceHandler
        // (+ its stores) so the drainer can self-advance the saga on a terminal delivery outcome. These
        // sagas are seeded in STARTED with a single command; the synthesized result events (e.g.
        // ProcessConstituted from ActivateDeposit) have NO transition from STARTED → AdvanceAsync returns
        // NoTransition → a graceful no-op, so the PUBLISHED/FAILED row assertions still hold.
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

    // The SagaAdvanceHandler composition (mirrors Program.cs): the bridge self-advances the saga through
    // it on a terminal delivery outcome, on the SAME connection+transaction as the status flip.
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

    // ---- Seed helpers --------------------------------------------------------------------------

    private async Task StartSagaAsync(Guid processId, Guid? correlationId)
    {
        var stateStore = new SagaStateStore();
        var businessRefStore = new SagaBusinessReferenceStore();
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await stateStore.TryStartAsync(
            connection, tx, processId, ConstitutionProcess.Type, ConstitutionProcess.States.Started, correlationId);

        // Pin the per-saga business references the full-payload factory reads (mandatory now — bd
        // babelstone-t7o3.9). The FK requires the saga_state row to exist first (same transaction).
        // PII-free: integer cents + opaque references + a closed client-type code.
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

    private async Task<(Guid MessageId, string? TraceParent)> SeedCommandAsync(
        Guid processId, string commandType, Guid? correlationId)
    {
        var sink = new SagaCommandOutboxSink();
        var traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await sink.EmitAsync(
            connection, tx, processId, commandType, causationMessageId: Guid.NewGuid(),
            correlationId: correlationId, traceParent: traceParent);
        await tx.CommitAsync();

        // Read back the freshly minted delivery message id the sink wrote (the Idempotency-Key).
        await using var read = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT message_id, traceparent FROM saga_outbox WHERE process_id = @p AND command_type = @t ORDER BY seq DESC LIMIT 1;",
            read);
        command.Parameters.AddWithValue("p", processId);
        command.Parameters.AddWithValue("t", commandType);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1));
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
