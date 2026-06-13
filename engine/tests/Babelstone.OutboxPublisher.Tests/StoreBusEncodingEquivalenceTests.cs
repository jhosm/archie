using Babelstone.Engine;
using Babelstone.Engine.Avro;
using Babelstone.EventStore;
using Babelstone.EventStore.Migrations;
using Babelstone.Families.TermDeposit;
using Babelstone.FinancialTypes;
using Babelstone.TestFixtures;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.OutboxPublisher.Tests;

/// <summary>
/// STORE_BUS_ENCODING_EQUIVALENCE (ADR-PC-028 inline commitment row 2) — the dual-encode split, proven
/// at the storage seam. In plain English: the engine now writes the SAME event in two different shapes —
/// human-readable JSON into the event-store row (the permanent book of record), and compact Avro (with a
/// real Schema-Registry id) into the outbox row that becomes a bus message. This test appends an event,
/// pulls BOTH stored shapes straight out of PostgreSQL, decodes each with its own codec, and asserts they
/// describe the SAME event — same field values, money still integer cents — so the two encodings can
/// never silently skew.
///
/// Formally: the runtime is wired with a STORE encoder (the self-describing <see cref="JsonStoreSerializer"/>,
/// ADR-PC-028 §Decision) and a separate BUS encoder (the real <see cref="AvroEventSerializer"/> +
/// <see cref="ConfluentSchemaIdResolver"/> registering against the Testcontainer Schema Registry,
/// ADR-IC-002 §P3 / ADR-IC-004 §P3). Both halves commit in the runtime's ONE sink transaction
/// (ES_ATOMIC_APPEND_OUTBOX preserved). The assertion decodes <c>events.payload</c> via JSON and
/// <c>outbox.payload</c> via Avro and compares the reconstructed domain events for value-equality (a C#
/// record's structural equality is the "no skew" predicate). The outbox row's <c>schema_id</c> is also
/// asserted to be a REAL registered id (not the JSON placeholder <c>1</c>), proving the bus half is the
/// Avro encoding rather than a JSON mirror.
/// </summary>
/// <remarks>
/// SPEC-FIRST (ADR-PC-020 §P10): before the dual-encode split this test fails — the runtime took a single
/// codec and the outbox carried the SAME bytes as the store, so <c>outbox.payload</c> was JSON, not Avro,
/// and would not Avro-decode (and its <c>schema_id</c> was the placeholder <c>1</c>). Needs PostgreSQL +
/// the Redpanda Schema Registry (Testcontainers), no broker — the equivalence is asserted at the storage
/// seam, the full Avro-on-the-wire round trip is the sibling <see cref="OutboxToRedpandaIntegrationTests"/>.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class StoreBusEncodingEquivalenceTests : IAsyncLifetime
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

    [Fact]
    public async Task Store_JSON_and_bus_Avro_decode_to_the_same_event()
    {
        var depositId = Guid.NewGuid();

        // --- Arrange: the dual-encode split. STORE = self-describing JSON (ADR-PC-028); BUS = real
        //     Avro + a registered Schema-Registry schema_id (ADR-IC-002/§P3 / ADR-IC-004 §P3). ---
        var catalog = new AvroSchemaCatalog();
        using var schemaIds = ConfluentSchemaIdResolver.Create(catalog, _redpanda.SchemaRegistryUrl, registerIfAbsent: true);
        var storeSerializer = new JsonStoreSerializer();
        var busSerializer = new AvroEventSerializer(catalog, schemaIds);

        var store = new PostgresEventStore(ConnectionString);
        var runtime = new AggregateRuntime<DepositPosition>(
            store,
            new EventStoreSink(store),
            TermDepositFamilyModule.Registry(),
            storeSerializer,                          // store encoder → events.payload (JSON)
            new NullPiiProtector(),
            TimeProvider.System,
            () => DepositPosition.Empty,
            snapshots: null,
            postCommitProjector: null,
            integrationEventCatalog: catalog,         // ADR-IC-017 §P1 catalog-gated relay
            busSerializer: busSerializer);            // bus encoder → outbox.payload (Avro + schema_id)

        // --- Act: append one catalogued event (so it gets BOTH an events row and an outbox row). ---
        var constituted = new DepositConstituted(
            depositId, new Money(PrincipalCents), TanBasisPoints, "rs-2026-01",
            TermDays: 364, StartDate, MaturityDate, "AT_MATURITY", "NONE");
        await runtime.AppendAsync(depositId, expectedVersion: -1, [constituted], Ctx(), CancellationToken.None);

        // --- Assert: the store payload is JSON, the outbox payload is Avro, and they decode to the
        //     SAME event (no store↔bus skew, ADR-PC-028 residual-risk mitigation). ---
        var (storePayload, payloadSchemaId) = await StorePayloadAsync(depositId);
        var (busPayload, outboxSchemaId) = await OutboxPayloadAsync(depositId);

        // The store decodes from the bytes ALONE — no Schema Registry (ADR-PC-028 §Decision).
        var fromStore = (DepositConstituted)storeSerializer.Decode(storePayload, typeof(DepositConstituted));
        // The bus decodes as Avro (the bare Avro value; the codec reads it back against the same schema).
        var fromBus = (DepositConstituted)busSerializer.Decode(busPayload, typeof(DepositConstituted));

        // Record value-equality is the "semantically equal / no skew" predicate: every field, including
        // Money carried as integer cents, matches across the two independent encodings.
        Assert.Equal(constituted, fromStore);
        Assert.Equal(constituted, fromBus);
        Assert.Equal(fromStore, fromBus);
        Assert.Equal(fromStore.Principal.Cents, fromBus.Principal.Cents); // money as integer cents, no skew

        // The two encodings are genuinely DISTINCT bytes — the outbox is the compact Avro binary, the
        // store is the self-describing JSON text — not one mirrored into both columns (the pre-split
        // state this dual-encode replaced). Proof at the byte level that the split actually happened.
        Assert.NotEqual(storePayload.ToArray(), busPayload.ToArray());
        // The store payload is the JSON text (self-describing, ADR-PC-028): it begins with '{'.
        Assert.Equal((byte)'{', storePayload.Span[0]);

        // The outbox carries the REAL registered Schema-Registry schema_id (ADR-IC-004 §P3), the id the
        // relay frames into the Confluent wire format with no publish-time lookup — proof the bus half
        // is the Avro encoding. The store's payload_schema_id stays the JSON placeholder and is NOT a
        // decode key for the self-describing JSON (ADR-PC-028 residual-risk: payload_schema_id
        // reinterpreted) — asserted constant so a regression that started keying JSON decode off it is caught.
        Assert.Equal(schemaIds.ResolveSchemaId("term_deposit.DepositConstituted"), outboxSchemaId);
        Assert.Equal(1, payloadSchemaId);
    }

    private static AppendContext Ctx() => new(
        Family: "term_deposit",
        PackVersion: "pt.2026.1",
        SchemaVersion: "term_deposit@2026.1",
        Actor: "test",
        ValidTime: DateTimeOffset.UtcNow);

    private async Task<(ReadOnlyMemory<byte> Payload, int SchemaId)> StorePayloadAsync(Guid streamId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT payload, payload_schema_id FROM events WHERE stream_id = @id ORDER BY sequence_number LIMIT 1;",
            connection);
        command.Parameters.AddWithValue("id", streamId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "expected one stored event");
        return (reader.GetFieldValue<byte[]>(0), reader.GetInt32(1));
    }

    private async Task<(ReadOnlyMemory<byte> Payload, int SchemaId)> OutboxPayloadAsync(Guid aggregateId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT payload, schema_id FROM outbox WHERE aggregate_id = @id ORDER BY sequence_number LIMIT 1;",
            connection);
        command.Parameters.AddWithValue("id", aggregateId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "expected one outbox row");
        return (reader.GetFieldValue<byte[]>(0), reader.GetInt32(1));
    }

    /// <summary>
    /// The decided self-describing JSON store codec (ADR-PC-028): encodes/decodes ANY DomainEvent from
    /// the bytes alone, no Schema Registry, no .avsc. Mirrors the host's production JsonEventSerializer;
    /// nested here so this integration test does not take a dependency on the host assembly (the same
    /// shape CatalogGatedRelayIntegrationTests uses).
    /// </summary>
    private sealed class JsonStoreSerializer : IEventSerializer
    {
        public EncodedPayload Encode(DomainEvent @event)
            => new(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType()), SchemaId: 1);

        public DomainEvent Decode(ReadOnlyMemory<byte> payload, Type payloadType)
            => (DomainEvent)System.Text.Json.JsonSerializer.Deserialize(payload.Span, payloadType)!;
    }
}
