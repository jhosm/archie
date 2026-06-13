using System.Net;
using Babelstone.Orchestrator.Dispatch;
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
/// The end-to-end gated-settlement proof (bd babelstone-t7o3.4, ADR-PC-016 §68/§127 · ADR-PC-029
/// slot 2). The engine's constitution append is now DE-SETTLED — it appends <c>DepositConstituted</c>
/// only, with NO in-engine money leg. The principal debit is the constitution SAGA's gated step: the
/// orchestrator decides <c>ReserveAccountBalance</c> (the reversible hold) then <c>ConfirmDebit</c>
/// (the irreversible debit) into <c>saga_outbox</c>, and the dispatcher (bd babelstone-t7o3.3) delivers
/// them over idempotent HTTP to the Core ACL. At v1 that ACL is a WireMock stub (the real ACL is DEF-1,
/// bd ub9s); this test stands one up in-process and proves the legs actually arrive there.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why WireMock.Net rather than the lane's bare <see cref="RecordingHttpServer"/>.</b> The dispatcher
/// CONTRACT (route, Idempotency-Key, traceparent, slot-5 error model) is already pinned by
/// <see cref="SagaCommandDispatcherIntegrationTests"/>. This test exercises the v1 SETTLEMENT TARGET the
/// way it will actually be configured: a WireMock-stubbed Core ACL with explicit per-endpoint mappings —
/// a success mapping (the happy path) AND an <c>InsufficientBalance</c>/refusal mapping (so the saga's
/// compensation trigger is exercisable). The same WireMock service is what the dev stack runs
/// (infra/compose.yaml), so the test and the dev stack share one stub shape.
/// </para>
/// <para>
/// <b>Extraction-ready (ADR-PC-019 §P2).</b> WireMock.Net is a TEST-only dependency; it does not widen
/// the runtime orchestrator's dependency graph. A DEDICATED Postgres container (not the shared
/// collection fixture) isolates this class's <c>saga_outbox</c> rows so the dispatcher only ever sees the
/// rows seeded here — the only way to assert exact delivery counts/routes.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class GatedSettlementWireMockIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private WireMockServer _acl = null!;

    private string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await new Migrations.MigrationRunner(ConnectionString).ApplyAsync();

        // The v1 Core-ACL settlement stub. Five settlement endpoints (ADR-PC-016 §Payload), the same
        // routes the production SagaCommandRouter maps the settlement commands to. The happy-path
        // reserve+debit accept; the dedicated /insufficient probe route refuses with a 422 carrying the
        // ACL's InsufficientBalance category so the saga's compensation trigger is exercisable.
        _acl = WireMockServer.Start();

        // ReserveAccountBalance → reversible hold accepted (201 Created).
        _acl.Given(Request.Create().WithPath("/v1/reservations").UsingPost())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.Created)
                .WithHeader("Content-Type", "application/json").WithBody("""{"reservation":"held"}"""));

        // ConfirmDebit → irreversible debit confirmed (200 OK).
        _acl.Given(Request.Create().WithPath("/v1/debits").UsingPost())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.OK)
                .WithHeader("Content-Type", "application/json").WithBody("""{"debit":"confirmed"}"""));

        // ReleaseBalanceReservation / ReverseCoreDebit → compensation legs always accepted (idempotent).
        _acl.Given(Request.Create().WithPath("/v1/reservations/release").UsingPost())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.OK).WithBody("{}"));
        _acl.Given(Request.Create().WithPath("/v1/debits/reverse").UsingPost())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.OK).WithBody("{}"));

        // InsufficientBalance refusal probe: a reservation against this dedicated path is REFUSED (422)
        // with the ACL's InsufficientBalance domain category (ADR-PC-016 slot 5 — the constitution
        // debit is gated on funds). The dispatcher classifies a 4xx as TERMINAL FAILED, which is the
        // saga's compensation/DepositConstitutionFailed trigger.
        _acl.Given(Request.Create().WithPath("/v1/reservations/insufficient").UsingPost())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.UnprocessableEntity)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"category":"InsufficientBalance","title":"insufficient funds"}"""));
    }

    public async Task DisposeAsync()
    {
        _acl.Stop();
        await _pg.DisposeAsync();
    }

    [Fact]
    public async Task The_saga_settlement_legs_are_delivered_to_the_WireMock_Core_ACL_on_the_success_path()
    {
        // The saga decided the two happy-path settlement legs (the reversible hold then the irreversible
        // debit) into saga_outbox. The engine's constitution append carried NO money leg — these legs ARE
        // the relocated settlement (ADR-PC-016 §68/§127). Seed them PENDING; the dispatcher must deliver
        // both to the WireMock ACL and flip both PUBLISHED.
        var processId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        await StartSagaAsync(processId, correlationId);
        var (reserveId, _) = await SeedCommandAsync(processId, ConstitutionProcess.ReserveAccountBalance, correlationId);
        var (confirmId, _) = await SeedCommandAsync(processId, ConstitutionProcess.ConfirmDebit, correlationId);

        using var host = BuildHost(settlementBaseUrl: _acl.Url!);
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () => await StatusAsync(reserveId) == "PUBLISHED" && await StatusAsync(confirmId) == "PUBLISHED",
                TimeSpan.FromSeconds(30),
                "the dispatcher did not deliver both settlement legs to the WireMock ACL");
        }
        finally
        {
            await host.StopAsync();
        }

        // Both legs flipped PUBLISHED on the ACL's 2xx.
        Assert.Equal("PUBLISHED", await StatusAsync(reserveId));
        Assert.Equal("PUBLISHED", await StatusAsync(confirmId));

        // Both legs reached the WireMock ACL on their mapped routes, each carrying its row's message_id
        // as the Idempotency-Key (ADR-PC-029 slot 1/4). Matched by THIS process's keys so a sibling
        // test's leftover row in a shared DB could not perturb the assertion (here the DB is isolated,
        // but the key-scoped match is the robust discipline).
        var reservationHit = Assert.Single(AclRequests(), r => r.Path == "/v1/reservations" && r.IdempotencyKey == reserveId.ToString());
        Assert.Equal("POST", reservationHit.Method);

        var debitHit = Assert.Single(AclRequests(), r => r.Path == "/v1/debits" && r.IdempotencyKey == confirmId.ToString());
        Assert.Equal("POST", debitHit.Method);
    }

    [Fact]
    public async Task An_InsufficientBalance_refusal_from_the_ACL_marks_the_settlement_leg_FAILED_for_compensation()
    {
        // The funding account lacks the principal: the Core ACL refuses the reversible hold with a 422
        // InsufficientBalance (ADR-PC-016 slot 5 — the constitution debit is gated on funds). The
        // dispatcher classifies the 4xx as TERMINAL FAILED with the status recorded — exactly the signal
        // the saga's compensation path reacts to (toward DepositConstitutionFailed). The leg routes to
        // the dedicated /insufficient probe path, which the router maps via a refusing command name.
        var processId = Guid.NewGuid();
        await StartSagaAsync(processId, correlationId: null);
        var (reserveId, _) = await SeedCommandAsync(
            processId, RefusingReserveCommandType, correlationId: null);

        using var host = BuildHost(settlementBaseUrl: _acl.Url!);
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () => await StatusAsync(reserveId) == "FAILED",
                TimeSpan.FromSeconds(30),
                "the dispatcher did not mark the refused settlement leg FAILED");
        }
        finally
        {
            await host.StopAsync();
        }

        // Terminal FAILED with the ACL's 422 recorded — the saga's compensation/DepositConstitutionFailed
        // trigger. Never silently dropped (ADR-PC-029 slot 5).
        Assert.Equal("FAILED", await StatusAsync(reserveId));
        Assert.Equal(422, await FailureStatusCodeAsync(reserveId));

        // The refusal genuinely came from the WireMock ACL probe route (the saga sent it; the ACL
        // refused it), keyed by this row's Idempotency-Key.
        Assert.Single(AclRequests(), r => r.Path == "/v1/reservations/insufficient" && r.IdempotencyKey == reserveId.ToString());
    }

    // ---- Host wiring (mirrors the production dispatcher composition; engine target is never hit here) ----

    private IHost BuildHost(string settlementBaseUrl)
    {
        var options = new SagaCommandDispatcherOptions
        {
            ConnectionString = ConnectionString,
            // No settlement command routes to the engine; an unreachable engine URL proves it.
            EngineBaseUrl = "http://engine.invalid",
            SettlementBaseUrl = settlementBaseUrl,
            PollInterval = TimeSpan.FromMilliseconds(100),
        };
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ICommandRouter>(new GatedSettlementRouter(options));
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton(sp => new SagaCommandDispatchDrainer(
            sp.GetRequiredService<SagaCommandDispatcherOptions>(),
            sp.GetRequiredService<ICommandRouter>(),
            sp.GetRequiredService<IHttpClientFactory>()));
        builder.Services.AddHostedService<SagaCommandDispatcherService>();
        return builder.Build();
    }

    // A synthetic command name the test uses to drive the ACL's InsufficientBalance probe route. It is
    // NOT a ConstitutionProcess command; the wrapper router below routes it to /v1/reservations/insufficient
    // so the refusal mapping is reachable WITHOUT a second WireMock server or a stateful first-call-fails
    // mapping — the production router (and its commands) is unchanged.
    private const string RefusingReserveCommandType = "ReserveAccountBalanceInsufficient";

    /// <summary>
    /// The production <see cref="SagaCommandRouter"/> plus one extra route for the test-only refusal
    /// probe command. Every real command routes exactly as production; only the synthetic
    /// <see cref="RefusingReserveCommandType"/> gains a route, to the ACL's /insufficient mapping.
    /// </summary>
    private sealed class GatedSettlementRouter(SagaCommandDispatcherOptions options) : ICommandRouter
    {
        private readonly SagaCommandRouter _production = new(options);
        private readonly string _settlementBaseUrl = options.SettlementBaseUrl;

        public CommandRoute? Resolve(string commandType) => commandType == RefusingReserveCommandType
            ? new CommandRoute(_settlementBaseUrl, "/v1/reservations/insufficient", HttpMethod.Post)
            : _production.Resolve(commandType);
    }

    /// <summary>Flatten the WireMock ACL's log into the load-bearing fields (path, method, the
    /// dispatcher's Idempotency-Key header) as a null-safe projection — so the assertions match on plain
    /// records rather than dereferencing WireMock's nullable log graph.</summary>
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

    // ---- Seed helpers (mirror SagaCommandDispatcherIntegrationTests) ---------------------------------

    private async Task StartSagaAsync(Guid processId, Guid? correlationId)
    {
        var stateStore = new SagaStateStore();
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await stateStore.TryStartAsync(
            connection, tx, processId, ConstitutionProcess.Type, SagaState.Started, correlationId);
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
