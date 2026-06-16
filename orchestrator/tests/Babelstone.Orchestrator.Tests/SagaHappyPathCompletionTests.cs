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
/// The happy-path COMPLETION proof (bd babelstone-3klm). The constitution saga reaches its terminal
/// success state ON THE ENGINE'S REAL EVENT, not on the <c>ActivateDeposit</c> HTTP 2xx — the
/// ADR-PC-029 slot-2 contract. The engine appends a deposit and its outbox relay publishes the
/// catalogued <c>DepositConstituted</c> integration fact (ADR-IC-002): CloudEvents Binary-mode
/// headers (ADR-IC-015) with <c>ce_type = com.bank.deposits.DepositConstituted</c> and
/// <c>ce_subject = </c>the deposit stream id. The orchestrator's hosted consume loop
/// (<see cref="SagaConsumeLoop"/>) decodes those headers, keys the transition table on the
/// <c>ce_type</c>'s record name (<c>DepositConstituted</c>) and correlates to the saga by
/// <c>ce_subject → process_id</c>, and drives <c>(APPROVED, DepositConstituted) → COMPLETED</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is the slot-2 path, not the 2xx.</b> bd babelstone-t7o3.8 (PR #180) deliberately
/// REMOVED the <c>(ActivateDeposit, Applied 2xx) → ProcessConstituted</c> self-advance: ADR-PC-029
/// slot 2 says the saga advances on the engine's resulting <c>DepositConstituted</c> EVENT, consumed
/// off the bus, never on the activation 2xx. <see cref="ConstitutionResultEvents.ForOutcome"/> enforces
/// that exclusion in code. So COMPLETED must be reached by a REAL bus event flowing through the REAL
/// consume loop — which is exactly what these tests drive, against a real Redpanda + PostgreSQL.
/// </para>
/// <para>
/// <b>The event name is the engine's, not the saga's internal label.</b> The engine's catalogued
/// record is <c>DepositConstituted</c>; the consume loop's <see cref="SagaConsumeLoop.RecordName"/>
/// extracts exactly that from <c>com.bank.deposits.DepositConstituted</c>. The saga's transition table
/// must therefore key the <c>(APPROVED, …) → COMPLETED</c> row on the literal string
/// <c>"DepositConstituted"</c> — which is the VALUE of the <see cref="ConstitutionProcess.ProcessConstituted"/>
/// constant. Before bd babelstone-3klm that constant held <c>"ProcessConstituted"</c>, so a real
/// <c>DepositConstituted</c> bus event hit <see cref="AdvanceOutcome.NoTransition"/> (poison) and the
/// saga stranded at APPROVED forever.
/// </para>
/// <para>
/// <b>Extraction-ready (ADR-PC-019 §P2).</b> No engine-kernel reference; the engine's relay is modelled
/// by producing the SAME CloudEvents header subset the <c>OutboxDrainer</c> emits. A dedicated Postgres
/// container isolates this class's rows.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SagaHappyPathCompletionTests : IAsyncLifetime
{
    private const long ThresholdCents = 25_000_00;

    private readonly RedpandaFixture _redpanda = new();
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_redpanda.InitializeAsync(), _pg.GatedStartAsync());
        await new Migrations.MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync()
    {
        await _pg.DisposeAsync();
        await _redpanda.DisposeAsync();
    }

    /// <summary>
    /// THE HEADLINE (Fork A, bd babelstone-t7o3.11). A saga walked to APPROVED reaches COMPLETED when the
    /// engine's real <c>DepositConstituted</c> event arrives on the FAMILY INTEGRATION topic
    /// <c>term_deposit</c> — the topic the engine's <c>OutboxDrainer</c> actually publishes to
    /// (topic = <c>aggregate_type</c>) — and the hosted consume loop, NOW SUBSCRIBED TO THE FULL
    /// <see cref="SagaConsumeTopics.ConstitutionProcessTopics"/> set, drives it. This proves the
    /// engine→saga event path is closed: the orchestrator reads the family topic (Fork A) and correlates
    /// the integration fact to the saga by <c>ce_subject → process_id</c> (the saga POSTs
    /// <c>deposit_id = process_id</c>, so <c>aggregate_id == process_id == ce_subject</c>; bd babelstone-3k10).
    /// Driven by the REAL Confluent consumer, not a hand-fed <see cref="SagaInboxEvent"/>. Before this lane
    /// the loop subscribed ONLY to <c>deposits.process.events</c>, so a real engine event on
    /// <c>term_deposit</c> was never consumed and the saga stranded at APPROVED forever.
    /// </summary>
    [Fact]
    public async Task DepositConstituted_on_the_family_topic_correlated_via_ce_subject_advances_saga_to_COMPLETED()
    {
        // Fork A: the engine publishes to the family integration topic (term_deposit), and the saga now
        // subscribes to it. This is the topic the OutboxDrainer ACTUALLY produces to at runtime.
        var topic = SagaConsumeTopics.TermDepositIntegrationTopic;

        var processId = await StartSagaAndWalkToApprovedAsync();
        Assert.Equal(ConstitutionProcess.States.Approved, await StateOrNullAsync(processId));

        // Produce the engine's REAL terminal fact onto the FAMILY topic: ce_type's record name is
        // "DepositConstituted" and ce_subject is the deposit stream's aggregate_id — which equals the
        // saga's process_id because the saga POSTs deposit_id = process_id to the engine (bd 3k10).
        var messageId = Guid.NewGuid();
        await ProduceDepositConstitutedAsync(topic, messageId, ceSubject: processId);

        // The host subscribes to the FULL production topic set (both deposits.process.events AND
        // term_deposit), exactly as Program.cs wires it — so this exercises the real Fork-A subscription.
        using var host = BuildHost(SagaConsumeTopics.ConstitutionProcessTopics, groupId: $"orch-family-{Guid.NewGuid()}");
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () => await StateOrNullAsync(processId) == ConstitutionProcess.States.Completed,
                TimeSpan.FromSeconds(40),
                "the hosted loop did not advance the saga to COMPLETED on the DepositConstituted event off the family topic");
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.Equal(ConstitutionProcess.States.Completed, await StateOrNullAsync(processId));
        Assert.True(ConstitutionProcess.States.IsTerminal(await StateOrNullAsync(processId) ?? ConstitutionProcess.States.Started));
        Assert.Equal(1, await CountInboxAsync(messageId));
    }

    /// <summary>
    /// A saga walked to APPROVED reaches COMPLETED when a <c>DepositConstituted</c> event arrives on
    /// <c>deposits.process.events</c> and the hosted consume loop drives it — proving the slot-2 advance is
    /// on the EVENT (correlated by <c>ce_subject → process_id</c>), not the activation 2xx.
    /// Driven by the REAL Confluent consumer, not a hand-fed <see cref="SagaInboxEvent"/>.
    /// <para>
    /// NB: this validates the bus-resume MECHANISM on the process topic only; it is NOT the production
    /// engine→saga path. The engine actually relays <c>DepositConstituted</c> on the <c>term_deposit</c>
    /// FAMILY topic (Fork A; ADR-IC-003 A6 2026-06-15), so the authoritative coverage for the production
    /// path is the headline
    /// <see cref="DepositConstituted_on_the_family_topic_correlated_via_ce_subject_advances_saga_to_COMPLETED"/>.
    /// This second test guards the consume-loop/correlation mechanism independently of which topic carries it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task DepositConstituted_bus_event_correlated_via_ce_subject_advances_saga_to_COMPLETED()
    {
        var topic = SagaConsumeTopics.ConstitutionProcessTopic;

        // Edge-start the saga and walk it to APPROVED through the REAL advance handler (the consume
        // loop only ADVANCES; it never starts a saga, bd babelstone-t7o3.9). The engine 2xx is not
        // exercised here — this test isolates the bus-resume advance the slot-2 contract names.
        var processId = await StartSagaAndWalkToApprovedAsync();
        Assert.Equal(ConstitutionProcess.States.Approved, await StateOrNullAsync(processId));

        // Produce the engine's REAL terminal fact: ce_type's record name is "DepositConstituted" (the
        // engine's catalogued event), and ce_subject is the deposit stream id, which the slot-2
        // correlation pins to the saga's process_id. This is what the OutboxDrainer relays.
        var messageId = Guid.NewGuid();
        await ProduceDepositConstitutedAsync(topic, messageId, ceSubject: processId);

        using var host = BuildHost(topic, groupId: $"orch-complete-{Guid.NewGuid()}");
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () => await StateOrNullAsync(processId) == ConstitutionProcess.States.Completed,
                TimeSpan.FromSeconds(40),
                "the hosted loop did not advance the saga to COMPLETED on the DepositConstituted event");
        }
        finally
        {
            await host.StopAsync();
        }

        // The saga reached its terminal success state, driven entirely by the real bus event.
        Assert.Equal(ConstitutionProcess.States.Completed, await StateOrNullAsync(processId));
        Assert.True(ConstitutionProcess.States.IsTerminal(await StateOrNullAsync(processId) ?? ConstitutionProcess.States.Started));

        // Effectively-once: exactly one inbox dedup row for the event's ce_id.
        Assert.Equal(1, await CountInboxAsync(messageId));
    }

    /// <summary>
    /// A redelivered <c>DepositConstituted</c> (the same ce_id produced twice) is deduplicated by the
    /// consume loop's inbox INSERT — the saga moves to COMPLETED exactly once and stays there.
    /// </summary>
    [Fact]
    public async Task DepositConstituted_event_is_idempotent_on_redelivery()
    {
        var topic = SagaConsumeTopics.ConstitutionProcessTopic;
        var processId = await StartSagaAndWalkToApprovedAsync();

        var messageId = Guid.NewGuid();
        await ProduceDepositConstitutedAsync(topic, messageId, ceSubject: processId);
        await ProduceDepositConstitutedAsync(topic, messageId, ceSubject: processId);

        using var host = BuildHost(topic, groupId: $"orch-complete-{Guid.NewGuid()}");
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () => await StateOrNullAsync(processId) == ConstitutionProcess.States.Completed,
                TimeSpan.FromSeconds(40),
                "the hosted loop did not advance the saga to COMPLETED on the DepositConstituted event");

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

        Assert.Equal(1, await CountInboxAsync(messageId));
        Assert.Equal(ConstitutionProcess.States.Completed, await StateOrNullAsync(processId));
    }

    /// <summary>
    /// A second <c>DepositConstituted</c> (a fresh ce_id) for an ALREADY-COMPLETED saga is a graceful
    /// no-op: the handler dedup-rows it and returns <see cref="AdvanceOutcome.Terminal"/>, the loop
    /// commits past it, and the state stays COMPLETED. A terminal saga accepts no further transitions
    /// (ADR-IC-003 §"Terminal").
    /// </summary>
    [Fact]
    public async Task DepositConstituted_on_a_terminal_saga_is_a_graceful_noop()
    {
        var topic = SagaConsumeTopics.ConstitutionProcessTopic;
        var processId = await StartSagaAndWalkToApprovedAsync();

        // Drive to COMPLETED on the first event.
        var firstId = Guid.NewGuid();
        await ProduceDepositConstitutedAsync(topic, firstId, ceSubject: processId);

        using var host = BuildHost(topic, groupId: $"orch-complete-{Guid.NewGuid()}");
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () => await StateOrNullAsync(processId) == ConstitutionProcess.States.Completed,
                TimeSpan.FromSeconds(40),
                "the hosted loop did not advance the saga to COMPLETED");

            // A SECOND, distinct DepositConstituted for the now-terminal saga — a late/duplicate engine
            // relay. It must be a graceful no-op: dedup-rowed (its own ce_id) and the state unchanged.
            var secondId = Guid.NewGuid();
            await ProduceDepositConstitutedAsync(topic, secondId, ceSubject: processId);

            await WaitUntilAsync(
                async () => await CountInboxAsync(secondId) == 1,
                TimeSpan.FromSeconds(20),
                "the late terminal-saga event was not consumed and dedup-rowed");
        }
        finally
        {
            await host.StopAsync();
        }

        // Still COMPLETED — the late event moved nothing.
        Assert.Equal(ConstitutionProcess.States.Completed, await StateOrNullAsync(processId));
    }

    // ---- Host wiring (the SAME composition the production Program.cs uses) -------------------

    private IHost BuildHost(string topic, string groupId) => BuildHost([topic], groupId);

    private IHost BuildHost(IReadOnlyList<string> topics, string groupId)
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
            Topics = topics,
        });
        builder.Services.AddSingleton(sp => new SagaConsumeLoop(
            sp.GetRequiredService<SagaInboxConsumerOptions>(),
            sp.GetRequiredService<SagaAdvanceHandler>()));
        builder.Services.AddHostedService<SagaInboxConsumerService>();
        return builder.Build();
    }

    // ---- Walk to APPROVED (edge start + the prior happy-path advances) -----------------------

    // Start the saga through the REAL edge starter, then advance it to APPROVED via the REAL advance
    // handler — exactly the moves the dispatcher/bridge would drive, but without the HTTP legs so this
    // test isolates the FINAL bus-resume advance the slot-2 contract names. The amount is under the
    // threshold, so the join auto-approves (in-process self-emit) → APPROVED → DebitConfirmed leaves
    // the saga at APPROVED, armed for ActivateDeposit and awaiting the engine's DepositConstituted.
    private async Task<Guid> StartSagaAndWalkToApprovedAsync()
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
                AutoApprovalThresholdMinorUnits: ThresholdCents),
            correlationId: Guid.NewGuid());
        Assert.Equal(ConstitutionProcess.States.ParallelValidation, result.State);
        var processId = result.ProcessId;

        var handler = new SagaAdvanceHandler(machine, stateStore, transitionLog, sink);

        // Parallel-validation join (balance first), then the auto-approval self-emit crosses
        // VALIDATIONS_COMPLETE → APPROVED in-process, then DebitConfirmed re-arms APPROVED.
        await AdvanceAsync(handler, processId, ConstitutionProcess.BalanceReserved);
        await AdvanceAsync(handler, processId, ConstitutionProcess.LimitsValidated);
        Assert.Equal(ConstitutionProcess.States.Approved, await StateOrNullAsync(processId));
        await AdvanceAsync(handler, processId, ConstitutionProcess.DebitConfirmed);
        Assert.Equal(ConstitutionProcess.States.Approved, await StateOrNullAsync(processId));

        return processId;
    }

    private async Task AdvanceAsync(SagaAdvanceHandler handler, Guid processId, string eventType)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var outcome = await handler.AdvanceAsync(connection, tx,
            new SagaInboxEvent(Guid.NewGuid(), processId, eventType, "test.injected", CorrelationId: null));
        Assert.Equal(AdvanceOutcome.Advanced, outcome);
        await tx.CommitAsync();
    }

    // ---- Produce the engine's DepositConstituted (mirror the outbox relay's header subset) ---

    private async Task ProduceDepositConstitutedAsync(string topic, Guid messageId, Guid ceSubject)
    {
        // The orchestrator keys ONLY on the event TYPE NAME (the ce_type last segment), never the Avro
        // payload, so the consume loop reads CloudEvents headers alone. ce_type mirrors the engine
        // OutboxDrainer.ReverseDnsType output for a term_deposit.DepositConstituted row; ce_subject is
        // the deposit stream's aggregate id (= the saga process_id under the slot-2 correlation pin).
        var headers = new Headers();
        Add(headers, "ce_specversion", "1.0");
        Add(headers, "ce_id", messageId.ToString());
        Add(headers, "ce_source", "urn:babelstone:engine:test");
        Add(headers, "ce_type", "com.bank.deposits.DepositConstituted");
        Add(headers, "ce_time", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Add(headers, "ce_datacontenttype", "application/avro");
        Add(headers, "ce_subject", ceSubject.ToString());
        Add(headers, "ce_aggregatetype", "term_deposit");

        var config = new ProducerConfig { BootstrapServers = _redpanda.BootstrapServers, EnableIdempotence = true, Acks = Acks.All };
        using var producer = new ProducerBuilder<byte[], byte[]>(config).Build();
        await producer.ProduceAsync(topic, new Message<byte[], byte[]>
        {
            Key = ceSubject.ToByteArray(),
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
