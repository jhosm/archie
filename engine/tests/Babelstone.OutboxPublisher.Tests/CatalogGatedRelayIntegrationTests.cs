using Babelstone.Engine;
using Babelstone.Engine.Avro;
using Babelstone.EventStore;
using Babelstone.EventStore.Migrations;
using Babelstone.Families.TermDeposit;
using Babelstone.FinancialTypes;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.OutboxPublisher.Tests;

/// <summary>
/// INTEGRATION_EVENT_CATALOG_GATED (commitment catalogue row 21, ADR-IC-017 §P1) — the catalog-gated
/// relay, end to end against real PostgreSQL. In plain English: the engine appends every event it is
/// told to, but only the events that have been deliberately promoted (have a catalogue/<c>.avsc</c>
/// entry) ever get an outbox row — and an outbox row is the ONLY thing the relay can ever publish. So
/// an UNCATALOGUED event is store-only by construction: it is appended, folded, and replayable, but it
/// never reaches the durable bus.
///
/// The test appends, on one stream, a CATALOGUED event (<c>DepositConstituted</c>, which has an
/// <c>.avsc</c>) and an UNCATALOGUED event (<c>InterestAccrued</c>, which has a registered handler — so
/// the engine can append/fold it — but no <c>.avsc</c> after the ADR-IC-017 §P4 de-promotion of the
/// accrual mechanics), through the durable <see cref="AggregateRuntime{TState}"/> wired with the REAL
/// <see cref="AvroSchemaCatalog"/> as the §P1 gate. It then asserts the biconditional at the storage
/// seam: BOTH events are in the event store (appended), but ONLY the catalogued one produced an outbox
/// row (will reach the bus). The append+ outbox atomicity (ES_ATOMIC_APPEND_OUTBOX) is exercised by
/// construction — both halves commit in the runtime's single sink transaction.
/// </summary>
/// <remarks>
/// The store codec is the decided self-describing <c>JsonEventSerializer</c> (ADR-PC-028) so the
/// uncatalogued event can be encoded for the STORE payload with no schema — which is exactly why the
/// store format is JSON and the bus surface is the deliberately-promoted Avro subset. The bus side is
/// asserted at the OUTBOX (a PENDING row == "will be published"); the full Avro-on-the-wire round trip
/// is the sibling <see cref="OutboxToRedpandaIntegrationTests"/>, which this complements rather than
/// duplicates. Needs only PostgreSQL (Testcontainers), no broker.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CatalogGatedRelayIntegrationTests : IAsyncLifetime
{
    private const long PrincipalCents = 1_000_000;
    private const int TanBasisPoints = 300;
    private static readonly DateOnly StartDate = new(2026, 1, 1);
    private static readonly DateOnly MaturityDate = new(2026, 12, 31);

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await new MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    [Fact]
    public async Task Only_catalogued_events_get_an_outbox_row_while_both_are_appended()
    {
        var depositId = Guid.NewGuid();

        // The §P1 gate is the REAL embedded-schema catalogue. After the ADR-IC-017 §P4 promotion pass:
        // DepositConstituted + InterestPaid have an .avsc (catalogued → publishable); the de-promoted
        // InterestAccrued accrual-mechanics event does NOT (uncatalogued → store-only). This is the
        // SPEC-FIRST flip — InterestPaid is now CATALOGUED (was the store-only example pre-§P4), and
        // InterestAccrued is now the uncatalogued one.
        var catalog = new AvroSchemaCatalog();
        Assert.True(catalog.IsCataloguedIntegrationEvent("term_deposit.DepositConstituted"));
        Assert.True(catalog.IsCataloguedIntegrationEvent("term_deposit.InterestPaid"));
        Assert.False(catalog.IsCataloguedIntegrationEvent("term_deposit.InterestAccrued"));

        var store = new PostgresEventStore(ConnectionString);
        var runtime = new AggregateRuntime<DepositPosition>(
            store,
            new EventStoreSink(store),
            TermDepositFamilyModule.Registry(),
            new SelfDescribingJsonSerializer(),      // the decided self-describing store codec (ADR-PC-028)
            new NullPiiProtector(),
            TimeProvider.System,
            () => DepositPosition.Empty,
            snapshots: null,
            postCommitProjector: null,
            integrationEventCatalog: catalog);       // ADR-IC-017 §P1 catalog-gated relay

        // --- Act: append one catalogued, then one uncatalogued event on the same stream. ---
        var constituted = new DepositConstituted(
            depositId, new Money(PrincipalCents), TanBasisPoints, "rs-2026-01",
            TermDays: 364, StartDate, MaturityDate, "AT_MATURITY", "NONE");
        await runtime.AppendAsync(depositId, expectedVersion: -1, [constituted], Ctx(), CancellationToken.None);

        var accrued = new InterestAccrued(new Money(30_417), MaturityDate);
        await runtime.AppendAsync(depositId, expectedVersion: 0, [accrued], Ctx(), CancellationToken.None);

        // --- Assert: BOTH events are in the event store (appended/folded/replayable). ---
        Assert.Equal(
            ["term_deposit.DepositConstituted", "term_deposit.InterestAccrued"],
            await StoredEventTypesAsync(depositId));

        // --- Assert: ONLY the catalogued event produced an outbox row (the bus surface). The
        //     de-promoted InterestAccrued is store-only — it never reaches the bus. ---
        Assert.Equal(["term_deposit.DepositConstituted"], await OutboxEventTypesAsync(depositId));
    }

    [Fact]
    public async Task The_promoted_InterestPaid_produces_an_outbox_row_while_de_promoted_accrual_mechanics_stay_store_only()
    {
        // The SPEC-FIRST assertion of the ADR-IC-017 §P4 promotion: after the swap, the relay now
        // publishes InterestPaid (the coarse coupon/advance payout fact) and keeps the fine-grained
        // InterestAccrued / WithholdingApplied accrual mechanics store-only. Appends all three on one
        // stream (after constitution) and asserts the outbox carries EXACTLY DepositConstituted +
        // InterestPaid — the new catalogued set on this stream — never the two de-promoted ones.
        var depositId = Guid.NewGuid();
        var catalog = new AvroSchemaCatalog();

        var store = new PostgresEventStore(ConnectionString);
        var runtime = new AggregateRuntime<DepositPosition>(
            store, new EventStoreSink(store), TermDepositFamilyModule.Registry(),
            new SelfDescribingJsonSerializer(), new NullPiiProtector(), TimeProvider.System,
            () => DepositPosition.Empty, snapshots: null, postCommitProjector: null,
            integrationEventCatalog: catalog);

        var constituted = new DepositConstituted(
            depositId, new Money(PrincipalCents), TanBasisPoints, "rs-2026-01",
            TermDays: 364, StartDate, MaturityDate, "AT_MATURITY", "NONE");
        await runtime.AppendAsync(depositId, expectedVersion: -1, [constituted], Ctx(), CancellationToken.None);

        // One coupon period: the accrual mechanics (InterestAccrued + WithholdingApplied, now
        // store-only) and the coarse payout (InterestPaid, now catalogued) appended together.
        var accrued = new InterestAccrued(new Money(30_417), MaturityDate);
        var withheld = new WithholdingApplied(new Money(8_517), new Money(21_900));
        var paid = new InterestPaid(depositId, new Money(30_417), new Money(8_517), new Money(21_900), MaturityDate);
        await runtime.AppendAsync(
            depositId, expectedVersion: 0, [accrued, withheld, paid], Ctx(), CancellationToken.None);

        // All four events are in the store (appended/folded/replayable).
        Assert.Equal(
            ["term_deposit.DepositConstituted", "term_deposit.InterestAccrued",
             "term_deposit.WithholdingApplied", "term_deposit.InterestPaid"],
            await StoredEventTypesAsync(depositId));

        // The bus surface is EXACTLY the catalogued subset: DepositConstituted + the promoted
        // InterestPaid. The de-promoted accrual mechanics produced no outbox row.
        Assert.Equal(
            ["term_deposit.DepositConstituted", "term_deposit.InterestPaid"],
            await OutboxEventTypesAsync(depositId));
    }

    [Fact]
    public async Task A_store_only_batch_appends_with_no_outbox_row()
    {
        // A batch of ONLY uncatalogued events writes event rows but ZERO outbox rows — the relaxed
        // §P2 lower bound (ADR-IC-017 §P1): atomicity holds (one transaction), but an uncatalogued
        // event has no outbox row. The append must SUCCEED, not throw the old "no event without its
        // outbox row" guard.
        var depositId = Guid.NewGuid();
        var catalog = new AvroSchemaCatalog();

        var store = new PostgresEventStore(ConnectionString);
        var runtime = new AggregateRuntime<DepositPosition>(
            store, new EventStoreSink(store), TermDepositFamilyModule.Registry(),
            new SelfDescribingJsonSerializer(), new NullPiiProtector(), TimeProvider.System,
            () => DepositPosition.Empty, snapshots: null, postCommitProjector: null,
            integrationEventCatalog: catalog);

        // DepositConstituted first (so the stream exists and folds), then an uncatalogued-only batch.
        var constituted = new DepositConstituted(
            depositId, new Money(PrincipalCents), TanBasisPoints, "rs-2026-01",
            TermDays: 364, StartDate, MaturityDate, "AT_MATURITY", "NONE");
        await runtime.AppendAsync(depositId, expectedVersion: -1, [constituted], Ctx(), CancellationToken.None);

        var accrued = new InterestAccrued(new Money(30_417), MaturityDate);
        var renewed = new DepositRenewed(
            depositId, Guid.NewGuid(), new Money(PrincipalCents), "rs-2026-02",
            TanBasisPoints, 364, MaturityDate, MaturityDate.AddYears(1));

        // Both InterestAccrued (de-promoted to internal/store-only by ADR-IC-017 §P4) and DepositRenewed
        // are schemaless → the whole batch is store-only. (InterestPaid is NO LONGER a valid store-only
        // example — it was promoted to a catalogued integration event.)
        var head = await runtime.AppendAsync(
            depositId, expectedVersion: 0, [accrued, renewed], Ctx(), CancellationToken.None);

        Assert.Equal(2, head); // three events appended (seq 0,1,2), head == 2.
        Assert.Equal(3, (await StoredEventTypesAsync(depositId)).Count);
        // Only the catalogued DepositConstituted produced an outbox row; the store-only batch added none.
        Assert.Equal(["term_deposit.DepositConstituted"], await OutboxEventTypesAsync(depositId));
    }

    private static AppendContext Ctx() => new(
        Family: "term_deposit",
        PackVersion: "pt.2026.1",
        SchemaVersion: "term_deposit@2026.1",
        Actor: "test",
        ValidTime: DateTimeOffset.UtcNow);

    private async Task<List<string>> StoredEventTypesAsync(Guid streamId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT event_type FROM events WHERE stream_id = @id ORDER BY sequence_number;", connection);
        command.Parameters.AddWithValue("id", streamId);
        return await ReadStringsAsync(command);
    }

    private async Task<List<string>> OutboxEventTypesAsync(Guid aggregateId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT event_type FROM outbox WHERE aggregate_id = @id ORDER BY sequence_number;", connection);
        command.Parameters.AddWithValue("id", aggregateId);
        return await ReadStringsAsync(command);
    }

    private static async Task<List<string>> ReadStringsAsync(NpgsqlCommand command)
    {
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    /// <summary>
    /// The decided self-describing JSON store codec (ADR-PC-028): encodes ANY DomainEvent from the
    /// bytes alone, with no Schema Registry and no .avsc — which is exactly why an UNCATALOGUED event
    /// can still be appended to the store. Mirrors the host's production JsonEventSerializer; nested
    /// here so this integration test does not take a dependency on the host assembly.
    /// </summary>
    private sealed class SelfDescribingJsonSerializer : IEventSerializer
    {
        public EncodedPayload Encode(DomainEvent @event)
            => new(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType()), SchemaId: 1);

        public DomainEvent Decode(ReadOnlyMemory<byte> payload, Type payloadType)
            => (DomainEvent)System.Text.Json.JsonSerializer.Deserialize(payload.Span, payloadType)!;
    }
}
