using System.Buffers.Binary;
using System.Text;
using Avro.Generic;
using Babelstone.Engine;
using Babelstone.Engine.Avro;
using Babelstone.EventStore;
using Babelstone.EventStore.Migrations;
using Babelstone.Families.TermDeposit;
using Babelstone.FinancialTypes;
using Babelstone.TestFixtures;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.OutboxPublisher.Tests;

/// <summary>
/// The E.4 walking-skeleton round-trip (the fixture E.6 reuses): append the catalogued term-deposit
/// events through <see cref="AggregateRuntime{TState}"/> wired with the real Avro codec (so the
/// outbox rows carry real SR schema_ids), run the relay <see cref="OutboxDrainer.DrainOnceAsync"/>
/// to publish to Redpanda, then CONSUME and assert the Avro payload, the CloudEvents headers, and
/// the PUBLISHED flip.
///
/// After the ADR-IC-017 §P4 promotion pass the bus-encodable set is the three CATALOGUED events
/// (DepositConstituted, InterestPaid, DepositMatured); the de-promoted InterestAccrued/WithholdingApplied
/// accrual mechanics have no .avsc, so the Avro codec cannot encode them — they are store-only and never
/// appear here. This test wires the codec directly (NOT the catalog-gated runtime), so it appends only
/// the catalogued set: a constitution, then a coupon payout (InterestPaid) and the maturity payout.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OutboxToRedpandaIntegrationTests : IAsyncLifetime
{
    // The canonical AT_MATURITY numbers (E.1): principal 1,000,000c (€10,000.00), TAN 300 bps,
    // gross 30,417c, tax 8,517c, net 21,900c, payout 1,021,900c.
    private const long PrincipalCents = 1_000_000;
    private const int TanBasisPoints = 300;
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

    [Fact]
    public async Task Catalogued_appends_drain_to_Redpanda_as_Avro_with_CloudEvents_headers_and_flip_to_published()
    {
        var depositId = Guid.NewGuid();

        // --- Arrange: Avro codec registered against the Redpanda Schema Registry. ---
        var catalog = new AvroSchemaCatalog();
        using var schemaIds = ConfluentSchemaIdResolver.Create(catalog, _redpanda.SchemaRegistryUrl, registerIfAbsent: true);
        var serializer = new AvroEventSerializer(catalog, schemaIds);

        // --- Arrange: durable runtime over real PostgreSQL with the term-deposit handlers. ---
        var store = new PostgresEventStore(ConnectionString);
        var runtime = new AggregateRuntime<DepositPosition>(
            store,
            new EventStoreSink(store),
            TermDepositFamilyModule.Registry(),
            serializer,
            new NullPiiProtector(),
            TimeProvider.System,
            () => DepositPosition.Empty);

        // --- Act 1: append the three CATALOGUED events (constitution, a coupon payout, maturity). The
        //     de-promoted InterestAccrued/WithholdingApplied have no .avsc and cannot be Avro-encoded,
        //     so they are deliberately absent from this bus round-trip (ADR-IC-017 §P4). ---
        var constituted = new DepositConstituted(
            depositId, new Money(PrincipalCents), TanBasisPoints, "rs-2026-01",
            TermDays: 364, StartDate, MaturityDate, "AT_MATURITY", "NONE");
        await runtime.AppendAsync(depositId, expectedVersion: -1, [constituted], Ctx(), CancellationToken.None);

        var paid = new InterestPaid(depositId, new Money(GrossCents), new Money(TaxCents), new Money(NetCents), MaturityDate);
        var matured = new DepositMatured(new Money(PrincipalCents), new Money(NetCents), new Money(PayoutCents), MaturityDate);
        await runtime.AppendAsync(depositId, expectedVersion: 0, [paid, matured], Ctx(), CancellationToken.None);

        // --- Act 2: drain the outbox to Redpanda. ---
        var options = new OutboxRelayOptions
        {
            ConnectionString = ConnectionString,
            BootstrapServers = _redpanda.BootstrapServers,
            Source = "urn:babelstone:engine:test",
        };
        await using var drainer = new OutboxDrainer(options);
        var published = await drainer.DrainOnceAsync(CancellationToken.None);
        Assert.Equal(3, published);

        // --- Assert: the outbox rows all flipped to PUBLISHED. ---
        Assert.Equal(0, await CountPendingAsync(depositId));
        Assert.Equal(3, await CountPublishedAsync(depositId));

        // --- Assert: consume the three records from Redpanda and check Avro + headers. ---
        var records = ConsumeAll(topic: "term_deposit", expected: 3);
        Assert.Equal(3, records.Count);

        // Per-aggregate order is preserved (ADR-IC-004 §P2): the three ce_type values in sequence.
        Assert.Equal(
            [
                "com.bank.deposits.DepositConstituted",
                "com.bank.deposits.InterestPaid",
                "com.bank.deposits.DepositMatured",
            ],
            records.Select(r => Header(r, "ce_type")).ToList());

        foreach (var record in records)
        {
            // CloudEvents Binary-mode headers present on every record (ADR-IC-015).
            Assert.False(string.IsNullOrEmpty(Header(record, "ce_id")));
            Assert.Equal("1.0", Header(record, "ce_specversion"));
            Assert.Equal("application/avro", Header(record, "ce_datacontenttype"));
            Assert.Equal(depositId.ToString(), Header(record, "ce_subject"));
            Assert.Equal("term_deposit", Header(record, "ce_aggregatetype"));
            Assert.Equal("urn:babelstone:engine:test", Header(record, "ce_source"));
            // The key is the aggregate_id (partition routing).
            Assert.Equal(depositId.ToByteArray(), record.Message.Key);
        }

        // DepositConstituted payload: deserialize the Avro value, assert the canonical fields.
        var constitutedRecord = DeserializeValue(records[0].Message.Value);
        // uuid logicalType surfaces as a System.Guid through the Confluent/Apache.Avro SerDe.
        Assert.Equal(depositId, (Guid)constitutedRecord["deposit_id"]);
        Assert.Equal(PrincipalCents, (long)constitutedRecord["principal_cents"]);
        Assert.Equal(TanBasisPoints, (int)constitutedRecord["tan_basis_points"]);
        Assert.Equal("rs-2026-01", (string)constitutedRecord["rate_sheet_version_id"]);
        Assert.Equal("AT_MATURITY", (string)constitutedRecord["interest_variant"]);
        // DateOnly is a date-logicalType field; the Confluent Avro deserializer surfaces it as a
        // UTC-midnight DateTime (on the wire it is an int day-count since the Unix epoch).
        Assert.Equal(StartDate, DateOnly.FromDateTime((DateTime)constitutedRecord["start_date"]));

        // The promoted InterestPaid carries the coupon's three money legs + the deposit reference.
        var paidRecord = DeserializeValue(records[1].Message.Value);
        Assert.Equal(depositId, (Guid)paidRecord["deposit_id"]);
        Assert.Equal(GrossCents, (long)paidRecord["gross_interest_cents"]);
        Assert.Equal(TaxCents, (long)paidRecord["withholding_tax_cents"]);
        Assert.Equal(NetCents, (long)paidRecord["net_interest_cents"]);

        // The maturity event carries the canonical payout legs.
        var maturedRecord = DeserializeValue(records[2].Message.Value);
        Assert.Equal(PayoutCents, (long)maturedRecord["total_payout_cents"]);
        Assert.Equal(NetCents, (long)maturedRecord["net_interest_paid_cents"]);
        Assert.Equal(PrincipalCents, (long)maturedRecord["principal_returned_cents"]);
    }

    /// <summary>
    /// The end-to-end CE-extension-header seam (ADR-IC-018 §P5, bd mtto.1): a <c>DepositMatured</c>
    /// carrying a non-NONE <c>auto_renewal_policy</c> must surface the policy as the promoted
    /// <c>ce_autorenewalpolicy</c> header on the consumed Redpanda record — proven through the REAL
    /// outbox <c>integration_headers</c> JSONB column and the relay (not just the in-memory
    /// <c>BuildHeadersCore</c> transform). The sibling <c>DepositConstituted</c> declares no extension
    /// header, so it must carry NONE — the relay names no event, it copies only what the event declared.
    /// </summary>
    [Fact]
    public async Task DepositMatured_with_a_renewal_policy_drains_with_the_promoted_ce_autorenewalpolicy_header()
    {
        var depositId = Guid.NewGuid();

        var catalog = new AvroSchemaCatalog();
        using var schemaIds = ConfluentSchemaIdResolver.Create(catalog, _redpanda.SchemaRegistryUrl, registerIfAbsent: true);
        var serializer = new AvroEventSerializer(catalog, schemaIds);

        var store = new PostgresEventStore(ConnectionString);
        var runtime = new AggregateRuntime<DepositPosition>(
            store, new EventStoreSink(store), TermDepositFamilyModule.Registry(),
            serializer, new NullPiiProtector(), TimeProvider.System, () => DepositPosition.Empty);

        // Constitute with an auto-renewing policy, then mature carrying that policy. The maturity
        // event's IntegrationHeaders override declares {autorenewalpolicy}, which the append persists
        // into the outbox integration_headers JSONB column.
        var constituted = new DepositConstituted(
            depositId, new Money(PrincipalCents), TanBasisPoints, "rs-2026-01",
            TermDays: 364, StartDate, MaturityDate, "AT_MATURITY", "SAME_TERM_CURRENT_RATE");
        await runtime.AppendAsync(depositId, expectedVersion: -1, [constituted], Ctx(), CancellationToken.None);

        var matured = new DepositMatured(
            new Money(PrincipalCents), new Money(NetCents), new Money(PayoutCents), MaturityDate,
            AutoRenewalPolicy: "SAME_TERM_CURRENT_RATE");
        await runtime.AppendAsync(depositId, expectedVersion: 0, [matured], Ctx(), CancellationToken.None);

        var options = new OutboxRelayOptions
        {
            ConnectionString = ConnectionString,
            BootstrapServers = _redpanda.BootstrapServers,
            Source = "urn:babelstone:engine:test",
        };
        await using var drainer = new OutboxDrainer(options);
        Assert.Equal(2, await drainer.DrainOnceAsync(CancellationToken.None));

        var records = ConsumeAll(topic: "term_deposit", expected: 2);
        Assert.Equal(2, records.Count);

        // The maturity record carries the promoted extension header verbatim — through the real
        // outbox JSONB column and the relay, the full seam.
        var maturedRecord = records.Single(r => Header(r, "ce_type") == "com.bank.deposits.DepositMatured");
        Assert.Equal("SAME_TERM_CURRENT_RATE", Header(maturedRecord, "ce_autorenewalpolicy"));
        // …and the durable Avro payload carries the field too.
        Assert.Equal("SAME_TERM_CURRENT_RATE", (string)DeserializeValue(maturedRecord.Message.Value)["auto_renewal_policy"]);

        // The constitution event declared no extension header → the relay emits none.
        var constitutedRecord = records.Single(r => Header(r, "ce_type") == "com.bank.deposits.DepositConstituted");
        Assert.False(constitutedRecord.Message.Headers.TryGetLastBytes("ce_autorenewalpolicy", out _));
    }

    private static AppendContext Ctx() => new(
        Family: "term_deposit",
        PackVersion: "pt.2026.1",
        SchemaVersion: "term_deposit@2026.1",
        Actor: "test",
        ValidTime: DateTimeOffset.UtcNow);

    // ---- Consume + Avro deserialize -----------------------------------------------------

    private List<ConsumeResult<byte[], byte[]>> ConsumeAll(string topic, int expected)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _redpanda.BootstrapServers,
            GroupId = $"e4-test-{Guid.NewGuid()}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        };

        using var consumer = new ConsumerBuilder<byte[], byte[]>(config).Build();
        consumer.Subscribe(topic);

        var results = new List<ConsumeResult<byte[], byte[]>>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (results.Count < expected && DateTime.UtcNow < deadline)
        {
            var result = consumer.Consume(TimeSpan.FromSeconds(5));
            if (result?.Message is not null)
            {
                results.Add(result);
            }
        }

        consumer.Close();
        return results;
    }

    private GenericRecord DeserializeValue(byte[] wireFormatValue)
    {
        // The relay produced Confluent wire format (magic byte + big-endian schema_id + avro).
        // Use the Confluent Avro deserializer against the Redpanda SR to decode it back.
        Assert.Equal(0x00, wireFormatValue[0]);
        _ = BinaryPrimitives.ReadInt32BigEndian(wireFormatValue.AsSpan(1, 4)); // schema_id (resolved by the deserializer)

        using var sr = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = _redpanda.SchemaRegistryUrl });
        var deserializer = new AvroDeserializer<GenericRecord>(sr);
        return deserializer
            .DeserializeAsync(wireFormatValue, isNull: false, SerializationContext.Empty)
            .GetAwaiter().GetResult();
    }

    private static string Header(ConsumeResult<byte[], byte[]> r, string key)
        => r.Message.Headers.TryGetLastBytes(key, out var bytes) ? Encoding.UTF8.GetString(bytes) : string.Empty;

    // ---- Outbox status assertions -------------------------------------------------------

    private Task<long> CountPendingAsync(Guid aggregateId) => CountAsync(aggregateId, "PENDING");

    private Task<long> CountPublishedAsync(Guid aggregateId) => CountAsync(aggregateId, "PUBLISHED");

    private async Task<long> CountAsync(Guid aggregateId, string status)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM outbox WHERE aggregate_id = @id AND status = @status;", connection);
        command.Parameters.AddWithValue("id", aggregateId);
        command.Parameters.AddWithValue("status", status);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
