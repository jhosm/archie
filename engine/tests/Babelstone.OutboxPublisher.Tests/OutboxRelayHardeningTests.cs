using System.Diagnostics.Metrics;
using System.Text;
using Babelstone.Engine;
using Babelstone.Engine.Avro;
using Babelstone.EventStore;
using Babelstone.EventStore.Migrations;
using Babelstone.Families.TermDeposit;
using Babelstone.FinancialTypes;
using Babelstone.Telemetry;
using Confluent.Kafka;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.OutboxPublisher.Tests;

/// <summary>
/// The G.1 relay-hardening lane: the SKIP-LOCKED HA-coordination guarantee (ADR-IC-004 §P2 /
/// §Residual-risks) and the publish-lag SLI (ADR-IC-004 §P4 / ADR-IC-007). Reuses the E.4 Redpanda
/// + PostgreSQL fixtures, the real Avro codec, and the durable runtime so the seeded outbox rows
/// carry real SR <c>schema_id</c>s — exactly the rows the relay drains in production.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OutboxRelayHardeningTests : IAsyncLifetime
{
    private const long PrincipalCents = 1_000_000;
    private const int TanBasisPoints = 300;

    // The canonical AT_MATURITY numbers (E.1), reused for the multi-event-per-aggregate seeding.
    private const long GrossCents = 30_417;
    private const long TaxCents = 8_517;
    private const long NetCents = 21_900;
    private const long PayoutCents = 1_021_900;

    private static readonly DateOnly StartDate = new(2026, 1, 1);
    private static readonly DateOnly MaturityDate = new(2026, 12, 31);

    private readonly RedpandaFixture _redpanda = new();
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_redpanda.InitializeAsync(), _pg.StartAsync());
        await new MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync()
    {
        await _pg.DisposeAsync();
        await _redpanda.DisposeAsync();
    }

    /// <summary>
    /// Two drainers race the same PENDING backlog. <c>FOR UPDATE SKIP LOCKED</c> (ADR-IC-004 §P2)
    /// must make them claim DISJOINT rows: every row is published exactly once (no double-delivery
    /// on Redpanda, no row flipped PUBLISHED twice) and neither drainer blocks on the other's locks
    /// (no deadlock — the whole run completes well within the test timeout).
    /// </summary>
    [Fact]
    public async Task Two_concurrent_drainers_publish_each_row_exactly_once_without_deadlock()
    {
        // --- Arrange: a backlog of distinct single-event streams (one outbox row each). One
        //     row per aggregate keeps the assertion crisp — disjoint claims, no per-aggregate
        //     ordering to reason about — and a few dozen rows guarantees real interleaving. ---
        const int rowCount = 40;
        var depositIds = await SeedPendingDepositsAsync(rowCount);
        Assert.Equal(rowCount, await CountByStatusAsync("PENDING"));

        // --- Act: two drainers, each its own DB connection + Redpanda producer, drain the same
        //     outbox concurrently until the backlog is empty. A small batch size forces several
        //     drain cycles so the two genuinely interleave (rather than one swallowing the whole
        //     backlog in a single 256-row pass) — the contention this test is about. ---
        await using var drainerA = new OutboxDrainer(RelayOptions(batchSize: 5));
        await using var drainerB = new OutboxDrainer(RelayOptions(batchSize: 5));

        var totalA = 0;
        var totalB = 0;
        async Task DrainToDrain(OutboxDrainer drainer, Action<int> tally)
        {
            int published;
            do
            {
                published = await drainer.DrainOnceAsync(CancellationToken.None);
                tally(published);
            }
            while (published > 0);
        }

        // A 30s budget: SKIP LOCKED never blocks, so two competing drainers finish promptly. A
        // hang here would be the regression this test exists to catch (a plain FOR UPDATE would
        // serialize, not deadlock — but the assertion below is the real guard).
        var drains = Task.WhenAll(
            DrainToDrain(drainerA, n => Interlocked.Add(ref totalA, n)),
            DrainToDrain(drainerB, n => Interlocked.Add(ref totalB, n)));
        var completed = await Task.WhenAny(drains, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(completed == drains, "Concurrent drainers did not finish within 30s — possible deadlock.");
        await drains; // surface any drain exception

        // --- Assert: the two drainers between them published every row exactly once. The counts
        //     PARTITION the backlog — totalA + totalB == rowCount means no row was claimed twice
        //     (a double-publish would push the sum above rowCount). This is the load-bearing,
        //     timing-independent invariant; the exact A/B split is a scheduling detail. ---
        Assert.Equal(rowCount, totalA + totalB);

        // --- Assert: the DB shows every row PUBLISHED, none left PENDING (no row lost). ---
        Assert.Equal(rowCount, await CountByStatusAsync("PUBLISHED"));
        Assert.Equal(0, await CountByStatusAsync("PENDING"));

        // --- Assert (the no-double-DELIVERY proof): consume the topic and confirm each deposit's
        //     event_id (ce_id) appears on Redpanda EXACTLY once — a double-publish would surface a
        //     duplicate ce_id even though the DB row only flips once. ---
        var ceIds = ConsumeCeIds(topic: "term_deposit", expected: rowCount);
        Assert.Equal(rowCount, ceIds.Count);
        Assert.Equal(rowCount, ceIds.Distinct().Count());
        // Every consumed record is one of the seeded deposits (a sanity cross-check on identity).
        var seededEventIds = await EventIdsAsync(depositIds);
        Assert.True(ceIds.ToHashSet().SetEquals(seededEventIds));
    }

    /// <summary>
    /// The §P2 hard constraint under concurrency (ADR-IC-004 §P2: "Publish order within an aggregate
    /// is a hard constraint ... the publisher must acquire a lock granularity that prevents concurrent
    /// in-flight rows for the same aggregate_id"). MULTIPLE events per aggregate (the maturity flow's
    /// three-event append shares one created_at, ordered only by sequence_number) drained by TWO
    /// concurrent drainers with a batch size SMALLER than an aggregate's event count, so an aggregate's
    /// stream straddles batch/cycle boundaries. The per-aggregate advisory lock must keep every
    /// aggregate on a single drainer at a time, so each aggregate's events ARRIVE on its Redpanda
    /// partition in sequence order. Without the advisory lock (plain SKIP LOCKED), drainer B could
    /// claim seq 2 while A holds seq 1 and publish it first — this test fails in that world.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_drainers_preserve_per_aggregate_order_across_multi_event_appends()
    {
        // --- Arrange: several aggregates, each FOUR events (Constituted + a 3-event append). ---
        const int aggregateCount = 8;
        const int eventsPerAggregate = 4;
        var rowCount = aggregateCount * eventsPerAggregate;
        var seeded = await SeedMultiEventDepositsAsync(aggregateCount);
        Assert.Equal(rowCount, await CountByStatusAsync("PENDING"));

        // --- Act: two drainers, batch size 2 (< 4 events/aggregate) so every aggregate's stream is
        //     split across at least two claims — the exact case SKIP-LOCKED-alone would reorder. ---
        await using var drainerA = new OutboxDrainer(RelayOptions(batchSize: 2));
        await using var drainerB = new OutboxDrainer(RelayOptions(batchSize: 2));

        async Task DrainToDrain(OutboxDrainer drainer)
        {
            int published;
            do
            {
                published = await drainer.DrainOnceAsync(CancellationToken.None);
            }
            while (published > 0);
        }

        var drains = Task.WhenAll(DrainToDrain(drainerA), DrainToDrain(drainerB));
        var completed = await Task.WhenAny(drains, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.True(completed == drains, "Concurrent drainers did not finish within 60s — possible deadlock.");
        await drains;

        Assert.Equal(rowCount, await CountByStatusAsync("PUBLISHED"));
        Assert.Equal(0, await CountByStatusAsync("PENDING"));

        // --- Assert: per aggregate, the ce_ids ARRIVED on the partition in per-stream sequence order.
        //     This is the §P2 hard constraint — the property a plain SKIP LOCKED two-drainer race
        //     would violate by interleaving seq 2 before seq 1 of the same aggregate. ---
        var arrivedByAggregate = ConsumeCeIdsByAggregate(topic: "term_deposit", expected: rowCount);
        Assert.Equal(aggregateCount, arrivedByAggregate.Count);
        foreach (var (aggregateId, sequencedEventIds) in seeded)
        {
            Assert.True(
                arrivedByAggregate.TryGetValue(aggregateId, out var arrived),
                $"No records arrived for aggregate {aggregateId}.");
            Assert.Equal(sequencedEventIds, arrived);
        }
    }

    /// <summary>
    /// The §P4 publish-lag SLI (ADR-IC-004 §P4): <see cref="OutboxLagObserver"/> emits
    /// <c>outbox_publish_lag_seconds</c> as the age of the OLDEST PENDING row, and crucially it
    /// reports that age even when NOTHING publishes — the failure mode (publisher down / Redpanda
    /// unavailable) §P4's Critical alert exists to catch, where a per-published-row metric is silent.
    /// Here no drainer runs at all, yet the gauge reads the seeded backlog's age &gt; 0; with an empty
    /// backlog it reads 0.
    /// </summary>
    [Fact]
    public async Task Lag_gauge_reports_oldest_pending_age_even_when_nothing_publishes()
    {
        var measurements = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BabelstoneTelemetry.MeterName &&
                instrument.Name == BabelstoneAttributes.OutboxPublishLagMetric)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) =>
        {
            lock (measurements)
            {
                measurements.Add(value);
            }
        });
        listener.Start();

        // The observer registers the gauge on the shared meter; the listener's RecordObservable-
        // Instruments call drives its callback (the OTel collection cycle's analogue).
        using var observer = new OutboxLagObserver(ConnectionString);

        // --- Empty backlog: the gauge reads 0 (no PENDING rows). ---
        measurements.Clear();
        listener.RecordObservableInstruments();
        Assert.NotEmpty(measurements);
        Assert.All(measurements, v => Assert.Equal(0, v));

        // --- Seed a backlog and let it age a beat; NOTHING publishes (no drainer runs). The gauge
        //     must still report the oldest PENDING row's age — climbing, not silent. ---
        await SeedPendingDepositsAsync(3);
        await Task.Delay(TimeSpan.FromSeconds(1));

        measurements.Clear();
        listener.RecordObservableInstruments();
        Assert.NotEmpty(measurements);
        var observed = measurements.Max();
        // The gauge tracks a real, non-trivial backlog age, close to what the same single-clock query
        // sees directly (a small tolerance for the time between the two reads).
        var expected = await OldestPendingAgeSecondsAsync();
        Assert.True(observed > 0, $"lag gauge should report the backlog age, was {observed}.");
        Assert.True(
            Math.Abs(observed - expected) < 2.0,
            $"lag gauge ({observed}s) should track the oldest-PENDING age ({expected}s).");
    }

    /// <summary>
    /// The per-row publish-latency histogram (a G.1 addition over ADR-IC-007): draining records the
    /// <c>outbox_publish_latency_seconds</c> histogram on the shared <see cref="BabelstoneTelemetry.Meter"/>,
    /// once per published row, carrying the <c>babelstone.aggregate_type</c> structural tag — and
    /// nothing PII-ish — per the operational-tier attribute discipline. This is NOT the §P4 SLI (that
    /// is the oldest-PENDING gauge exercised by <see cref="Lag_gauge_reports_oldest_pending_age_even_when_nothing_publishes"/>).
    /// </summary>
    [Fact]
    public async Task Draining_records_publish_latency_metric_with_babelstone_attributes()
    {
        // Tag-key fragments that would be a tier-2/tier-3 leak in the metrics backend (mirrors the
        // engine TelemetrySpanTests guard) — the SLI dimensions must stay operational-tier.
        string[] piiKeyFragments = ["nif", "iban", "account", "name", "email", "client", "phone", "address", "tax_id"];

        const int rowCount = 3;
        await SeedPendingDepositsAsync(rowCount);

        // A MeterListener on the shared meter is the metrics analogue of the ActivityListener in
        // the engine TelemetrySpanTests: it is exactly what a host's AddMeter(BabelstoneTelemetry
        // .MeterName) wires up. Capture every measurement of the publish-lag instrument.
        var measurements = new List<(double Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BabelstoneTelemetry.MeterName &&
                instrument.Name == BabelstoneAttributes.OutboxPublishLatencyMetric)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            lock (measurements)
            {
                measurements.Add((value, tags.ToArray()));
            }
        });
        listener.Start();

        await using var drainer = new OutboxDrainer(RelayOptions());
        var published = await drainer.DrainOnceAsync(CancellationToken.None);
        Assert.Equal(rowCount, published);

        listener.Dispose(); // flush: no measurement arrives after dispose

        // At least one measurement per row this drain published. ">=" not "==": the meter is
        // process-global, so a sibling Integration test class draining in parallel can record its
        // own publish-latency measurements onto the same instrument — counting them is cross-talk,
        // not a defect. The per-row, right-dimension, non-negative invariants below are what matter.
        // Latency is single-clock (computed in the DB), so >= 0 holds even under host/DB clock skew.
        Assert.True(
            measurements.Count >= rowCount,
            $"Expected at least {rowCount} publish-latency measurements, saw {measurements.Count}.");
        Assert.All(measurements, m => Assert.True(m.Value >= 0, $"latency must be non-negative, was {m.Value}"));

        foreach (var (_, tags) in measurements)
        {
            // The aggregate_type dimension is present and is the term-deposit topic.
            var aggregateType = Assert.Single(tags, t => t.Key == BabelstoneAttributes.AggregateType);
            Assert.Equal("term_deposit", aggregateType.Value);

            // Every dimension is a babelstone.* operational-tier key — none PII-ish (ADR-IC-007 P4).
            foreach (var tag in tags)
            {
                Assert.StartsWith("babelstone.", tag.Key);
                var lowered = tag.Key.ToLowerInvariant();
                Assert.DoesNotContain(piiKeyFragments, fragment => lowered.Contains(fragment));
            }
        }
    }

    // ---- Seeding -----------------------------------------------------------------------

    /// <summary>
    /// Appends <paramref name="count"/> single-event deposit streams through the real durable
    /// runtime + Avro codec, so each yields one PENDING outbox row with a genuine SR schema_id.
    /// Returns the deposit (stream/aggregate) ids.
    /// </summary>
    private async Task<List<Guid>> SeedPendingDepositsAsync(int count)
    {
        var runtime = BuildRuntime();
        var depositIds = new List<Guid>(count);
        for (var i = 0; i < count; i++)
        {
            var depositId = Guid.NewGuid();
            var constituted = new DepositConstituted(
                depositId, new Money(PrincipalCents), TanBasisPoints, "rs-2026-01",
                TermDays: 364, StartDate, MaturityDate, "AT_MATURITY", "NONE");
            await runtime.AppendAsync(depositId, expectedVersion: -1, [constituted], Ctx(), CancellationToken.None);
            depositIds.Add(depositId);
        }

        return depositIds;
    }

    /// <summary>
    /// Appends <paramref name="count"/> deposit streams, each with the full FOUR-event maturity flow
    /// (Constituted; then Accrued + WithholdingApplied + Matured in one multi-event append). The
    /// second append stamps all three rows with ONE transaction_time, so they share <c>created_at</c>
    /// and are ordered only by <c>sequence_number</c> — exactly the intra-append tie the §P2 drain
    /// must order. Returns each aggregate's event_ids in per-stream sequence order.
    /// </summary>
    private async Task<List<(Guid AggregateId, List<Guid> SequencedEventIds)>> SeedMultiEventDepositsAsync(int count)
    {
        var runtime = BuildRuntime();
        var seeded = new List<(Guid, List<Guid>)>(count);
        for (var i = 0; i < count; i++)
        {
            var depositId = Guid.NewGuid();
            var constituted = new DepositConstituted(
                depositId, new Money(PrincipalCents), TanBasisPoints, "rs-2026-01",
                TermDays: 364, StartDate, MaturityDate, "AT_MATURITY", "NONE");
            await runtime.AppendAsync(depositId, expectedVersion: -1, [constituted], Ctx(), CancellationToken.None);

            var accrued = new InterestAccrued(new Money(GrossCents), MaturityDate);
            var withheld = new WithholdingApplied(new Money(TaxCents), new Money(NetCents));
            var matured = new DepositMatured(new Money(PrincipalCents), new Money(NetCents), new Money(PayoutCents), MaturityDate);
            await runtime.AppendAsync(depositId, expectedVersion: 0, [accrued, withheld, matured], Ctx(), CancellationToken.None);

            seeded.Add((depositId, await EventIdsBySequenceAsync(depositId)));
        }

        return seeded;
    }

    private AggregateRuntime<DepositPosition> BuildRuntime()
    {
        var catalog = new AvroSchemaCatalog();
        // The resolver registers schemas against the test SR; it is not disposed here because the
        // returned runtime holds the serializer for the caller's whole append loop. The container
        // teardown reclaims the SR; the resolver owns only an HttpClient.
        var schemaIds = ConfluentSchemaIdResolver.Create(catalog, _redpanda.SchemaRegistryUrl, registerIfAbsent: true);
        var serializer = new AvroEventSerializer(catalog, schemaIds);

        var store = new PostgresEventStore(ConnectionString);
        return new AggregateRuntime<DepositPosition>(
            store,
            new EventStoreSink(store),
            TermDepositFamilyModule.Registry(),
            serializer,
            new NullPiiProtector(),
            TimeProvider.System,
            () => DepositPosition.Empty);
    }

    private static AppendContext Ctx() => new(
        Family: "term_deposit",
        PackVersion: "pt.2026.1",
        SchemaVersion: "term_deposit@2026.1",
        Actor: "test",
        ValidTime: DateTimeOffset.UtcNow);

    private OutboxRelayOptions RelayOptions(int? batchSize = null)
    {
        // Leave BatchSize at the record default (256) unless a test asks for a smaller one.
        var options = new OutboxRelayOptions
        {
            ConnectionString = ConnectionString,
            BootstrapServers = _redpanda.BootstrapServers,
            Source = "urn:babelstone:engine:test",
        };
        return batchSize is null ? options : options with { BatchSize = batchSize.Value };
    }

    // ---- Consume (identity only — the E.4 test already asserts payload/headers) --------

    private List<Guid> ConsumeCeIds(string topic, int expected)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _redpanda.BootstrapServers,
            GroupId = $"g1-test-{Guid.NewGuid()}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        };

        using var consumer = new ConsumerBuilder<byte[], byte[]>(config).Build();
        consumer.Subscribe(topic);

        var ceIds = new List<Guid>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        // Read a little past `expected` so a stray duplicate (the bug) would be observed, not hidden
        // by stopping the moment the count is reached.
        while (DateTime.UtcNow < deadline)
        {
            var result = consumer.Consume(TimeSpan.FromSeconds(3));
            if (result?.Message is null)
            {
                if (ceIds.Count >= expected)
                {
                    break; // backlog drained and a poll came back empty — done
                }

                continue;
            }

            ceIds.Add(Guid.Parse(Header(result, "ce_id")));
        }

        consumer.Close();
        return ceIds;
    }

    /// <summary>
    /// Consumes <paramref name="expected"/> records and returns, per aggregate (CloudEvents
    /// <c>ce_subject</c> = aggregate_id), the <c>ce_id</c>s in the order they ARRIVED on the topic.
    /// Records keyed by aggregate_id all land on one partition, so per-aggregate arrival order is the
    /// partition delivery order — what §P2 must keep equal to per-stream sequence order.
    /// </summary>
    private Dictionary<Guid, List<Guid>> ConsumeCeIdsByAggregate(string topic, int expected)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _redpanda.BootstrapServers,
            GroupId = $"g1-test-{Guid.NewGuid()}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        };

        using var consumer = new ConsumerBuilder<byte[], byte[]>(config).Build();
        consumer.Subscribe(topic);

        var byAggregate = new Dictionary<Guid, List<Guid>>();
        var total = 0;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var result = consumer.Consume(TimeSpan.FromSeconds(3));
            if (result?.Message is null)
            {
                if (total >= expected)
                {
                    break;
                }

                continue;
            }

            var subject = Guid.Parse(Header(result, "ce_subject"));
            var ceId = Guid.Parse(Header(result, "ce_id"));
            (byAggregate.TryGetValue(subject, out var list)
                ? list
                : byAggregate[subject] = new List<Guid>()).Add(ceId);
            total++;
        }

        consumer.Close();
        return byAggregate;
    }

    private static string Header(ConsumeResult<byte[], byte[]> r, string key)
        => r.Message.Headers.TryGetLastBytes(key, out var bytes) ? Encoding.UTF8.GetString(bytes) : string.Empty;

    // ---- Outbox status assertions ------------------------------------------------------

    private async Task<int> CountByStatusAsync(string status)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM outbox WHERE status = @status;", connection);
        command.Parameters.AddWithValue("status", status);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<HashSet<Guid>> EventIdsAsync(IReadOnlyList<Guid> aggregateIds)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT event_id FROM outbox WHERE aggregate_id = ANY(@ids);", connection);
        command.Parameters.AddWithValue("ids", aggregateIds.ToArray());

        var ids = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>One aggregate's outbox event_ids in per-stream <c>sequence_number</c> order —
    /// the order the §P2 drain must preserve on the aggregate's Redpanda partition.</summary>
    private async Task<List<Guid>> EventIdsBySequenceAsync(Guid aggregateId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT event_id FROM outbox WHERE aggregate_id = @id ORDER BY sequence_number;", connection);
        command.Parameters.AddWithValue("id", aggregateId);

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>Age in seconds of the oldest PENDING outbox row (the §P4 SLI's measurand),
    /// computed the same single-clock way the gauge does, for the gauge's direct assertion.</summary>
    private async Task<double> OldestPendingAgeSecondsAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COALESCE(EXTRACT(EPOCH FROM clock_timestamp() - MIN(created_at)), 0) FROM outbox WHERE status = 'PENDING';",
            connection);
        return Convert.ToDouble(await command.ExecuteScalarAsync());
    }
}
