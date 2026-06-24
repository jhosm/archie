using Babelstone.Engine;
using Babelstone.Engine.Hosting;
using Babelstone.EventStore;
using Babelstone.RateSheets;
using Npgsql;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// The operator pack-migration write-path end-to-end (ADR-PC-009 §P3, surface §3.6) against real
/// PostgreSQL. In plain English: open a deposit (pinned for life to pt.2026.1), then run an operator
/// migration to pt.2027.1 — and prove the deposit's events flip to the new pack from the migration
/// forward while its earlier history stays on the old pack, exactly the "pinned for life, except by
/// explicit audited migration" guarantee ADR-PC-009 makes executable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PackMigrationIntegrationTests(ConstitutionFixture fixture)
    : IClassFixture<ConstitutionFixture>
{
    private const string FromPack = "pt.2026.1";
    private const string ToPack = "pt.2027.1";

    // A generous test cap — large enough that the existing small populations never trip it, so the
    // cap-unrelated tests stay unaffected. The cap-specific tests set a tiny cap explicitly.
    private const int TestCap = 10_000;

    [Fact]
    public async Task Preview_reports_the_matched_set_without_emitting_any_event()
    {
        await fixture.EnsureRateSheetAsync(SharedSheet);
        var (_, service, migration) = Compose(fixture.ConnectionString);
        var depositId = await ConstituteAsync(service);

        var before = await fixture.CountAsync("events", "stream_id", depositId);

        // Preview matches the deposit (it is pinned to pt.2026.1) but appends NOTHING — the
        // pre-emission confirmation step (ADR-PC-009 Residual-risks: previewable before emission).
        var matched = await migration.PreviewAsync(FromPack, [depositId]);

        Assert.Contains(depositId, matched);
        Assert.Equal(before, await fixture.CountAsync("events", "stream_id", depositId)); // no new events
    }

    [Fact]
    public async Task Migration_re_pins_the_instance_on_the_envelope_from_the_migration_forward()
    {
        await fixture.EnsureRateSheetAsync(SharedSheet);
        var (_, service, migration) = Compose(fixture.ConnectionString);
        var depositId = await ConstituteAsync(service);

        // One PackVersionMigrated appended to the deposit, pinned to the target pack.
        var migrated = await migration.MigrateAsync(
            FromPack, ToPack, [depositId], "mig-2027-rate-change", "operator:regulatory-ops",
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal([depositId], migrated);

        // The re-pin lives on the ENVELOPE (ADR-PC-009 §P1/§P3): read the stored pack_version per
        // sequence. Sequence 0 (DepositConstituted) stays on the OLD pack; the migration event (the new
        // head) carries the NEW pack.
        var pins = await PackVersionsBySequenceAsync(depositId);
        Assert.Equal(FromPack, pins[0]);                          // pre-migration history pinned old
        Assert.Equal(ToPack, pins[^1]);                           // the migration (and onward) pinned new

        // The migration event is the boundary fact itself: event_type is the engine-declared
        // operations.PackVersionMigrated (no family prefix — event-store §4.3). It is STORE-ONLY by
        // construction — the production host wires the real AvroSchemaCatalog, which has no .avsc for it,
        // so the fail-closed relay writes no outbox row (ADR-IC-017 §P1). That biconditional
        // (catalogued ⇔ on the bus) is authoritatively proved by the engine's
        // CatalogGatedRelayReverseOrphanTests; here we assert the stored event_type, which is the
        // write-path's own contract.
        var head = await HeadEventTypeAsync(depositId);
        Assert.Equal("operations.PackVersionMigrated", head);
    }

    [Fact]
    public async Task Migration_is_idempotent_re_running_the_same_migration_appends_no_second_event()
    {
        await fixture.EnsureRateSheetAsync(SharedSheet);
        var (_, service, migration) = Compose(fixture.ConnectionString);
        var depositId = await ConstituteAsync(service);

        await migration.MigrateAsync(
            FromPack, ToPack, [depositId], "mig-idem", "operator:regulatory-ops",
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var afterFirst = await fixture.CountAsync("events", "stream_id", depositId);

        // Re-running the SAME migration: the instance is no longer on the from-version, so it is SKIPPED
        // (the second run finds it already at pt.2027.1) — no second PackVersionMigrated. Idempotent on
        // (migration_id, instance) AND on the current-pin guard (ADR-PC-009 §P3).
        var second = await migration.MigrateAsync(
            FromPack, ToPack, [depositId], "mig-idem", "operator:regulatory-ops",
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Empty(second); // nothing left to migrate
        Assert.Equal(afterFirst, await fixture.CountAsync("events", "stream_id", depositId));
    }

    [Fact]
    public async Task A_migrated_instance_folds_to_the_same_position_the_migration_is_a_no_op_on_state()
    {
        await fixture.EnsureRateSheetAsync(SharedSheet);
        var (runtime, service, migration) = Compose(fixture.ConnectionString);
        var depositId = await ConstituteAsync(service);

        var beforeMigration = await runtime.LoadAsync(depositId);

        await migration.MigrateAsync(
            FromPack, ToPack, [depositId], "mig-noop", "operator:regulatory-ops",
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        // The projection is unchanged except the head version advanced by the migration event — the fold
        // is a no-op (the pin is on the envelope, not the position). Lifecycle, money, ids all identical.
        var afterMigration = await runtime.LoadAsync(depositId);
        Assert.Equal(beforeMigration.State, afterMigration.State);
        Assert.Equal(beforeMigration.Version + 1, afterMigration.Version);
    }

    // ---- Predicate instance_filter over the read model (bd babelstone-7giq, surface §3.6) ----

    [Fact]
    public async Task ListActiveStreamIds_returns_only_the_live_lifecycle_rows_in_a_stable_order()
    {
        var readModel = new PostgresDepositReadModelStore(fixture.ConnectionString);
        await readModel.TruncateAsync();

        var active1 = await UpsertRowAsync(readModel, nameof(DepositLifecycle.Active));
        var active2 = await UpsertRowAsync(readModel, nameof(DepositLifecycle.Active));
        var matured = await UpsertRowAsync(readModel, nameof(DepositLifecycle.Matured));
        var failed = await UpsertRowAsync(readModel, nameof(DepositLifecycle.Failed));
        // Lower-case "active" must NOT match — currently_active binds the case-sensitive enum literal.
        var miscased = await UpsertRowAsync(readModel, "active");

        var ids = await readModel.ListActiveStreamIdsAsync(TestCap);

        Assert.Contains(active1, ids);
        Assert.Contains(active2, ids);
        Assert.DoesNotContain(matured, ids);
        Assert.DoesNotContain(failed, ids);
        Assert.DoesNotContain(miscased, ids);
        // Stable order (ORDER BY stream_id) — a second read yields the identical sequence. We assert
        // STABILITY rather than a specific order, since Postgres uuid ordering and .NET Guid ordering
        // differ and the contract is determinism, not a particular permutation.
        Assert.Equal(ids, await readModel.ListActiveStreamIdsAsync(TestCap));
    }

    [Fact]
    public async Task Resolver_resolves_the_active_population_and_rejects_unsupported_predicates()
    {
        var readModel = new PostgresDepositReadModelStore(fixture.ConnectionString);
        await readModel.TruncateAsync();
        var active = await UpsertRowAsync(readModel, nameof(DepositLifecycle.Active));
        var matured = await UpsertRowAsync(readModel, nameof(DepositLifecycle.Matured));

        var resolver = new DepositInstanceFilterResolver(readModel, "term_deposit", TestCap);

        var resolved = await resolver.ResolveAsync(new InstanceFilter("term_deposit", true));
        Assert.Contains(active, resolved);
        Assert.DoesNotContain(matured, resolved);

        // Internal invariants: a wrong family or currently_active=false is a wiring bug, not operator input
        // (the endpoint screens both first), so the resolver fails loud rather than mis-resolving.
        await Assert.ThrowsAsync<ArgumentException>(
            () => resolver.ResolveAsync(new InstanceFilter("personal_loan", true)));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => resolver.ResolveAsync(new InstanceFilter("term_deposit", false)));
    }

    [Fact]
    public async Task Predicate_migrates_the_active_on_from_population_excluding_terminal_and_already_migrated()
    {
        await fixture.EnsureRateSheetAsync(SharedSheet);
        var (_, service, migration) = Compose(fixture.ConnectionString);
        var readModel = new PostgresDepositReadModelStore(fixture.ConnectionString);
        await readModel.TruncateAsync();

        // A mixed population, all constituted on pt.2026.1 (real event streams the migration reads heads of):
        //   active1, active2  — live, on FROM            → the predicate selects AND the head-pin keeps them
        //   terminal          — read model says Matured  → the predicate EXCLUDES it (not live)
        //   alreadyMigrated   — live in the read model, but its events are pre-migrated to TO
        //                       → the predicate selects it, the head-pin guard NARROWS it out
        var active1 = await ConstituteAsync(service);
        var active2 = await ConstituteAsync(service);
        var terminal = await ConstituteAsync(service);
        var alreadyMigrated = await ConstituteAsync(service);

        await UpsertRowForStreamAsync(readModel, active1, nameof(DepositLifecycle.Active));
        await UpsertRowForStreamAsync(readModel, active2, nameof(DepositLifecycle.Active));
        await UpsertRowForStreamAsync(readModel, terminal, nameof(DepositLifecycle.Matured));
        await UpsertRowForStreamAsync(readModel, alreadyMigrated, nameof(DepositLifecycle.Active));

        // Pre-migrate alreadyMigrated so its head is on TO before the predicate run; its read-model row
        // stays Active, so the predicate still SELECTS it — only the head-pin guard removes it.
        await migration.MigrateAsync(
            FromPack, ToPack, [alreadyMigrated], "mig-pre", "operator:regulatory-ops",
            new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        var alreadyMigratedEvents = await fixture.CountAsync("events", "stream_id", alreadyMigrated);

        // Resolve the predicate { product_family: term_deposit, currently_active: true } over the read model.
        var resolver = new DepositInstanceFilterResolver(readModel, "term_deposit", TestCap);
        var candidates = await resolver.ResolveAsync(new InstanceFilter("term_deposit", true));

        // The predicate WIDENS to the live population: the three Active rows, NOT the Matured one.
        Assert.Contains(active1, candidates);
        Assert.Contains(active2, candidates);
        Assert.Contains(alreadyMigrated, candidates);
        Assert.DoesNotContain(terminal, candidates);

        // Preview NARROWS to those still on FROM (alreadyMigrated is on TO) and emits nothing.
        var eventsBeforePreview = await fixture.CountAsync("events", "stream_id", active1);
        var preview = await migration.PreviewAsync(FromPack, candidates);
        Assert.Equal(new[] { active1, active2 }, preview.OrderBy(SortKey(active1, active2)).ToArray());
        Assert.Equal(eventsBeforePreview, await fixture.CountAsync("events", "stream_id", active1)); // side-effect-free

        // Emit: exactly the Active-and-on-FROM subset is re-pinned.
        var migrated = await migration.MigrateAsync(
            FromPack, ToPack, candidates, "mig-predicate", "operator:regulatory-ops",
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(new[] { active1, active2 }, migrated.OrderBy(SortKey(active1, active2)).ToArray());

        // active1/active2 now head on TO; terminal was never selected (still on FROM); alreadyMigrated
        // gained NO second migration event (the head-pin guard skipped it in the predicate run).
        Assert.Equal(ToPack, (await PackVersionsBySequenceAsync(active1))[^1]);
        Assert.Equal(ToPack, (await PackVersionsBySequenceAsync(active2))[^1]);
        Assert.Equal(FromPack, (await PackVersionsBySequenceAsync(terminal))[^1]);
        Assert.Equal(alreadyMigratedEvents, await fixture.CountAsync("events", "stream_id", alreadyMigrated));
    }

    // ---- Hard cap on the selected population (bd babelstone-fk7m.12, ADR-PC-009 §A2) ----

    [Fact]
    public async Task ListActiveStreamIds_caps_the_read_at_the_requested_limit()
    {
        var readModel = new PostgresDepositReadModelStore(fixture.ConnectionString);
        await readModel.TruncateAsync();

        // Five live rows, read with a LIMIT of 3 — the read returns at most the limit, never the whole
        // population (the access-path bound that stops Postgres streaming an unbounded id list back).
        for (var i = 0; i < 5; i++)
        {
            await UpsertRowAsync(readModel, nameof(DepositLifecycle.Active));
        }

        var capped = await readModel.ListActiveStreamIdsAsync(limit: 3);

        Assert.Equal(3, capped.Count);
    }

    [Fact]
    public async Task Resolver_returns_cap_plus_one_as_the_overflow_sentinel_when_the_population_exceeds_the_cap()
    {
        var readModel = new PostgresDepositReadModelStore(fixture.ConnectionString);
        await readModel.TruncateAsync();

        // Cap = 2, but four live rows: the resolver asks the store for cap+1 (= 3), so an over-cap
        // population comes back as exactly cap+1 — enough for the write-path's cap guard to detect the
        // overflow without dragging the whole population out of the read model.
        for (var i = 0; i < 4; i++)
        {
            await UpsertRowAsync(readModel, nameof(DepositLifecycle.Active));
        }

        var resolver = new DepositInstanceFilterResolver(readModel, "term_deposit", migrationCap: 2);
        var resolved = await resolver.ResolveAsync(new InstanceFilter("term_deposit", true));

        Assert.Equal(3, resolved.Count); // cap (2) + 1
    }

    [Fact]
    public async Task Predicate_over_the_cap_is_rejected_by_the_write_path_before_any_event_is_emitted()
    {
        await fixture.EnsureRateSheetAsync(SharedSheet);
        // Cap = 1, but two live deposits constituted on FROM: the predicate selects both, the write-path
        // refuses the over-cap selection in BOTH preview and emit — no PackVersionMigrated is appended.
        var (_, service, migration) = Compose(fixture.ConnectionString, migrationCap: 1);
        var readModel = new PostgresDepositReadModelStore(fixture.ConnectionString);
        await readModel.TruncateAsync();

        var active1 = await ConstituteAsync(service);
        var active2 = await ConstituteAsync(service);
        await UpsertRowForStreamAsync(readModel, active1, nameof(DepositLifecycle.Active));
        await UpsertRowForStreamAsync(readModel, active2, nameof(DepositLifecycle.Active));

        var resolver = new DepositInstanceFilterResolver(readModel, "term_deposit", migrationCap: 1);
        var candidates = await resolver.ResolveAsync(new InstanceFilter("term_deposit", true));
        Assert.Equal(2, candidates.Count); // cap (1) + 1 — the overflow sentinel

        var eventsBefore = await fixture.CountAsync("events", "stream_id", active1);

        // Preview rejects the over-cap selection (no "preview passes, emit explodes" gap)...
        var previewEx = await Assert.ThrowsAsync<PackMigrationCapExceededException>(
            () => migration.PreviewAsync(FromPack, candidates));
        Assert.Equal(2, previewEx.SelectedCount);
        Assert.Equal(1, previewEx.Cap);

        // ...and so does emit, appending nothing.
        await Assert.ThrowsAsync<PackMigrationCapExceededException>(
            () => migration.MigrateAsync(
                FromPack, ToPack, candidates, "mig-over-cap", "operator:regulatory-ops",
                new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(eventsBefore, await fixture.CountAsync("events", "stream_id", active1));
    }

    // A stable comparer so the order-insensitive assertions above read clearly: sort the matched set by
    // its position among the two known active ids (avoids depending on Postgres-vs-.NET uuid ordering).
    private static Func<Guid, int> SortKey(Guid first, Guid second)
        => id => id == first ? 0 : id == second ? 1 : 2;

    private static async Task<Guid> UpsertRowAsync(PostgresDepositReadModelStore store, string lifecycle)
    {
        var streamId = Guid.NewGuid();
        await UpsertRowForStreamAsync(store, streamId, lifecycle);
        return streamId;
    }

    private static Task UpsertRowForStreamAsync(
        PostgresDepositReadModelStore store, Guid streamId, string lifecycle)
        => store.UpsertAsync(new DepositReadModelRow(
            StreamId: streamId, Sor: "engine", PrincipalCents: 1_000_000, TanBasisPoints: 300,
            RateSheetVersionId: "pt-deposits-2026.1", ProductCode: "dpz_pt_12m_juros_venc", TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15), MaturityDate: new DateOnly(2027, 1, 15),
            InterestVariant: "AT_MATURITY", AutoRenewalPolicy: "NONE", PaymentPeriodMonths: 0,
            Lifecycle: lifecycle, AccruedGrossInterestCents: 0, WithholdingToDateCents: 0,
            NetInterestCents: 0, TotalPayoutCents: 0, CouponsPaid: 0, Detail: Array.Empty<byte>(),
            LastSequence: 0, LastUpdated: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero)));

    private static async Task<Guid> ConstituteAsync(TermDepositConstitutionService service)
    {
        var depositId = Guid.NewGuid();
        await service.ConstituteAsync(new ConstituteDepositCommand(
            DepositId: depositId, PrincipalCents: 1_000_000, ProductId: "dpz_pt_12m_juros_venc", Role: "standard",
            TermDays: 365, StartDate: new DateOnly(2026, 1, 15),
            ConstitutedAt: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
            InterestVariant: "AT_MATURITY", AutoRenewalPolicy: "NONE", FundingAccount: "PT50-DDA-001", Actor: "mcp:dev"));
        return depositId;
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

    private static RateSheet SharedSheet => TestRateSheets.MultiPriced(
        "pt-deposits-2026.1", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        ("dpz_pt_12m_juros_venc", "standard", 300),
        ("dpz_pt_12m_juros_mensal", "standard", 325),
        ("dpz_pt_12m_juros_antecip", "standard", 300));

    /// <summary>Compose the durable runtime, the constitution decider, and the operator pack-migration
    /// service over the term-deposit family. The runtime registry includes the engine-declared
    /// cross-cutting handlers (TermDepositFamilyModule.Registry() splices them in), so it folds the
    /// PackVersionMigrated the migration appends.</summary>
    private static (AggregateRuntime<DepositPosition> Runtime, TermDepositConstitutionService Service, PackMigrationService<DepositPosition> Migration)
        Compose(string connectionString, int migrationCap = TestCap)
    {
        var store = new PostgresEventStore(connectionString);
        var runtime = new AggregateRuntime<DepositPosition>(
            store, new EventStoreSink(store), TermDepositFamilyModule.Registry(),
            new JsonEventSerializer(), new NullPiiProtector(), TimeProvider.System,
            () => DepositPosition.Empty);
        var service = new TermDepositConstitutionService(
            runtime, new PostgresRateSheetStore(connectionString), new RecordingSettlementPort(),
            SkeletonPack.LoadPt2026(), dayCountPrimitive: "act_360", withholdingPrimitive: "irs_juros");
        var migration = new PackMigrationService<DepositPosition>(runtime, store, "term_deposit", migrationCap);
        return (runtime, service, migration);
    }
}
