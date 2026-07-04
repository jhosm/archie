using System.Net;
using System.Text.Json;
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
/// The CONSUMER half of the dispatcher↔engine command contract (ENGINE_COMMAND_PACT, ADR-PC-029
/// slot 6 / ADR-IC-009). A consumer-driven contract (CDC): the dispatcher (the consumer) declares
/// EXACTLY the request it makes of the engine command endpoint (the provider), and asserts the
/// contract holds from its own side. The complementary PROVIDER-verification half
/// (<c>EngineCommandPactProviderTests</c> in the engine API test project) replays this same expected
/// request against the REAL engine via <c>WebApplicationFactory&lt;Program&gt;</c> and asserts the
/// engine honours it — so a provider-side break (the engine dropping the Idempotency-Key requirement,
/// changing the snake_case body, or the 201 shape) fails that build.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a hand-rolled Pact-style CDC, not the PactNet FFI.</b> PactNet (the formal Pact .NET
/// library) carries a native Rust FFI (libpact_ffi) that must download/bundle per-platform on CI; a
/// greenfield Pact broker harness is a larger, CI-fragile change than this lane should land cleanly.
/// So this is a Pact-STYLE CDC: a shared, explicit <see cref="EngineCommandContract"/> the consumer
/// produces against and the provider verifies against — the same consumer-drives-the-contract
/// discipline, pinned in code, without the native dependency. The catalogue row 20 therefore stays
/// <c>Planned</c> with a clear note (the formal PactNet harness + broker is the follow-up); the
/// contract is concretely held by these two tests in the meantime — never faked Live.
/// </para>
/// <para>
/// This consumer test drives the REAL <see cref="SagaCommandDispatchDrainer"/> against a stub that
/// asserts the contract on the way IN (the request the dispatcher built) and returns the contract's
/// expected 201 response. It proves the dispatcher's outbound request MATCHES
/// <see cref="EngineCommandContract"/> — the mandatory <c>Idempotency-Key</c> (the saga_outbox
/// message_id, a UUID), the <c>POST /v1/deposits</c> route, and the JSON body — so the two halves are
/// pinned to the SAME contract object.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class EngineCommandPactConsumerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.GatedStartAsync();
        await new Migrations.MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    [Fact]
    public async Task The_dispatcher_request_honours_the_engine_command_contract()
    {
        var processId = Guid.NewGuid();
        await StartSagaAsync(processId);
        var messageId = await SeedActivateDepositAsync(processId);

        // The stub provider asserts the CONSUMER side of EngineCommandContract on every inbound
        // request: the Pact-pinned route, the POST method, a present-and-UUID Idempotency-Key, and a
        // well-formed JSON body. It returns the contract's expected 201 so the dispatcher flips the
        // row PUBLISHED — closing the consumer leg.
        var contractHeld = false;
        await using var engine = new RecordingHttpServer(request =>
        {
            EngineCommandContract.AssertConsumerRequest(request);
            contractHeld = true;
            return (HttpStatusCode.Created, EngineCommandContract.ExpectedCreatedBody(messageId));
        });

        using var host = BuildHost(engine.BaseUrl);
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () => await StatusAsync(messageId) == "PUBLISHED",
                TimeSpan.FromSeconds(30),
                "the dispatcher did not deliver the ActivateDeposit under the engine command contract");
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.True(contractHeld, "the stub provider never saw a contract-conformant request");
        var request = Assert.Single(engine.Requests);
        // Restate the contract assertions at the test level so a failure points here, not just inside
        // the stub callback.
        Assert.Equal("/v1/deposits", request.Path);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.True(Guid.TryParse(request.IdempotencyKey, out var key) && key == messageId);
    }

    // ---- Host wiring + seed --------------------------------------------------------------------

    private IHost BuildHost(string engineBaseUrl)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(new SagaCommandDispatcherOptions
        {
            ConnectionString = ConnectionString,
            EngineBaseUrl = engineBaseUrl,
            SettlementBaseUrl = "http://settlement.invalid",
            PollInterval = TimeSpan.FromMilliseconds(100),
        });
        builder.Services.AddSingleton<ICommandRouter, SagaCommandRouter>();
        builder.Services.AddHttpClient();
        // The command-outcome → result-event bridge (bd babelstone-t7o3.8) injects the SagaAdvanceHandler
        // so the drainer can self-advance the saga on a terminal delivery outcome. This Pact test seeds a
        // STARTED saga + a single ActivateDeposit; the synthesized ProcessConstituted has no transition
        // from STARTED → NoTransition → graceful no-op, so the delivery contract assertions are unaffected.
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
            sp.GetRequiredService<ISagaCommandSink>()));
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

    private async Task StartSagaAsync(Guid processId)
    {
        var stateStore = new SagaStateStore();
        var businessRefStore = new SagaBusinessReferenceStore();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await stateStore.TryStartAsync(
            connection, tx, processId, subjectId: processId, ConstitutionProcess.Type, ConstitutionProcess.States.Started, correlationId: null);

        // Pin the per-saga business references the full-payload factory reads (mandatory now — bd
        // babelstone-t7o3.9). The FK requires the saga_state row to exist first (same transaction).
        await businessRefStore.TryInsertAsync(
            connection, tx,
            // The MINIMAL business reference (Fork B rework, bd t7o3.11 / 3k10 / c8d8): the orchestrator
            // pins only the product code + amount + account/threshold references. The structural product
            // facts the rejected v1 stand-in pinned here are GONE — the engine resolves them from the
            // product code at constitution (the maintainer's Q2 choice, ADR-PC-009). The product code is
            // a real launch variant so the dispatched body matches what the PROVIDER half
            // (EngineCommandPactProviderTests) replays against the REAL engine — both halves pin the same
            // minimal {deposit_id, product_id, principal_cents, funding_account} contract shape.
            new SagaBusinessReference(
                ProcessId: processId,
                ProductRef: "dpz_pt_12m_juros_venc",
                AmountMinorUnits: 100_00,
                SourceAccountRef: "acct-ref-001",
                InterestAccountRef: null,
                DepositRef: "DEP-" + processId.ToString("N"),
                ClientType: ClientType.Existing,
                AutoApprovalThresholdMinorUnits: 1_000_00));
        await tx.CommitAsync();
    }

    private async Task<Guid> SeedActivateDepositAsync(Guid processId)
    {
        var sink = new SagaCommandOutboxSink();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await sink.EmitAsync(
            connection, tx, processId, ConstitutionProcess.ActivateDeposit,
            causationMessageId: Guid.NewGuid(), correlationId: null);
        await tx.CommitAsync();

        await using var read = new NpgsqlConnection(ConnectionString);
        await read.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT message_id FROM saga_outbox WHERE process_id = @p ORDER BY seq DESC LIMIT 1;", read);
        command.Parameters.AddWithValue("p", processId);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> StatusAsync(Guid messageId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM saga_outbox WHERE message_id = @id;", connection);
        command.Parameters.AddWithValue("id", messageId);
        return (string)(await command.ExecuteScalarAsync())!;
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
