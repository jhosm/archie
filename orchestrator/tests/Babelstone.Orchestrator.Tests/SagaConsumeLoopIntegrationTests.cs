using System.Globalization;
using System.Text;
using Babelstone.Orchestrator.Edge;
using Babelstone.Families.TermDeposit.Orchestration;
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
/// The end-to-end proof that the orchestrator now READS events off the bus and drives the saga —
/// not just that <see cref="SagaAdvanceHandler.AdvanceAsync"/> works in isolation (which
/// <see cref="SagaAdvanceIntegrationTests"/> already covers). A real <c>ConstitutionRequested</c>
/// event is produced onto Redpanda, framed with the SAME CloudEvents Binary-mode headers the engine's
/// outbox relay emits (ADR-IC-015), and the HOSTED <see cref="SagaInboxConsumerService"/> consume loop
/// — the one wired into <c>Program.cs</c> — subscribes, decodes the headers into the PII-free
/// <see cref="SagaInboxEvent"/>, and advances the saga to <see cref="ConstitutionProcess.States.ParallelValidation"/>
/// in one transaction whose offset commits only after the DB commit.
/// </summary>
/// <remarks>
/// These tests anchor to the falsifiable claim ADR-IC-003 §S2 names — "the orchestrator is a Redpanda
/// consumer like every other service … the saga resumes when the triggering event arrives from
/// Redpanda" — driven through the REAL Confluent consumer, not a hand-fed <see cref="SagaInboxEvent"/>.
/// ADR-IC-003 §"Verifiable commitments" records that the orchestrator carries no catalogue Test ID yet
/// (a deliberate, visible gap), so — per the lane's spec-first rule — no new catalogue row is invented;
/// the test anchors to that existing §S2 behaviour.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SagaConsumeLoopIntegrationTests : IAsyncLifetime
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

    /// <summary>
    /// The hosted consume loop reads a produced ADVANCE event and advances an EXISTING saga end-to-end:
    /// the saga is edge-started first (the sole start path, bd babelstone-t7o3.9), then a produced
    /// <c>BalanceReserved</c> drives it from PARALLEL_VALIDATION → AWAIT_LIMITS_VALIDATED, the inbox
    /// dedup row lands (effectively-once), and the two validation commands the EDGE emitted are present
    /// — all advanced by the REAL Confluent consumer, not by calling the handler directly.
    /// </summary>
    [Fact]
    public async Task The_hosted_loop_consumes_an_advance_event_and_advances_the_saga()
    {
        var topic = SagaConsumeTopics.ConstitutionProcessTopic;

        // Edge-start the saga (creates the saga_state row + references + the minted process id, and
        // emits the two parallel validation commands). The hosted loop's job is to ADVANCE it.
        var processId = await StartSagaAtEdgeAsync();
        Assert.Equal(ConstitutionProcess.States.ParallelValidation, await StateOrNullAsync(processId));

        // The constitution saga reacts to events on the engine's deposits-process topic. Produce an
        // ADVANCE event with the exact CloudEvents headers the outbox relay emits (ADR-IC-015).
        var messageId = Guid.NewGuid();
        await ProduceEventAsync(topic, messageId, processId, ConstitutionProcess.BalanceReserved);

        using var host = BuildHost(topic, groupId: $"orch-consume-{Guid.NewGuid()}");
        await host.StartAsync();
        try
        {
            // The loop is asynchronous (subscribe → assign → consume → advance), so poll until the
            // saga has been driven into AWAIT_LIMITS_VALIDATED or the deadline elapses.
            await WaitUntilAsync(
                async () => await StateOrNullAsync(processId) == ConstitutionProcess.States.AwaitLimitsValidated,
                TimeSpan.FromSeconds(40),
                "the hosted loop did not advance the saga to AWAIT_LIMITS_VALIDATED");
        }
        finally
        {
            await host.StopAsync();
        }

        // The saga advanced — driven entirely by the hosted consumer.
        Assert.Equal(ConstitutionProcess.States.AwaitLimitsValidated, await StateOrNullAsync(processId));

        // Effectively-once: exactly one inbox dedup row for this message_id.
        Assert.Equal(1, await CountInboxAsync(messageId));

        // The two parallel validation commands the EDGE emitted are in the durable outbox (the
        // BalanceReserved advance emits no commands) (ADR-IC-003 §P1).
        Assert.Equal(
            new[] { ConstitutionProcess.ReserveAccountBalance, ConstitutionProcess.ValidateProductLimits },
            await OutboxCommandsAsync(processId));
    }

    /// <summary>
    /// A duplicate physical delivery (the same ce_id produced twice) is deduplicated by the consume
    /// loop's inbox INSERT — exactly one dedup row, the saga moved once. This proves the loop owns the
    /// offset/transaction the dedup needs (Document 04 / ADR-IC-003 §P1), not just the handler.
    /// </summary>
    [Fact]
    public async Task A_duplicate_delivery_through_the_loop_is_effectively_once()
    {
        var topic = SagaConsumeTopics.ConstitutionProcessTopic;

        // Edge-start the saga, then produce the SAME advance ce_id twice — the at-least-once
        // redelivery the inbox absorbs (the loop ADVANCES; it never starts a saga, bd babelstone-t7o3.9).
        var processId = await StartSagaAtEdgeAsync();
        var messageId = Guid.NewGuid();
        await ProduceEventAsync(topic, messageId, processId, ConstitutionProcess.BalanceReserved);
        await ProduceEventAsync(topic, messageId, processId, ConstitutionProcess.BalanceReserved);

        using var host = BuildHost(topic, groupId: $"orch-consume-{Guid.NewGuid()}");
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () => await StateOrNullAsync(processId) == ConstitutionProcess.States.AwaitLimitsValidated,
                TimeSpan.FromSeconds(40),
                "the hosted loop did not advance the saga to AWAIT_LIMITS_VALIDATED");

            // Give the loop a beat to consume + dedup the second physical delivery too.
            await WaitUntilAsync(
                async () => await CountInboxAsync(messageId) == 1,
                TimeSpan.FromSeconds(10),
                "the duplicate delivery did not settle to exactly one inbox row");
        }
        finally
        {
            await host.StopAsync();
        }

        // Effectively-once: one dedup row, the saga moved once — the redelivery added nothing.
        Assert.Equal(1, await CountInboxAsync(messageId));
        Assert.Equal(ConstitutionProcess.States.AwaitLimitsValidated, await StateOrNullAsync(processId));
    }

    // ---- Host wiring (the SAME composition the production Program.cs uses) -------------------

    private IHost BuildHost(string topic, string groupId)
    {
        var builder = Host.CreateApplicationBuilder();
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
        builder.Services.AddSingleton(new SagaInboxConsumerOptions
        {
            ConnectionString = ConnectionString,
            BootstrapServers = _redpanda.BootstrapServers,
            GroupId = groupId,
            Topics = [topic],
        });
        builder.Services.AddSingleton(sp => new SagaConsumeLoop(
            sp.GetRequiredService<SagaInboxConsumerOptions>(),
            sp.GetRequiredService<SagaAdvanceHandler>()));
        builder.Services.AddHostedService<SagaInboxConsumerService>();
        return builder.Build();
    }

    // ---- Edge start (the sole start path, bd babelstone-t7o3.9) ------------------------------

    // Start the saga through the REAL edge starter so the hosted loop has an EXISTING saga to advance:
    // it creates the saga_state row, pins the references, drives STARTED → PARALLEL_VALIDATION, and
    // emits the two parallel commands — all atomic. Returns the minted internal process id.
    private async Task<Guid> StartSagaAtEdgeAsync()
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
                AmountMinorUnits: 100_00,
                SourceAccountRef: "acct-ref-001",
                InterestAccountRef: null,
                ClientType: ClientType.Existing,
                AutoApprovalThresholdMinorUnits: 1_000_00));

        Assert.Equal(ConstitutionProcess.States.ParallelValidation, result.State);
        return result.ProcessId;
    }

    // ---- Produce a CloudEvents-headed record (mirror the outbox relay's header subset) -------

    private async Task ProduceEventAsync(string topic, Guid messageId, Guid processId, string eventType)
    {
        // The orchestrator keys ONLY on the event TYPE NAME (the ce_type last segment), never on the
        // Avro payload, so the consume loop reads the CloudEvents headers alone. A minimal value is
        // produced (the loop never decodes it) so the record is not a tombstone.
        var ceType = $"com.bank.deposits.{eventType}";
        var headers = new Headers();
        Add(headers, "ce_specversion", "1.0");
        Add(headers, "ce_id", messageId.ToString());
        Add(headers, "ce_source", "urn:babelstone:engine:test");
        Add(headers, "ce_type", ceType);
        Add(headers, "ce_time", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Add(headers, "ce_datacontenttype", "application/avro");
        Add(headers, "ce_subject", processId.ToString());
        Add(headers, "ce_aggregatetype", topic);

        var config = new ProducerConfig { BootstrapServers = _redpanda.BootstrapServers, EnableIdempotence = true, Acks = Acks.All };
        using var producer = new ProducerBuilder<byte[], byte[]>(config).Build();
        await producer.ProduceAsync(topic, new Message<byte[], byte[]>
        {
            Key = processId.ToByteArray(),
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
        await using var command = new NpgsqlCommand(
            "SELECT state FROM saga_state WHERE process_id = @p;", connection);
        command.Parameters.AddWithValue("p", processId);
        var raw = await command.ExecuteScalarAsync();
        return raw is string name ? name : null;
    }

    private async Task<int> CountInboxAsync(Guid messageId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM inbox WHERE message_id = @id;", connection);
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
