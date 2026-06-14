using System.Globalization;
using System.Text;
using Babelstone.Engine;
using Babelstone.Engine.Avro;
using Babelstone.Families.TermDeposit;
using Babelstone.FinancialTypes;
using Babelstone.TestFixtures;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.InboxConsumer.Tests;

/// <summary>
/// The G.2 inbox-consumer round-trip — the consumer mirror of the E.4/G.1 outbox tests. Produce
/// real Confluent wire-format Avro records (framed + CloudEvents-headed exactly as the outbox relay
/// does, via the local <see cref="WireFormat"/> helper) onto Redpanda, run the <see cref="InboxPump"/>, and assert the
/// three behaviours the brief calls for: duplicate-delivery dedup, poison-message handling, and the
/// offset ⇄ transaction ordering (a throwing handler rolls back AND leaves the offset uncommitted so
/// the message is redelivered).
/// </summary>
[Trait("Category", "Integration")]
public sealed class InboxConsumerIntegrationTests : IAsyncLifetime
{
    private const long PrincipalCents = 1_000_000;
    private const int TanBasisPoints = 300;
    private static readonly DateOnly StartDate = new(2026, 1, 1);
    private static readonly DateOnly MaturityDate = new(2026, 12, 31);
    private const string Topic = "term_deposit";

    private readonly RedpandaFixture _redpanda = new();
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private AvroEventSerializer _serializer = null!;
    private ConfluentSchemaIdResolver _schemaIds = null!;
    private ConfluentSchemaByIdResolver _writerSchemas = null!;

    private string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_redpanda.InitializeAsync(), _pg.StartAsync());
        await new Babelstone.EventStore.Migrations.MigrationRunner(ConnectionString).ApplyAsync();

        // The same Avro codec the relay encodes with, registered against the test SR so the produced
        // records carry real schema_ids — exactly the wire format the consumer un-frames + decodes.
        var catalog = new AvroSchemaCatalog();
        _schemaIds = ConfluentSchemaIdResolver.Create(catalog, _redpanda.SchemaRegistryUrl, registerIfAbsent: true);
        _serializer = new AvroEventSerializer(catalog, _schemaIds);
        // The CONSUMER-side writer-schema-by-id resolver: the pump resolves the writer schema from the
        // embedded wire-format schema_id against the SAME test SR and decodes writer→reader (ADR-IC-002
        // §Consequences; runtime lookup §P3) — exercising the production decode path, not the writer == reader fallback.
        _writerSchemas = ConfluentSchemaByIdResolver.Create(_redpanda.SchemaRegistryUrl);
    }

    public async Task DisposeAsync()
    {
        _writerSchemas.Dispose();
        _schemaIds.Dispose();
        await _pg.DisposeAsync();
        await _redpanda.DisposeAsync();
    }

    // ---- The three behaviours --------------------------------------------------------------

    /// <summary>
    /// Duplicate-delivery dedup (Document 04 / ADR-IC-004 §Residual-risks "mandatory, not optional").
    /// The SAME record (same ce_id) is produced TWICE. The pump handles the first (Handled) and
    /// dedups the second (Duplicate) on the message_id PK: exactly one inbox row, the handler ran
    /// exactly once, and both offsets advanced (neither delivery wedges the partition).
    /// </summary>
    [Fact]
    public async Task Duplicate_delivery_is_deduplicated_on_message_id()
    {
        var depositId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var constituted = NewConstituted(depositId);

        // Produce the identical record twice — the at-least-once redelivery the inbox absorbs.
        await ProduceAsync(messageId, depositId, constituted);
        await ProduceAsync(messageId, depositId, constituted);

        var handler = new RecordingHandler();
        using var pump = NewPump(handler);

        var first = await PumpUntilNonIdleAsync(pump);
        var second = await PumpUntilNonIdleAsync(pump);

        Assert.Equal(InboxPump.PumpOutcome.Handled, first);
        Assert.Equal(InboxPump.PumpOutcome.Duplicate, second);

        // The handler ran exactly ONCE (the effect was not duplicated — real money safety).
        Assert.Single(handler.Handled);
        Assert.Equal(messageId, handler.Handled[0].MessageId);
        Assert.IsType<DepositConstituted>(handler.Handled[0].Event);

        // Exactly one dedup row landed.
        Assert.Equal(1, await CountInboxAsync(messageId));
    }

    /// <summary>
    /// Poison-message handling. A record whose ce_type names an event this consumer does not know is
    /// un-processable; the pump skips PAST it (offset committed) rather than wedging the partition,
    /// and writes NO inbox row. A well-formed record produced AFTER it is then handled normally —
    /// proving the poison record did not block the ones behind it. The poison sink saw it.
    /// </summary>
    [Fact]
    public async Task Poison_record_is_skipped_past_and_does_not_block_the_partition()
    {
        var depositId = Guid.NewGuid();
        var poisonId = Guid.NewGuid();
        var goodId = Guid.NewGuid();
        var constituted = NewConstituted(depositId);

        // A poison record: valid wire framing + headers, but a ce_type the consumer's resolver does
        // not register (an event from a context this consumer does not handle).
        await ProducePoisonUnknownTypeAsync(poisonId, depositId, constituted);
        // A good record behind it.
        await ProduceAsync(goodId, depositId, constituted);

        var handler = new RecordingHandler();
        var poison = new RecordingPoisonSink();
        using var pump = NewPump(handler, poison);

        var first = await PumpUntilNonIdleAsync(pump);
        var second = await PumpUntilNonIdleAsync(pump);

        Assert.Equal(InboxPump.PumpOutcome.Poison, first);
        Assert.Equal(InboxPump.PumpOutcome.Handled, second);

        // The poison record left no dedup row and the handler never saw it.
        Assert.Equal(0, await CountInboxAsync(poisonId));
        Assert.DoesNotContain(handler.Handled, m => m.MessageId == poisonId);
        // The good record behind it was handled (the poison did not block the partition).
        Assert.Contains(handler.Handled, m => m.MessageId == goodId);
        Assert.Equal(1, await CountInboxAsync(goodId));
        // The poison sink saw the bad record with a reason.
        Assert.Single(poison.Seen);
        Assert.Contains("no event type registered", poison.Seen[0].Reason);
    }

    /// <summary>
    /// Offset ⇄ transaction ordering. A handler that THROWS on its first call (a transient failure)
    /// must roll the transaction back — leaving NO inbox row — and the pump must NOT commit the
    /// offset, so the record is redelivered. On redelivery the handler succeeds, the inbox row lands,
    /// and the offset finally advances. This is the at-least-once + effectively-once contract: a
    /// transient failure is redelivered (never silently skipped), unlike a poison record.
    /// </summary>
    [Fact]
    public async Task Handler_exception_rolls_back_and_redelivers_then_commits_on_success()
    {
        var depositId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        await ProduceAsync(messageId, depositId, NewConstituted(depositId));

        var handler = new ThrowOnceHandler();
        using var pump = NewPump(handler);

        // First pump: the handler throws. The pump propagates AFTER rollback and BEFORE the offset
        // commit (the InboxConsumerService loop would catch+backoff; here we assert directly).
        await PumpUntilThrowsAsync(pump);
        // Rollback: no dedup row was written.
        Assert.Equal(0, await CountInboxAsync(messageId));

        // The offset was NOT committed, so the SAME record is redelivered. This time the handler
        // succeeds: the dedup row lands and the message is Handled.
        var outcome = await PumpUntilNonIdleAsync(pump);
        Assert.Equal(InboxPump.PumpOutcome.Handled, outcome);
        Assert.Equal(1, await CountInboxAsync(messageId));
        Assert.Equal(2, handler.Calls); // threw once, succeeded once
    }

    /// <summary>
    /// Finding #1 regression: a handler-side unique-violation on a DIFFERENT constraint (NOT inbox_pkey)
    /// must NOT be misclassified as an inbox duplicate. The dedup catch narrows on inbox_pkey only, so
    /// a foreign 23505 propagates as a transient failure — the pump seeks back, leaves the offset
    /// uncommitted, and the record is REDELIVERED (effectively-once preserved), instead of being rolled
    /// back, counted as a duplicate, and committed-past (which would silently drop the message's effect).
    /// On redelivery the collision is gone and the message is handled exactly once.
    /// </summary>
    [Fact]
    public async Task Handler_unique_violation_on_a_foreign_constraint_redelivers_not_dedups()
    {
        // A handler-owned table with its own unique constraint (a stand-in for a saga-state PK or a
        // local-outbox event_id — the rows the IInboxMessageHandler contract invites a handler to write).
        await ExecuteAsync("""
            CREATE TABLE saga_state (
                saga_key UUID NOT NULL,
                CONSTRAINT saga_state_pkey PRIMARY KEY (saga_key)
            );
            """);

        var depositId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        await ProduceAsync(messageId, depositId, NewConstituted(depositId));

        // The handler INSERTs into saga_state with a key that collides on its FIRST call (a row was
        // pre-seeded), then with a fresh key on later calls. The first INSERT raises a non-inbox 23505.
        var preSeeded = Guid.NewGuid();
        await ExecuteAsync("INSERT INTO saga_state (saga_key) VALUES (@k);", ("k", preSeeded));
        var handler = new ForeignUniqueViolationOnceHandler(preSeeded);
        using var pump = NewPump(handler);

        // First pump: the handler's saga_state INSERT collides on saga_state_pkey (NOT inbox_pkey). The
        // foreign 23505 must propagate — the pump seeks back and rethrows, NOT swallow it as a duplicate.
        await PumpUntilThrowsAsync(pump);
        // Rollback: NO inbox row was written (the message was not silently consumed).
        Assert.Equal(0, await CountInboxAsync(messageId));

        // The offset was NOT committed, so the SAME record is redelivered. This time the handler uses a
        // fresh saga key, the effect lands, and the message is Handled exactly once.
        var outcome = await PumpUntilNonIdleAsync(pump);
        Assert.Equal(InboxPump.PumpOutcome.Handled, outcome);
        Assert.Equal(1, await CountInboxAsync(messageId));
        Assert.Equal(2, handler.Calls); // collided once, succeeded once
    }

    /// <summary>
    /// Finding #3 regression: a null-payload compaction tombstone (the GDPR right-to-erasure signal,
    /// ADR-IC-002 §P4) must be recognised BEFORE the Avro decode and skipped-and-committed as a
    /// Tombstone — NOT routed to the poison/dead-letter path. The poison sink must never see it (no
    /// false dead-letter alert), no inbox row is written, and a good record behind it is handled
    /// normally (the tombstone did not wedge the partition).
    /// </summary>
    [Fact]
    public async Task Null_payload_tombstone_is_skipped_as_tombstone_not_poison()
    {
        var depositId = Guid.NewGuid();
        var goodId = Guid.NewGuid();

        // A tombstone: a keyed record with a NULL value (no Avro, no framing). Then a good record behind it.
        await ProduceTombstoneAsync(depositId);
        await ProduceAsync(goodId, depositId, NewConstituted(depositId));

        var handler = new RecordingHandler();
        var poison = new RecordingPoisonSink();
        using var pump = NewPump(handler, poison);

        var first = await PumpUntilNonIdleAsync(pump);
        var second = await PumpUntilNonIdleAsync(pump);

        // The tombstone is a Tombstone outcome — NOT Poison.
        Assert.Equal(InboxPump.PumpOutcome.Tombstone, first);
        Assert.Equal(InboxPump.PumpOutcome.Handled, second);

        // The poison sink NEVER saw the tombstone (no false dead-letter alert), and it left no inbox row.
        Assert.Empty(poison.Seen);
        // The good record behind it was handled (the tombstone did not block the partition).
        Assert.Contains(handler.Handled, m => m.MessageId == goodId);
        Assert.Equal(1, await CountInboxAsync(goodId));
    }

    /// <summary>
    /// ic10 — two-consumer REBALANCE race (the consumer mirror of the outbox lane's two-drainer race).
    /// TWO <see cref="InboxPump"/> instances share ONE consumer group on a MULTI-partition topic. Pump A
    /// starts alone, processes some records and commits, then pump B joins MID-STREAM — forcing a Kafka
    /// partition rebalance that redelivers any records A consumed-but-not-yet-committed when its
    /// partitions were revoked. The inbox <c>message_id</c> PK absorbs every such redelivery: each
    /// record lands EXACTLY ONE inbox row (effectively-once), no record is lost, and the handler effect
    /// for each message runs exactly once across BOTH pumps. This is the at-least-once delivery +
    /// effectively-once effect contract (Document 04 / ADR-IC-004 §Residual-risks) under a rebalance —
    /// the disruption a single-consumer test never exercises.
    /// </summary>
    [Fact]
    public async Task Two_consumers_in_one_group_are_effectively_once_across_a_rebalance()
    {
        // --- Arrange: a MULTI-partition topic so two consumers in one group split partitions (a
        //     single-partition topic would pin both to one consumer — no real rebalance). Distinct
        //     aggregate keys spread the records across partitions. ---
        const int partitions = 4;
        const int recordCount = 24;
        await CreateTopicAsync(Topic, partitions);

        var produced = new List<Guid>(recordCount); // the message_ids (ce_id) we expect, exactly once each
        for (var i = 0; i < recordCount; i++)
        {
            var depositId = Guid.NewGuid();
            var messageId = Guid.NewGuid();
            await ProduceAsync(messageId, depositId, NewConstituted(depositId));
            produced.Add(messageId);
        }

        var groupId = $"g2-rebalance-{Guid.NewGuid()}";
        var handlerA = new CountingHandler();
        var handlerB = new CountingHandler();
        using var pumpA = NewPumpInGroup(groupId, handlerA);

        // --- Act: pump A drains alone for a bit (it joins, gets ALL partitions, processes + commits
        //     a few records). Then pump B joins the SAME group — the broker revokes some partitions
        //     from A and assigns them to B (the rebalance). Both then drain concurrently to completion.
        //     A's in-flight, uncommitted records on the revoked partitions are redelivered to B; the
        //     inbox PK dedups them. ---

        // A alone: pump until it has handled a handful (so a rebalance genuinely interrupts mid-stream).
        await PumpUntilHandledAtLeastAsync(pumpA, atLeast: 4, budget: TimeSpan.FromSeconds(30));

        using var pumpB = NewPumpInGroup(groupId, handlerB);

        // Drain both concurrently until the whole backlog is in the inbox (count == recordCount) or a
        // generous deadline. Each pump loops PumpOnce; Idle just means "no record this poll".
        async Task DrainToBacklogEmpty(InboxPump pump)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                if (await CountInboxTotalAsync() >= recordCount)
                {
                    return;
                }

                await pump.PumpOnceAsync(CancellationToken.None);
            }
        }

        var drains = Task.WhenAll(DrainToBacklogEmpty(pumpA), DrainToBacklogEmpty(pumpB));
        var completed = await Task.WhenAny(drains, Task.Delay(TimeSpan.FromSeconds(70)));
        Assert.True(completed == drains, "Two consumers did not drain the backlog within the deadline.");
        await drains; // surface any pump exception

        // --- Assert (the effectively-once proof): EXACTLY one inbox row per produced message_id, and
        //     the full produced set is present — neither lost nor duplicated by the rebalance. The
        //     message_id PK makes a redelivered record collide rather than double-apply. ---
        Assert.Equal(recordCount, await CountInboxTotalAsync());
        foreach (var messageId in produced)
        {
            Assert.Equal(1, await CountInboxAsync(messageId));
        }

        // --- Assert: the HANDLER effect ran exactly once per record across BOTH pumps. handlerA +
        //     handlerB between them handled each message_id exactly once (a redelivery is a Duplicate,
        //     which does NOT invoke the handler — the IF EXISTS short-circuit / PK backstop). No
        //     message_id was handled by both pumps. ---
        var handledByA = handlerA.HandledMessageIds;
        var handledByB = handlerB.HandledMessageIds;
        Assert.Empty(handledByA.Intersect(handledByB)); // no double-handling across the rebalance
        var handledTotal = handledByA.Concat(handledByB).ToHashSet();
        Assert.Equal(recordCount, handledTotal.Count);
        Assert.True(handledTotal.SetEquals(produced), "The handled set must equal the produced set exactly.");
    }

    // ---- Produce (mirror the relay's framing + CloudEvents headers) ------------------------

    private async Task ProduceAsync(Guid messageId, Guid aggregateId, DomainEvent @event)
    {
        var encoded = _serializer.Encode(@event);
        var value = WireFormat.Frame(encoded.SchemaId, encoded.Bytes);
        var ceType = WireFormat.ReverseDnsType($"{Topic}.{@event.GetType().Name}");
        await ProduceRawAsync(messageId, aggregateId, ceType, value);
    }

    /// <summary>A poison record: well-framed Avro + valid headers, but a ce_type for an event the
    /// consumer's resolver does not register — un-processable, the poison path.</summary>
    private async Task ProducePoisonUnknownTypeAsync(Guid messageId, Guid aggregateId, DomainEvent realEvent)
    {
        var encoded = _serializer.Encode(realEvent);
        var value = WireFormat.Frame(encoded.SchemaId, encoded.Bytes);
        // A ce_type whose record name ("UnknownToThisConsumer") is not registered.
        await ProduceRawAsync(messageId, aggregateId, "com.bank.other.UnknownToThisConsumer", value);
    }

    /// <summary>A compaction tombstone: a keyed record with a NULL value (the GDPR erasure signal,
    /// ADR-IC-002 §P4). It carries headers like any record but no Avro payload at all.</summary>
    private async Task ProduceTombstoneAsync(Guid aggregateId)
    {
        var headers = new Headers();
        Add(headers, "ce_specversion", "1.0");
        Add(headers, "ce_id", Guid.NewGuid().ToString());
        Add(headers, "ce_subject", aggregateId.ToString());
        Add(headers, "ce_aggregatetype", Topic);

        var config = new ProducerConfig { BootstrapServers = _redpanda.BootstrapServers, EnableIdempotence = true, Acks = Acks.All };
        using var producer = new ProducerBuilder<byte[], byte[]>(config).Build();
        await producer.ProduceAsync(Topic, new Message<byte[], byte[]>
        {
            Key = aggregateId.ToByteArray(),
            Value = null!, // the tombstone: a null value on a compacted topic
            Headers = headers,
        });
        producer.Flush(TimeSpan.FromSeconds(10));
    }

    private async Task ProduceRawAsync(Guid messageId, Guid aggregateId, string ceType, byte[] value)
    {
        // CloudEvents Binary-mode headers (ADR-IC-015), the exact subset OutboxDrainer.BuildHeaders
        // emits that the consumer reads: ce_id (the dedup key), ce_type, ce_subject.
        var headers = new Headers();
        Add(headers, "ce_specversion", "1.0");
        Add(headers, "ce_id", messageId.ToString());
        Add(headers, "ce_source", "urn:babelstone:engine:test");
        Add(headers, "ce_type", ceType);
        Add(headers, "ce_time", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Add(headers, "ce_datacontenttype", "application/avro");
        Add(headers, "ce_subject", aggregateId.ToString());
        Add(headers, "ce_aggregatetype", Topic);

        var config = new ProducerConfig { BootstrapServers = _redpanda.BootstrapServers, EnableIdempotence = true, Acks = Acks.All };
        using var producer = new ProducerBuilder<byte[], byte[]>(config).Build();
        await producer.ProduceAsync(Topic, new Message<byte[], byte[]>
        {
            Key = aggregateId.ToByteArray(),
            Value = value,
            Headers = headers,
        });
        producer.Flush(TimeSpan.FromSeconds(10));
    }

    private static void Add(Headers headers, string key, string value)
        => headers.Add(key, Encoding.UTF8.GetBytes(value));

    /// <summary>Create a topic with a fixed partition count up front (the rebalance race needs MULTIPLE
    /// partitions so two consumers in one group split them). Idempotent: a "topic already exists" error
    /// is benign (a sibling produce may have auto-created a single-partition one — but this test produces
    /// only AFTER creating, so the explicit count wins).</summary>
    private async Task CreateTopicAsync(string topic, int partitions)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _redpanda.BootstrapServers }).Build();
        try
        {
            await admin.CreateTopicsAsync(
                [new TopicSpecification { Name = topic, NumPartitions = partitions, ReplicationFactor = 1 }]);
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            // Already created (e.g. by a prior run within the same container) — fine.
        }
    }

    private static DepositConstituted NewConstituted(Guid depositId) => new(
        depositId, new Money(PrincipalCents), TanBasisPoints, "rs-2026-01",
        TermDays: 364, StartDate, MaturityDate, "AT_MATURITY", "NONE");

    // ---- Pump wiring -----------------------------------------------------------------------

    private InboxPump NewPump(IInboxMessageHandler handler, IInboxPoisonSink? poisonSink = null)
    {
        var options = new InboxConsumerOptions
        {
            ConnectionString = ConnectionString,
            BootstrapServers = _redpanda.BootstrapServers,
            // A fresh group per pump so each test reads its own records from the start, regardless of
            // what a sibling test committed (Earliest + a unique group = a clean replay of this topic).
            GroupId = $"g2-inbox-test-{Guid.NewGuid()}",
            Topics = [Topic],
        };
        // Only the catalogued term-deposit events this consumer knows are registered — an unknown
        // ce_type is poison. After the ADR-IC-017 §P4 promotion pass the bus set is DepositConstituted,
        // InterestPaid, DepositMatured (the de-promoted accrual mechanics never reach the bus).
        var resolver = InboxEventTypeResolver.FromTypes(
            typeof(DepositConstituted), typeof(InterestPaid), typeof(DepositMatured));
        // The writer-schema resolver makes the pump take the production SR-resolution decode path.
        return new InboxPump(options, _serializer, resolver, handler, poisonSink, writerSchemas: _writerSchemas);
    }

    /// <summary>Build a pump bound to a SPECIFIC consumer group id (the two-consumer rebalance race
    /// puts two pumps in ONE group). Mirrors <see cref="NewPump"/> otherwise.</summary>
    private InboxPump NewPumpInGroup(string groupId, IInboxMessageHandler handler)
    {
        var options = new InboxConsumerOptions
        {
            ConnectionString = ConnectionString,
            BootstrapServers = _redpanda.BootstrapServers,
            GroupId = groupId,
            Topics = [Topic],
        };
        var resolver = InboxEventTypeResolver.FromTypes(
            typeof(DepositConstituted), typeof(InterestPaid), typeof(DepositMatured));
        return new InboxPump(options, _serializer, resolver, handler, poisonSink: null, writerSchemas: _writerSchemas);
    }

    /// <summary>Pump until a record is actually processed (the first poll after a subscribe often
    /// returns Idle while the group joins + the partition is assigned).</summary>
    private static async Task<InboxPump.PumpOutcome> PumpUntilNonIdleAsync(InboxPump pump)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var outcome = await pump.PumpOnceAsync(CancellationToken.None);
            if (outcome != InboxPump.PumpOutcome.Idle)
            {
                return outcome;
            }
        }

        throw new TimeoutException("Pump stayed idle for 30s — no record was delivered.");
    }

    /// <summary>
    /// Pump until the handler raises — absorbing the cold-consumer idle polls that precede partition
    /// assignment (the first <see cref="InboxPump.PumpOnceAsync"/> on a freshly-subscribed consumer
    /// often returns <see cref="InboxPump.PumpOutcome.Idle"/> before the produced record is delivered).
    /// Returns the exception the handler propagated. A record that is processed without throwing, or
    /// never delivered within the deadline, is a genuine failure — NOT swallowed as success.
    /// </summary>
    private static async Task<Exception> PumpUntilThrowsAsync(InboxPump pump)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var outcome = await pump.PumpOnceAsync(CancellationToken.None);
                if (outcome != InboxPump.PumpOutcome.Idle)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"Expected the handler to throw, but the pump returned {outcome}.");
                }
                // Idle: consumer not yet assigned a partition — keep polling.
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                return ex; // the handler-propagated exception we were waiting for
            }
        }

        throw new TimeoutException("Pump stayed idle for 30s — no record was delivered.");
    }

    /// <summary>Pump A alone until it has HANDLED at least <paramref name="atLeast"/> first-time records,
    /// so the subsequent join of pump B genuinely interrupts mid-stream (the rebalance race). Idle polls
    /// (cold consumer joining) and Duplicate/Poison outcomes are absorbed; only Handled counts.</summary>
    private static async Task PumpUntilHandledAtLeastAsync(InboxPump pump, int atLeast, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow.Add(budget);
        var handled = 0;
        while (DateTime.UtcNow < deadline)
        {
            var outcome = await pump.PumpOnceAsync(CancellationToken.None);
            if (outcome == InboxPump.PumpOutcome.Handled && ++handled >= atLeast)
            {
                return;
            }
        }

        throw new TimeoutException($"Pump A did not handle at least {atLeast} records within {budget.TotalSeconds}s.");
    }

    // ---- Inbox assertions ------------------------------------------------------------------

    private async Task<int> CountInboxAsync(Guid messageId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM inbox WHERE message_id = @id;", connection);
        command.Parameters.AddWithValue("id", messageId);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Total inbox rows — the rebalance race's completion gate and effectively-once measurand
    /// (it must equal the produced count: one row per message, none lost, none duplicated).</summary>
    private async Task<int> CountInboxTotalAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM inbox;", connection);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Run a one-off statement against the consumer DB (test fixture setup — a handler-owned
    /// table + seed row for the foreign-unique-violation regression).</summary>
    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    // ---- Test handlers ---------------------------------------------------------------------

    /// <summary>Records every message it handles — pure, no clock/IO beyond the supplied transaction.</summary>
    private sealed class RecordingHandler : IInboxMessageHandler
    {
        public List<InboxMessage> Handled { get; } = [];

        public Task<string?> HandleAsync(
            InboxMessage message, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct = default)
        {
            Handled.Add(message);
            return Task.FromResult<string?>($"handled:{message.EventType}");
        }
    }

    /// <summary>Captures the message_ids it handled (the rebalance race asserts each is handled exactly
    /// once across BOTH pumps). One instance per pump; the lock guards the post-drain read. Pure — no
    /// clock/IO beyond the supplied transaction (handler-purity discipline).</summary>
    private sealed class CountingHandler : IInboxMessageHandler
    {
        private readonly object _gate = new();
        private readonly HashSet<Guid> _handled = [];

        public HashSet<Guid> HandledMessageIds
        {
            get { lock (_gate) { return [.. _handled]; } }
        }

        public Task<string?> HandleAsync(
            InboxMessage message, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _handled.Add(message.MessageId);
            }

            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>Throws on the first call, succeeds afterwards — the transient-failure case the
    /// offset/transaction ordering must redeliver.</summary>
    private sealed class ThrowOnceHandler : IInboxMessageHandler
    {
        public int Calls { get; private set; }

        public Task<string?> HandleAsync(
            InboxMessage message, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct = default)
        {
            Calls++;
            if (Calls == 1)
            {
                throw new InvalidOperationException("transient handler failure (test)");
            }

            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>On its FIRST call inserts a saga_state row whose key collides on saga_state_pkey
    /// (a non-inbox unique violation — the foreign 23505 the dedup catch must NOT swallow); on later
    /// calls inserts a fresh key so the redelivery succeeds. Stands in for a real saga/local-outbox
    /// handler the IInboxMessageHandler contract invites.</summary>
    private sealed class ForeignUniqueViolationOnceHandler(Guid collidingKey) : IInboxMessageHandler
    {
        public int Calls { get; private set; }

        public async Task<string?> HandleAsync(
            InboxMessage message, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct = default)
        {
            Calls++;
            var key = Calls == 1 ? collidingKey : Guid.NewGuid();
            await using var command = new NpgsqlCommand(
                "INSERT INTO saga_state (saga_key) VALUES (@k);", connection, transaction);
            command.Parameters.AddWithValue("k", key);
            await command.ExecuteNonQueryAsync(ct); // first call raises saga_state_pkey unique-violation
            return null;
        }
    }

    private sealed class RecordingPoisonSink : IInboxPoisonSink
    {
        public List<(ConsumeResult<byte[], byte[]> Result, string Reason)> Seen { get; } = [];

        public Task OnPoisonAsync(ConsumeResult<byte[], byte[]> result, string reason, CancellationToken ct = default)
        {
            Seen.Add((result, reason));
            return Task.CompletedTask;
        }
    }
}
