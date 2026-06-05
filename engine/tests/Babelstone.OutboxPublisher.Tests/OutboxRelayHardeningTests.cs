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
    /// The publish-lag SLI (ADR-IC-004 §P4 / ADR-IC-007): draining records the
    /// <c>outbox_publish_lag_seconds</c> histogram on the shared <see cref="BabelstoneTelemetry.Meter"/>,
    /// once per published row, carrying the <c>babelstone.aggregate_type</c> structural tag — and
    /// nothing PII-ish — per the operational-tier attribute discipline.
    /// </summary>
    [Fact]
    public async Task Draining_records_publish_lag_metric_with_babelstone_attributes()
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
                instrument.Name == BabelstoneAttributes.OutboxPublishLagMetric)
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
        // own publish-lag measurements onto the same instrument — counting them is cross-talk, not
        // a defect. The per-row, right-dimension, non-negative invariants below are what matter.
        Assert.True(
            measurements.Count >= rowCount,
            $"Expected at least {rowCount} publish-lag measurements, saw {measurements.Count}.");
        Assert.All(measurements, m => Assert.True(m.Value >= 0, $"lag must be non-negative, was {m.Value}"));

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
        var catalog = new AvroSchemaCatalog();
        using var schemaIds = ConfluentSchemaIdResolver.Create(catalog, _redpanda.SchemaRegistryUrl, registerIfAbsent: true);
        var serializer = new AvroEventSerializer(catalog, schemaIds);

        var store = new PostgresEventStore(ConnectionString);
        var runtime = new AggregateRuntime<DepositPosition>(
            store,
            new EventStoreSink(store),
            TermDepositFamilyModule.Registry(),
            serializer,
            new NullPiiProtector(),
            TimeProvider.System,
            () => DepositPosition.Empty);

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
}
