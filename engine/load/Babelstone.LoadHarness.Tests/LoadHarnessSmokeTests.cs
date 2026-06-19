using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Avro;
using Avro.Generic;
using Avro.IO;
using Babelstone.Engine.Avro;
using Babelstone.Families.TermDeposit;
using Babelstone.Telemetry;
using Confluent.Kafka;
using Xunit;

namespace Babelstone.LoadHarness.Tests;

/// <summary>
/// Docker-free smoke / self-test for the L.1 load harness (ADR-PC-011). It pins the harness's own
/// falsifiable properties (the §192 "known gap" the ADR flags for the catalogue):
/// <list type="bullet">
///   <item>§G3/§8.5 determinism — the SAME seed reproduces the SAME event stream (and a DIFFERENT seed differs).</item>
///   <item>§G1 production bytes — the driver's wire-format value decodes against the engine's OWN Avro schema.</item>
///   <item>§8.2 shape — only harness-emitted classes are produced; the mix is drawn from them.</item>
///   <item>§P2/§G2 — the observer reads latency from the engine's OTel SPAN duration, not a send clock.</item>
///   <item>§P3/§G3 — the simulated clock advances domain time monotonically and is read as engine "now".</item>
/// </list>
/// All in-process: no Redpanda, no Schema Registry (a stub resolver feeds schema ids), no PostgreSQL.
/// </summary>
public sealed class LoadHarnessSmokeTests
{
    // A schema-id resolver that needs no Schema Registry — the smoke test only round-trips bytes
    // locally, so any stable id works. The engine's AvroEventSerializer takes ISchemaIdResolver.
    private sealed class StubSchemaIdResolver : ISchemaIdResolver
    {
        public int ResolveSchemaId(string eventType) => 42;
    }

    private static (AvroEventSerializer Serializer, AvroSchemaCatalog Catalog) BuildCodec()
    {
        var catalog = new AvroSchemaCatalog();
        return (new AvroEventSerializer(catalog, new StubSchemaIdResolver()), catalog);
    }

    [Fact]
    public void Same_seed_reproduces_the_same_event_stream()
    {
        var spec = WorkloadSpec.Default();
        var calibration = Calibration.V4Placeholder();
        var window = (Start: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), Length: TimeSpan.FromDays(1));
        var peakDay = new DateOnly(2026, 11, 27);

        var a = new WorkloadGenerator(seed: 1234, spec, calibration)
            .Generate(500, window.Start, window.Length, peakDay).ToList();
        var b = new WorkloadGenerator(seed: 1234, spec, calibration)
            .Generate(500, window.Start, window.Length, peakDay).ToList();

        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            // Reproducible to the last byte: same partition key, same class, same emit instant, same event.
            Assert.Equal(a[i].PartitionKey, b[i].PartitionKey);
            Assert.Equal(a[i].MixClass, b[i].MixClass);
            Assert.Equal(a[i].EmitInstant, b[i].EmitInstant);
            Assert.Equal(a[i].Event, b[i].Event);
        }
    }

    [Fact]
    public void Different_seed_yields_a_different_stream()
    {
        var spec = WorkloadSpec.Default();
        var calibration = Calibration.V4Placeholder();
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var peakDay = new DateOnly(2026, 11, 27);

        var a = new WorkloadGenerator(seed: 1, spec, calibration)
            .Generate(200, start, TimeSpan.FromDays(1), peakDay).ToList();
        var b = new WorkloadGenerator(seed: 2, spec, calibration)
            .Generate(200, start, TimeSpan.FromDays(1), peakDay).ToList();

        // Not equal sequences (the seed is the only difference and it must matter).
        Assert.NotEqual(
            a.Select(e => e.PartitionKey).ToList(),
            b.Select(e => e.PartitionKey).ToList());
    }

    [Fact]
    public void Generator_emits_only_harness_emitted_classes()
    {
        var spec = WorkloadSpec.Default();
        var emitted = spec.Mix.Where(c => c.HarnessEmitted).Select(c => c.Name).ToHashSet();
        var notEmitted = spec.Mix.Where(c => !c.HarnessEmitted).Select(c => c.Name).ToHashSet();

        var stream = new WorkloadGenerator(seed: 7, spec, Calibration.V4Placeholder())
            .Generate(1000, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromDays(1), new DateOnly(2026, 11, 27))
            .ToList();

        Assert.All(stream, e => Assert.Contains(e.MixClass, emitted));
        // The engine-generated / cross-mode classes are NEVER produced by the harness (§P1 / §8.4).
        Assert.All(stream, e => Assert.DoesNotContain(e.MixClass, notEmitted));
    }

    [Fact]
    public void Driver_produces_production_bytes_decodable_by_the_engine_schema()
    {
        var (serializer, catalog) = BuildCodec();
        using var capture = new CapturingProducer();
        var driver = new WorkloadDriver(serializer, catalog, producer: capture);

        var synthetic = new WorkloadGenerator(seed: 99, WorkloadSpec.Default(), Calibration.V4Placeholder())
            .Generate(1, new DateTimeOffset(2026, 1, 1, 9, 5, 0, TimeSpan.Zero), TimeSpan.FromMinutes(1), new DateOnly(2026, 11, 27))
            .Single();

        var (topic, message) = InvokeBuildMessage(driver, synthetic);

        // Topic == aggregate_type (the relay convention).
        Assert.Equal("term_deposit", topic);

        // Key == partition_key bytes (per-partition ordering invariant, §8.3).
        Assert.Equal(synthetic.PartitionKey.ToByteArray(), message.Key);

        // Confluent wire format: magic byte 0x00 ‖ big-endian schema_id ‖ avro value.
        Assert.Equal(0x00, message.Value[0]);
        var schemaId = BinaryPrimitives.ReadInt32BigEndian(message.Value.AsSpan(1, 4));
        Assert.Equal(42, schemaId);

        // The VALUE bytes decode against the ENGINE's OWN catalogued schema — proof the harness puts
        // production bytes on the bus (§G1): same serializer, same schema, no parallel encoder.
        var entry = catalog.ForRecordName(nameof(DepositConstituted));
        var record = DecodeAvro(entry.Schema, message.Value.AsSpan(5));
        // deposit_id is an Avro uuid logical type → decodes to a System.Guid.
        Assert.Equal(synthetic.PartitionKey, (Guid)record["deposit_id"]);
        Assert.True((long)record["principal_cents"] > 0);

        // CloudEvents Binary-mode headers (ADR-IC-015) match the relay's set.
        Assert.Equal("1.0", HeaderText(message, "ce_specversion"));
        Assert.Equal("com.bank.deposits.DepositConstituted", HeaderText(message, "ce_type"));
        Assert.Equal("application/avro", HeaderText(message, "ce_datacontenttype"));
        Assert.Equal(synthetic.PartitionKey.ToString(), HeaderText(message, "ce_subject"));
        Assert.Equal("term_deposit", HeaderText(message, "ce_aggregatetype"));
        Assert.Equal("urn:babelstone:loadharness", HeaderText(message, "ce_source"));
    }

    [Fact]
    public void Observer_reads_latency_from_engine_span_duration_not_a_send_clock()
    {
        using var observer = new LatencyObserver();

        // Emit two engine spans on the SHARED ActivitySource the observer listens to — exactly what the
        // engine runtime does at each sync-projection commit. The observer's latency IS the span
        // duration (§P2), so a longer span yields a larger p99 — never the test's wall-clock send time.
        EmitSpan(BabelstoneAttributes.SpanAccrualComputed, TimeSpan.FromMilliseconds(5));
        EmitSpan(BabelstoneAttributes.SpanAccrualComputed, TimeSpan.FromMilliseconds(15));

        var p = observer.PercentilesFor(BabelstoneAttributes.SpanAccrualComputed);
        Assert.NotNull(p);
        Assert.Equal(2, p!.Count);
        Assert.True(p.P99Ms >= 5.0, $"p99 {p.P99Ms}ms should reflect the span duration");

        // A band with a generous budget passes; a band with no spans is an explicit (not vacuous) fail.
        var generous = new SyncLatencyBand("test", BabelstoneAttributes.SpanAccrualComputed, 10_000, 10_000, 10_000);
        Assert.True(observer.Evaluate(generous).Passed);

        var noData = new SyncLatencyBand("absent", "span.never.emitted", 1, 1, 1);
        var verdict = observer.Evaluate(noData);
        Assert.False(verdict.Passed);
        Assert.Contains("no spans", verdict.Reason);
    }

    [Fact]
    public void Simulated_clock_advances_domain_time_monotonically()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new SimulatedClock(start);
        Assert.Equal(start, clock.GetUtcNow());

        clock.Advance(TimeSpan.FromDays(31)); // cross a month boundary — what makes the engine fire month-end events (§P3)
        Assert.Equal(start.AddDays(31), clock.GetUtcNow());

        // Monotonic: time cannot move backwards (a non-monotonic clock would break replay determinism).
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.AdvanceTo(start));
    }

    [Fact]
    public void Run_artefact_leads_with_verdict_and_names_the_seed_to_reproduce()
    {
        var band = new SyncLatencyBand("current_balance", BabelstoneAttributes.SpanAccrualComputed, 20, 80, 200);
        var pass = new LatencyVerdict(band, new LatencyPercentiles(10, 5, 10, 15), Passed: true, "within budget");
        var artefact = new RunArtefact(
            Seed: 555, CodeRevision: "abc123", Calibration.V4Placeholder(), [pass], EventsProduced: 100);

        Assert.True(artefact.Passed);
        Assert.Contains("PASS", artefact.Summary());
        Assert.Contains("seed=555", artefact.Summary());
        Assert.Contains("revision=abc123", artefact.Summary());
    }

    // ---- helpers ----

    private static void EmitSpan(string name, TimeSpan duration)
    {
        using var activity = BabelstoneTelemetry.ActivitySource.StartActivity(name, ActivityKind.Internal);
        // A real engine span's duration is its boundary-to-commit interval; here we hold it open for a
        // fixed span to give the observer a deterministic duration to read.
        if (activity is not null)
        {
            Thread.Sleep(duration);
        }
    }

    private static (string Topic, Message<byte[], byte[]> Message) InvokeBuildMessage(
        WorkloadDriver driver, SyntheticEvent synthetic)
    {
        // BuildMessage is internal; the test project sees it via InternalsVisibleTo.
        return driver.BuildMessage(synthetic);
    }

    private static GenericRecord DecodeAvro(RecordSchema schema, ReadOnlySpan<byte> value)
    {
        var reader = new GenericDatumReader<GenericRecord>(schema, schema);
        using var stream = new MemoryStream(value.ToArray(), writable: false);
        var decoder = new BinaryDecoder(stream);
        return reader.Read(null!, decoder);
    }

    private static string HeaderText(Message<byte[], byte[]> message, string key)
    {
        var header = message.Headers.GetLastBytes(key);
        return Encoding.UTF8.GetString(header);
    }

    // A no-broker IProducer that records produced messages — lets the smoke test exercise the driver's
    // full produce path (encode → frame → header → key) without Redpanda.
    private sealed class CapturingProducer : IProducer<byte[], byte[]>
    {
        public List<(string Topic, Message<byte[], byte[]> Message)> Produced { get; } = [];

        public Task<DeliveryResult<byte[], byte[]>> ProduceAsync(
            string topic, Message<byte[], byte[]> message, CancellationToken cancellationToken = default)
        {
            Produced.Add((topic, message));
            return Task.FromResult(new DeliveryResult<byte[], byte[]>
            {
                Topic = topic,
                Message = message,
                Status = PersistenceStatus.Persisted,
            });
        }

        public Task<DeliveryResult<byte[], byte[]>> ProduceAsync(
            TopicPartition topicPartition, Message<byte[], byte[]> message, CancellationToken cancellationToken = default)
            => ProduceAsync(topicPartition.Topic, message, cancellationToken);

        public void Produce(string topic, Message<byte[], byte[]> message, Action<DeliveryReport<byte[], byte[]>>? deliveryHandler = null)
            => Produced.Add((topic, message));

        public void Produce(TopicPartition topicPartition, Message<byte[], byte[]> message, Action<DeliveryReport<byte[], byte[]>>? deliveryHandler = null)
            => Produced.Add((topicPartition.Topic, message));

        public int Flush(TimeSpan timeout) => 0;
        public void Flush(CancellationToken cancellationToken = default) { }
        public int Poll(TimeSpan timeout) => 0;
        public void Dispose() { }

        // Unused surface — the driver only calls ProduceAsync/Flush/Dispose.
        public Handle Handle => throw new NotSupportedException();
        public string Name => nameof(CapturingProducer);
        public void InitTransactions(TimeSpan timeout) => throw new NotSupportedException();
        public void BeginTransaction() => throw new NotSupportedException();
        public void CommitTransaction(TimeSpan timeout) => throw new NotSupportedException();
        public void CommitTransaction() => throw new NotSupportedException();
        public void AbortTransaction(TimeSpan timeout) => throw new NotSupportedException();
        public void AbortTransaction() => throw new NotSupportedException();
        public void SendOffsetsToTransaction(IEnumerable<TopicPartitionOffset> offsets, IConsumerGroupMetadata groupMetadata, TimeSpan timeout) => throw new NotSupportedException();
        public int AddBrokers(string brokers) => 0;
        public void SetSaslCredentials(string username, string password) => throw new NotSupportedException();
    }
}
