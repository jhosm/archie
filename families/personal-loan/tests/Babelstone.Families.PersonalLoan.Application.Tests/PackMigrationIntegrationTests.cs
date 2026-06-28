using System.Text.Json;
using Babelstone.Engine;
using Babelstone.Engine.Hosting;
using Babelstone.EventStore;
using Babelstone.Families.PersonalLoan;
using Babelstone.Packs;
using Babelstone.RateSheets;
using Babelstone.TestFixtures;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Families.PersonalLoan.Application.Tests;

/// <summary>
/// The operator pack-migration write-path end-to-end (ADR-PC-009 §P3, surface §3.6) for the personal_loan
/// family, against real PostgreSQL. In plain English: disburse a loan (pinned for life to pt.2026.1), then
/// run an operator migration to pt.2027.1 — and prove the loan's events flip to the new pack from the
/// migration forward while its earlier history stays on the old pack, exactly the "pinned for life, except
/// by explicit audited migration" guarantee ADR-PC-009 makes executable. It ALSO proves the family's
/// instance_filter seam: <see cref="LoanInstanceFilterResolver"/> resolves the live loan population by
/// FOLDING the event store (personal_loan has no read model), keeping only the still-Active loans.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PackMigrationIntegrationTests(PackMigrationIntegrationTests.LoanFixture fixture)
    : IClassFixture<PackMigrationIntegrationTests.LoanFixture>
{
    private const string FromPack = "pt.2026.1";
    private const string ToPack = "pt.2027.1";
    private const string ProductId = "cp_pt_general_12m";
    private const string Role = "standard";

    [Fact]
    public async Task Preview_reports_the_matched_set_without_emitting_any_event()
    {
        await fixture.EnsureRateSheetAsync();
        var (_, service, migration) = Compose(fixture.ConnectionString);
        var loanId = await DisburseAsync(service);

        var before = await fixture.CountAsync("events", "stream_id", loanId);

        // Preview matches the loan (it is pinned to pt.2026.1) but appends NOTHING — the pre-emission
        // confirmation step (ADR-PC-009 Residual-risks: previewable before emission).
        var matched = await migration.PreviewAsync(FromPack, [loanId]);

        Assert.Contains(loanId, matched);
        Assert.Equal(before, await fixture.CountAsync("events", "stream_id", loanId)); // no new events
    }

    [Fact]
    public async Task Migration_re_pins_the_instance_on_the_envelope_from_the_migration_forward()
    {
        await fixture.EnsureRateSheetAsync();
        var (_, service, migration) = Compose(fixture.ConnectionString);
        var loanId = await DisburseAsync(service);

        // One PackVersionMigrated appended to the loan, pinned to the target pack.
        var migrated = await migration.MigrateAsync(
            FromPack, ToPack, [loanId], "mig-2027-rate-change", "operator:regulatory-ops",
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal([loanId], migrated);

        // The re-pin lives on the ENVELOPE (ADR-PC-009 §P1/§P3): sequence 0 (LoanDisbursed) stays on the
        // OLD pack; the migration event (the new head) carries the NEW pack.
        var pins = await PackVersionsBySequenceAsync(loanId);
        Assert.Equal(FromPack, pins[0]);   // pre-migration history pinned old
        Assert.Equal(ToPack, pins[^1]);    // the migration (and onward) pinned new

        // The migration event is the engine-declared operations.PackVersionMigrated (no family prefix —
        // event-store §4.3). STORE-ONLY by construction; here we assert the stored event_type, the
        // write-path's own contract.
        var head = await HeadEventTypeAsync(loanId);
        Assert.Equal("operations.PackVersionMigrated", head);
    }

    [Fact]
    public async Task Migration_is_idempotent_re_running_the_same_migration_appends_no_second_event()
    {
        await fixture.EnsureRateSheetAsync();
        var (_, service, migration) = Compose(fixture.ConnectionString);
        var loanId = await DisburseAsync(service);

        await migration.MigrateAsync(
            FromPack, ToPack, [loanId], "mig-idem", "operator:regulatory-ops",
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var afterFirst = await fixture.CountAsync("events", "stream_id", loanId);

        // Re-running the SAME migration: the loan is no longer on the from-version, so it is SKIPPED — no
        // second PackVersionMigrated (ADR-PC-009 §P3 current-pin guard).
        var second = await migration.MigrateAsync(
            FromPack, ToPack, [loanId], "mig-idem", "operator:regulatory-ops",
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Empty(second); // nothing left to migrate
        Assert.Equal(afterFirst, await fixture.CountAsync("events", "stream_id", loanId));
    }

    [Fact]
    public async Task A_migrated_instance_folds_to_the_same_position_the_migration_is_a_no_op_on_state()
    {
        await fixture.EnsureRateSheetAsync();
        var (runtime, service, migration) = Compose(fixture.ConnectionString);
        var loanId = await DisburseAsync(service);

        var beforeMigration = await runtime.LoadAsync(loanId);

        await migration.MigrateAsync(
            FromPack, ToPack, [loanId], "mig-noop", "operator:regulatory-ops",
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        // The projection is unchanged except the head version advanced by the migration event — the fold is
        // a no-op (the pin is on the envelope, not the position). Lifecycle, money, ids all identical.
        var afterMigration = await runtime.LoadAsync(loanId);
        Assert.Equal(beforeMigration.State, afterMigration.State);
        Assert.Equal(beforeMigration.Version + 1, afterMigration.Version);
    }

    // ---- Predicate instance_filter folding the event store (surface §3.6; personal_loan has no read model) ----

    [Fact]
    public async Task Resolver_resolves_only_the_active_loans_by_folding_the_event_store()
    {
        await fixture.EnsureRateSheetAsync();
        var (runtime, service, _) = Compose(fixture.ConnectionString);

        // A live loan (Active) and a written-off one (terminal). The resolver folds each stream and keeps
        // only the Active — the terminal WrittenOff is excluded (LifecycleTransitions terminal states).
        var active = await DisburseAsync(service);
        var writtenOff = await DisburseAsync(service);
        await service.WriteOffAsync(new WriteOffLoanCommand(
            LoanId: writtenOff,
            WrittenOffAt: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            WriteOffReason: "DEFAULT_UNRECOVERABLE",
            Actor: "operator:collections",
            CommandId: Guid.NewGuid()));

        var resolver = new LoanInstanceFilterResolver(
            new PostgresEventStore(fixture.ConnectionString), runtime, "personal_loan");

        var resolved = await resolver.ResolveAsync(new InstanceFilter("personal_loan", true));

        Assert.Contains(active, resolved);
        Assert.DoesNotContain(writtenOff, resolved);
        // Stable order (sorted ids) — a second resolve yields the identical sequence.
        Assert.Equal(resolved, await resolver.ResolveAsync(new InstanceFilter("personal_loan", true)));
    }

    [Fact]
    public async Task Resolver_rejects_unsupported_predicates()
    {
        await fixture.EnsureRateSheetAsync();
        var (runtime, _, _) = Compose(fixture.ConnectionString);
        var resolver = new LoanInstanceFilterResolver(
            new PostgresEventStore(fixture.ConnectionString), runtime, "personal_loan");

        // A wrong family or currently_active=false is a wiring bug, not operator input (the endpoint screens
        // both first), so the resolver fails loud rather than mis-resolving.
        await Assert.ThrowsAsync<ArgumentException>(
            () => resolver.ResolveAsync(new InstanceFilter("term_deposit", true)));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => resolver.ResolveAsync(new InstanceFilter("personal_loan", false)));
    }

    // ---- helpers ----

    private async Task<Guid> DisburseAsync(PersonalLoanConstitutionService service)
    {
        var loanId = Guid.NewGuid();
        await service.DisburseAsync(new DisburseLoanCommand(
            LoanId: loanId,
            PrincipalCents: 1_000_000,
            ProductId: ProductId,
            Role: Role,
            TermMonths: 12,
            StartDate: new DateOnly(2026, 1, 15),
            DisbursedAt: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
            Purpose: "general",
            DisbursementAccountRef: "acct-token-borrower",
            Actor: "mcp:dev",
            CommandId: Guid.NewGuid()));
        return loanId;
    }

    private async Task<IReadOnlyList<string>> PackVersionsBySequenceAsync(Guid streamId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT pack_version FROM events WHERE stream_id = @id ORDER BY sequence_number;", connection);
        command.Parameters.AddWithValue("id", streamId);
        var pins = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            pins.Add(reader.GetString(0));
        }

        return pins;
    }

    private async Task<string> HeadEventTypeAsync(Guid streamId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT event_type FROM events WHERE stream_id = @id ORDER BY sequence_number DESC LIMIT 1;", connection);
        command.Parameters.AddWithValue("id", streamId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Compose the durable runtime, the disbursement decider, and the operator pack-migration
    /// service over the personal_loan family. The runtime registry includes the engine-declared
    /// cross-cutting handlers (PersonalLoanFamilyModule.Registry() splices them in), so it folds the
    /// PackVersionMigrated the migration appends.</summary>
    private static (AggregateRuntime<LoanPosition> Runtime, PersonalLoanConstitutionService Service, PackMigrationService<LoanPosition> Migration)
        Compose(string connectionString)
    {
        var store = new PostgresEventStore(connectionString);
        var runtime = new AggregateRuntime<LoanPosition>(
            store, new EventStoreSink(store), PersonalLoanFamilyModule.Registry(),
            new JsonEventSerializer(), new NullPiiProtector(), TimeProvider.System,
            () => LoanPosition.Empty);
        var service = new PersonalLoanConstitutionService(
            runtime, new PostgresRateSheetStore(connectionString), MinimalPack());
        var migration = new PackMigrationService<LoanPosition>(runtime, store, "personal_loan");
        return (runtime, service, migration);
    }

    // The disbursement reads no pack primitive at constitution — it touches the pack only for its
    // VersionKey (stamped on the AppendContext as the pack_version pin), so a minimal structurally-valid
    // pack with VersionKey "pt.2026.1" suffices.
    private static VerifiedPack MinimalPack() => new(
        Manifest: new PackManifest(
            PackId: "pt", PackVersion: "2026.1", Namespace: "pt", ManifestSchemaVersion: 1,
            Publisher: "test", PackEffectiveFrom: new DateOnly(2026, 1, 1), BasedOnPackVersion: null,
            DeltaSummary: "", BreakingChanges: [], EngineCompatibleVersions: "*",
            SchemaPins: new Dictionary<string, string>(), RateSheetRefNames: [], TemplateRefNames: [], TestCorpusRef: ""),
        DayCounts: new Dictionary<string, PackDayCount>(),
        Withholdings: new Dictionary<string, PackWithholding>(),
        Fgds: new Dictionary<string, PackFgd>(),
        Reportings: new Dictionary<string, PackReporting>(),
        Parameters: new PackParameters(MaxConsumerRateBps: 0, AutoRenewalOptoutWindowDays: 0),
        RateSheetRefs: [],
        Families: []);

    /// <summary>A plain JSON codec standing in for the Avro codec (the same idiom the term-deposit tests
    /// use): SchemaId is a constant 1. The runtime is wired with this, so the appended payloads are JSON the
    /// store decodes back.</summary>
    private sealed class JsonEventSerializer : IEventSerializer
    {
        public EncodedPayload Encode(DomainEvent @event)
            => new(JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType()), SchemaId: 1);

        public DomainEvent Decode(ReadOnlyMemory<byte> payload, Type payloadType)
            => (DomainEvent)JsonSerializer.Deserialize(payload.Span, payloadType)!;
    }

    /// <summary>
    /// PG18 with the ENGINE event-store migrations applied (events, outbox, snapshots, rate_sheets,
    /// projections) AND the personal_loan family's own read-model migration
    /// (read_model.installment_calendar). The family read model ships in a SEPARATE migration set on the
    /// same tier (ADR-IC-005 §S1); the fixture applies ENGINE FIRST then FAMILY, the hard ordering the
    /// family migration's fail-loud role guard depends on.
    /// </summary>
    public sealed class LoanFixture : IAsyncLifetime
    {
        private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

        public string ConnectionString => _pg.GetConnectionString();

        public async Task InitializeAsync()
        {
            await _pg.GatedStartAsync();
            // Engine event-store schema first (it creates the babelstone_engine role the family read model
            // GRANTs on), then the family read-model schema — engine-before-family ordering.
            await new Babelstone.EventStore.Migrations.MigrationRunner(ConnectionString).ApplyAsync();
            await new Babelstone.Families.PersonalLoan.Application.Migrations.MigrationRunner(ConnectionString).ApplyAsync();
        }

        public async Task DisposeAsync() => await _pg.DisposeAsync();

        /// <summary>Insert the shared personal_loan rate sheet once, ignoring a duplicate. The integration
        /// tests share this one Postgres container; whichever test runs first inserts it, the rest no-op on
        /// the (product_family, effective_from) unique key.</summary>
        public async Task EnsureRateSheetAsync()
        {
            try
            {
                await new PostgresRateSheetStore(ConnectionString).InsertAsync(LoanRateSheet);
            }
            catch (DuplicateRateSheetVersionException)
            {
                // Another test already inserted the shared sheet — idempotent.
            }
        }

        /// <summary>Counts rows whose <paramref name="idColumn"/> equals <paramref name="id"/>.</summary>
        public async Task<long> CountAsync(string table, string idColumn, Guid id)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE {idColumn} = @id;", connection);
            command.Parameters.AddWithValue("id", id);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        // One sheet pricing (cp_pt_general_12m, standard) at a flat 600 bps TAN across all principals,
        // for the personal_loan family (ProductFamily must match PersonalLoanConstitutionService's
        // Family.FamilyName resolve).
        private static RateSheet LoanRateSheet => new(
            RateSheetVersionId: "rs-loans-2026.1",
            ProductFamily: "personal_loan",
            PackVersion: "pt.2026.1",
            EffectiveFrom: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Body: new RateSheetBody
            {
                Products = new Dictionary<string, Dictionary<string, RoleRates>>
                {
                    [ProductId] = new()
                    {
                        [Role] = new RoleRates { Bands = [new RateBand(0L, null, 600)] },
                    },
                },
            },
            ApprovedBy: "alm@bank.pt",
            ApprovalRef: "RC-2026-001",
            PublishedBy: "deploy@bank.pt");
    }
}
