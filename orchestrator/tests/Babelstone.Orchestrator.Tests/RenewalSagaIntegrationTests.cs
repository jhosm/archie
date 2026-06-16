using System.Globalization;
using System.Net;
using System.Text;
using Babelstone.Families.TermDeposit.Orchestration;
using Babelstone.Orchestrator.Dispatch;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Saga;
using Babelstone.TestFixtures;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// The end-to-end proof of the EVENT-AUTO-STARTED renewal saga (bd babelstone-mtto PR2) — the SECOND
/// saga on the family-agnostic substrate. A real <c>DepositMatured</c> record is produced onto Redpanda
/// with the SAME CloudEvents Binary-mode headers the engine's outbox relay emits (ADR-IC-015), INCLUDING
/// the <c>ce_autorenewalpolicy</c> extension header PR A promotes. Both the renewal consume loop and the
/// command dispatcher run (over a migrated PG container + a stub engine), so this exercises the FULL
/// path:
/// <list type="number">
///   <item>the substrate's auto-start machinery starts the saga on the matching <c>DepositMatured</c>
///   (the header predicate passes) → RENEWAL_CONSTITUTING, emitting <c>ConstituteRenewal</c>;</item>
///   <item>the dispatcher POSTs <c>ConstituteRenewal</c> to the engine's
///   <c>/v1/deposits/{process_id}/constitute-renewal</c> (the {process_id} URL-template substituted with
///   the closing deposit id), gets a 201, and the result-event bridge SYNTHESIZES
///   <c>NewDepositConstituted</c> → RENEWAL_LINKING, emitting <c>LinkRenewal</c>;</item>
///   <item>the dispatcher POSTs <c>LinkRenewal</c> to <c>/v1/deposits/{process_id}/renewal-link</c>, gets
///   a 200, and the bridge synthesizes <c>RenewalLinkConfirmed</c> → RENEWAL_COMPLETED (terminal).</item>
/// </list>
/// And the negative: a <c>NONE</c>-policy <c>DepositMatured</c> starts NO saga (the header predicate
/// fails — a NONE deposit terminates at maturity).
/// </summary>
/// <remarks>
/// The engine is a MINIMAL in-process RecordingHttpServer (the lane's sanctioned stand-in) rather than the
/// engine's WebApplicationFactory: the orchestrator subtree stays extraction-ready (ADR-PC-019 §P2 — no
/// engine-kernel reference, even in tests). The dispatcher↔engine renewal CONTRACT is pinned separately by
/// the Pact-style CDC against the real engine; here we assert the saga's autonomous progression and the
/// {process_id} path templating.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RenewalSagaIntegrationTests : IAsyncLifetime
{
    private readonly RedpandaFixture _redpanda = new();
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_redpanda.InitializeAsync(), _pg.StartAsync());
        await new Migrations.MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync()
    {
        await _pg.DisposeAsync();
        await _redpanda.DisposeAsync();
    }

    [Fact]
    public async Task A_matured_deposit_with_a_renewal_policy_auto_starts_the_saga_and_drives_it_to_RENEWAL_COMPLETED()
    {
        var topic = SagaConsumeTopics.TermDepositIntegrationTopic;

        // The closing deposit id IS the saga's process_id (ce_subject on DepositMatured). No saga exists
        // yet — the renewal saga is BORN on this bus fact (auto-start), not edge-started.
        var closingDepositId = Guid.NewGuid();
        Assert.Null(await StateOrNullAsync(closingDepositId));

        // Produce a DepositMatured with a NON-NONE ce_autorenewalpolicy extension header — the auto-start
        // discriminator PR A promotes from the event's IntegrationHeaders.
        var maturedMessageId = Guid.NewGuid();
        await ProduceDepositMaturedAsync(topic, maturedMessageId, closingDepositId, "SAME_TERM_CURRENT_RATE");

        // The stub engine: 201 for the constitute leg (opens the new stream), 200 for the link leg (folds
        // the close). The dispatcher's bridge synthesizes the forward signals from these 2xx outcomes.
        await using var engine = new RecordingHttpServer(req =>
            req.Path.EndsWith("/constitute-renewal", StringComparison.Ordinal)
                ? (HttpStatusCode.Created, "{}")
                : (HttpStatusCode.OK, "{}"));

        using var host = BuildHost(engineBaseUrl: engine.BaseUrl);
        await host.StartAsync();
        try
        {
            // Poll until the saga walks the whole cross-stream sequence to the terminal state.
            await WaitUntilAsync(
                async () => await StateOrNullAsync(closingDepositId) == RenewalProcess.States.RenewalCompleted,
                TimeSpan.FromSeconds(60),
                "the renewal saga did not auto-start and drive to RENEWAL_COMPLETED");
        }
        finally
        {
            await host.StopAsync();
        }

        // The saga reached the terminal state, entirely autonomously (auto-started off the bus, then
        // self-advanced through the two engine legs via the bridge).
        Assert.Equal(RenewalProcess.States.RenewalCompleted, await StateOrNullAsync(closingDepositId));

        // It was persisted as the RenewalProcess saga type (the auto-start machinery stamped it).
        Assert.Equal(RenewalProcess.Type, await SagaTypeAsync(closingDepositId));

        // Both renewal commands were emitted, in order, and both flipped PUBLISHED on their 2xx.
        Assert.Equal(
            new[] { RenewalProcess.ConstituteRenewal, RenewalProcess.LinkRenewal },
            await OutboxCommandsAsync(closingDepositId));

        // KEY INTEGRATION POINT — the {process_id} URL templating: both legs reached the engine on the
        // id-in-path routes, with the CLOSING deposit id (= process_id) substituted into the path.
        Assert.Contains(engine.Requests,
            r => r.Path == $"/v1/deposits/{closingDepositId}/constitute-renewal" && r.Method == HttpMethod.Post);
        Assert.Contains(engine.Requests,
            r => r.Path == $"/v1/deposits/{closingDepositId}/renewal-link" && r.Method == HttpMethod.Post);

        // Each leg carried a deterministic Idempotency-Key (the saga_outbox row id) — at-least-once safe.
        Assert.All(
            engine.Requests.Where(r => r.Path.Contains(closingDepositId.ToString(), StringComparison.Ordinal)),
            r => Assert.True(Guid.TryParse(r.IdempotencyKey, out _)));
    }

    [Fact]
    public async Task A_NONE_policy_matured_deposit_starts_NO_saga()
    {
        var topic = SagaConsumeTopics.TermDepositIntegrationTopic;
        var closingDepositId = Guid.NewGuid();

        // A NONE-policy DepositMatured: the deposit terminates at maturity, never renews. The auto-start
        // header predicate (autorenewalpolicy != NONE) FAILS, so no saga is born.
        var maturedMessageId = Guid.NewGuid();
        await ProduceDepositMaturedAsync(topic, maturedMessageId, closingDepositId, "NONE");

        // No leg should ever be POSTed — the engine stub records nothing for this deposit.
        await using var engine = new RecordingHttpServer(_ => (HttpStatusCode.Created, "{}"));

        using var host = BuildHost(engineBaseUrl: engine.BaseUrl);
        await host.StartAsync();
        try
        {
            // The loop consumes the record (it is a real DepositMatured), but the predicate fails so the
            // event is the existing UnknownSaga no-op: dedup-rowed, offset advanced, NO saga row created.
            // Wait until the message has been deduped (consumed), then assert no saga exists.
            await WaitUntilAsync(
                async () => await CountInboxAsync(maturedMessageId) == 1,
                TimeSpan.FromSeconds(40),
                "the NONE-policy DepositMatured was not consumed");

            // Give the dispatcher a beat in case a saga had (wrongly) started and queued a command.
            await Task.Delay(TimeSpan.FromMilliseconds(750));
        }
        finally
        {
            await host.StopAsync();
        }

        // No saga was started — the header predicate gated it out at the substrate.
        Assert.Null(await StateOrNullAsync(closingDepositId));
        // And no renewal command was emitted or POSTed for this deposit.
        Assert.Empty(await OutboxCommandsAsync(closingDepositId));
        Assert.DoesNotContain(engine.Requests,
            r => r.Path.Contains(closingDepositId.ToString(), StringComparison.Ordinal));
    }

    // ---- Host wiring (the SAME composition the production Program.cs uses for the renewal module) -----

    private IHost BuildHost(string engineBaseUrl)
    {
        var builder = Host.CreateApplicationBuilder();

        var context = new SagaModuleContext(
            RuntimeConnectionString: ConnectionString,
            EngineBaseUrl: engineBaseUrl,
            SettlementBaseUrl: "http://settlement.invalid");

        // ONLY the renewal module here (the auto-start + dispatch path under test). The substrate is
        // family-agnostic, so a single-module host exercises the full machinery without standing up the
        // edge-started constitution saga too. The module carries no product/role/funding config — the
        // engine resolves every renewal fact from the closing deposit (ADR-PC-009; bd babelstone-mtto.5).
        var renewalModule = new RenewalSagaModule(context);
        var modules = new ISagaModule[] { renewalModule };

        renewalModule.ConfigureServices(builder.Services, context);
        builder.Services.AddSingleton(renewalModule.StateMachine);
        builder.Services.AddSingleton(renewalModule.ResultEventBridge);
        builder.Services.AddSingleton(renewalModule.CommandRouter);

        builder.Services.AddSingleton<SagaStateStore>();
        builder.Services.AddSingleton<SagaTransitionLog>();
        builder.Services.AddSingleton<ISagaCommandSink>(sp =>
            new CompositeSagaCommandSink(sp.GetServices<ISagaTypedCommandSink>()));

        // The shared advance handler — hosts the renewal machine + the auto-start registry (the modules
        // are threaded in, exactly as Program.cs does).
        builder.Services.AddSingleton(sp => new SagaAdvanceHandler(
            sp.GetServices<ISagaStateMachine>(),
            sp.GetRequiredService<SagaStateStore>(),
            sp.GetRequiredService<SagaTransitionLog>(),
            sp.GetRequiredService<ISagaCommandSink>(),
            modules));

        // The renewal consume loop on its OWN group (per-module hosted service, the Program.cs shape).
        builder.Services.AddHostedService(sp =>
            new SagaInboxConsumerService(
                new SagaConsumeLoop(
                    new SagaInboxConsumerOptions
                    {
                        ConnectionString = ConnectionString,
                        BootstrapServers = _redpanda.BootstrapServers,
                        GroupId = $"orch-renewal-{Guid.NewGuid()}",
                        Topics = renewalModule.ConsumeTopics,
                        StartFromEarliest = true,
                    },
                    sp.GetRequiredService<SagaAdvanceHandler>())));

        // The dispatcher: drains saga_outbox, POSTs each command (substituting {process_id}), and
        // self-advances the saga via the bridge on the 2xx.
        builder.Services.AddSingleton(new SagaCommandDispatcherOptions
        {
            ConnectionString = ConnectionString,
            EngineBaseUrl = engineBaseUrl,
            SettlementBaseUrl = "http://settlement.invalid",
            PollInterval = TimeSpan.FromMilliseconds(100),
        });
        builder.Services.AddSingleton<ICommandRouter>(sp =>
            new CompositeCommandRouter(sp.GetServices<ISagaCommandRouter>()));
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton(sp => new SagaCommandDispatchDrainer(
            sp.GetRequiredService<SagaCommandDispatcherOptions>(),
            sp.GetRequiredService<ICommandRouter>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<SagaAdvanceHandler>(),
            sp.GetServices<IResultEventBridge>()));
        builder.Services.AddHostedService<SagaCommandDispatcherService>();

        return builder.Build();
    }

    // ---- Produce a CloudEvents-headed DepositMatured (mirror the outbox relay's header subset) -------

    private async Task ProduceDepositMaturedAsync(string topic, Guid messageId, Guid closingDepositId, string policy)
    {
        // The orchestrator keys on the ce_type record name + the ce_* extension headers (never the Avro
        // payload), so the auto-start machinery reads HEADERS alone. ce_subject is the closing deposit id
        // (= the saga's process_id). ce_autorenewalpolicy is the extension attribute PR A promotes from
        // the DepositMatured event's IntegrationHeaders.
        var headers = new Headers();
        Add(headers, "ce_specversion", "1.0");
        Add(headers, "ce_id", messageId.ToString());
        Add(headers, "ce_source", "urn:babelstone:engine:test");
        Add(headers, "ce_type", $"com.bank.deposits.{RenewalProcess.DepositMatured}");
        Add(headers, "ce_time", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Add(headers, "ce_datacontenttype", "application/avro");
        Add(headers, "ce_subject", closingDepositId.ToString());
        Add(headers, "ce_aggregatetype", topic);
        Add(headers, "ce_autorenewalpolicy", policy);

        var config = new ProducerConfig { BootstrapServers = _redpanda.BootstrapServers, EnableIdempotence = true, Acks = Acks.All };
        using var producer = new ProducerBuilder<byte[], byte[]>(config).Build();
        await producer.ProduceAsync(topic, new Message<byte[], byte[]>
        {
            Key = closingDepositId.ToByteArray(),
            Value = [0x00], // a non-null value: the loop reads headers only, never decodes this
            Headers = headers,
        });
        producer.Flush(TimeSpan.FromSeconds(10));
    }

    private static void Add(Headers headers, string key, string value)
        => headers.Add(key, Encoding.UTF8.GetBytes(value));

    // ---- Assertions ---------------------------------------------------------------------------

    private async Task<string?> StateOrNullAsync(Guid processId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT state FROM saga_state WHERE process_id = @p;", connection);
        command.Parameters.AddWithValue("p", processId);
        var raw = await command.ExecuteScalarAsync();
        return raw is string name ? name : null;
    }

    private async Task<string?> SagaTypeAsync(Guid processId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT saga_type FROM saga_state WHERE process_id = @p;", connection);
        command.Parameters.AddWithValue("p", processId);
        var raw = await command.ExecuteScalarAsync();
        return raw is string name ? name : null;
    }

    private async Task<int> CountInboxAsync(Guid messageId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM inbox WHERE message_id = @id;", connection);
        command.Parameters.AddWithValue("id", messageId);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string[]> OutboxCommandsAsync(Guid processId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT command_type FROM saga_outbox WHERE process_id = @p ORDER BY seq;", connection);
        command.Parameters.AddWithValue("p", processId);

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return [.. rows];
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

            await Task.Delay(250);
        }

        throw new TimeoutException($"Timed out after {timeout.TotalSeconds}s: {failureMessage}.");
    }
}
