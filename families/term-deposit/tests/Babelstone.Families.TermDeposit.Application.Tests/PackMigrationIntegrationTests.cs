using Babelstone.Engine;
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
    private static (AggregateRuntime<DepositPosition> Runtime, TermDepositConstitutionService Service, PackMigrationService Migration)
        Compose(string connectionString)
    {
        var store = new PostgresEventStore(connectionString);
        var runtime = new AggregateRuntime<DepositPosition>(
            store, new EventStoreSink(store), TermDepositFamilyModule.Registry(),
            new JsonEventSerializer(), new NullPiiProtector(), TimeProvider.System,
            () => DepositPosition.Empty);
        var service = new TermDepositConstitutionService(
            runtime, new PostgresRateSheetStore(connectionString), new RecordingSettlementPort(),
            SkeletonPack.LoadPt2026(), dayCountPrimitive: "act_360", withholdingPrimitive: "irs_juros");
        var migration = new PackMigrationService(runtime, store);
        return (runtime, service, migration);
    }
}
