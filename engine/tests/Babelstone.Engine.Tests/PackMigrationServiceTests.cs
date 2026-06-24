using Babelstone.Engine;
using Babelstone.Engine.Hosting;
using Babelstone.EventStore;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// The operator pack-migration write-path (<see cref="PackMigrationService{TState}"/>, ADR-PC-009 §P3)
/// proven FAMILY-AGNOSTIC: the service now lives in the engine spine and runs over ANY family projection
/// state. In plain English — the preview/re-pin/idempotency mechanics carry no term-deposit domain logic,
/// so this exercises them against the family-agnostic <c>CounterState</c> stand-in, proving the move down
/// into the engine (<c>ENGINE_FAMILY_AGNOSTIC</c>) is sound: any family that closes the generic gets the
/// same behaviour the term-deposit integration test proves end-to-end.
/// </summary>
/// <remarks>
/// The durable end-to-end against a real constituted term-deposit lives in the family's
/// <c>PackMigrationIntegrationTests</c>; this proves the SAME service over a different state, so the
/// generic carries no hidden family coupling.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PackMigrationServiceTests(EngineFixture fixture)
    : IClassFixture<EngineFixture>
{
    private const string FromPack = "pt.2026.1";
    private const string ToPack = "pt.2027.1";

    private static readonly HandlerRegistry RegistryWithCrossCutting = new(
    [
        .. new CounterFamilyModule().Handlers,
        .. CrossCuttingEventRegistrations.For<CounterState>(),
    ]);

    private AggregateRuntime<CounterState> Runtime() => new(
        fixture.Store, new EventStoreSink(fixture.Store), RegistryWithCrossCutting, fixture.Serializer,
        new NullPiiProtector(), new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
        () => new CounterState(0));

    private PackMigrationService<CounterState> Service() => new(Runtime(), fixture.Store, "counter");

    private PackMigrationService<CounterState> CappedService(int cap) =>
        new(Runtime(), fixture.Store, "counter", cap);

    private static AppendContext ContextPinned(string packVersion) => new(
        Family: "counter",
        PackVersion: packVersion,
        SchemaVersion: "counter@2026.1",
        Actor: "test",
        ValidTime: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private async Task<Guid> SeedInstanceAsync(string packVersion)
    {
        var streamId = Guid.NewGuid();
        await Runtime().AppendAsync(streamId, -1, [new Incremented(5)], ContextPinned(packVersion));
        return streamId;
    }

    [Fact]
    public async Task Preview_matches_only_instances_currently_on_the_from_version_and_emits_nothing()
    {
        var onFrom = await SeedInstanceAsync(FromPack);
        var onOther = await SeedInstanceAsync(ToPack); // already on the target — must NOT match
        var absent = Guid.NewGuid();                   // no events — must NOT match

        var matched = await Service().PreviewAsync(FromPack, [onFrom, onOther, absent]);

        Assert.Equal([onFrom], matched);
        // Preview is side-effect-free: the matched stream still has exactly its one seeded event.
        Assert.Single(await LoadEnvelopesAsync(onFrom));
    }

    [Fact]
    public async Task Migrate_re_pins_the_matched_instance_on_the_envelope_from_the_migration_forward()
    {
        var instanceId = await SeedInstanceAsync(FromPack);

        var migrated = await Service().MigrateAsync(
            FromPack, ToPack, [instanceId], "mig-svc-001", "operator:regulatory-ops",
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal([instanceId], migrated);

        var envelopes = await LoadEnvelopesAsync(instanceId);
        Assert.Equal(2, envelopes.Count);
        Assert.Equal(FromPack, envelopes[0].PackVersion);                 // pre-migration history stays old
        Assert.Equal(ToPack, envelopes[1].PackVersion);                   // the migration carries the new pin
        Assert.Equal("operations.PackVersionMigrated", envelopes[1].EventType);
    }

    [Fact]
    public async Task Migrate_is_idempotent_a_re_run_finds_the_instance_already_re_pinned_and_appends_nothing()
    {
        var instanceId = await SeedInstanceAsync(FromPack);

        await Service().MigrateAsync(
            FromPack, ToPack, [instanceId], "mig-svc-idem", "operator:regulatory-ops",
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var afterFirst = (await LoadEnvelopesAsync(instanceId)).Count;

        // The instance is no longer on the from-version, so the current-pin guard SKIPS it: no second event.
        var second = await Service().MigrateAsync(
            FromPack, ToPack, [instanceId], "mig-svc-idem", "operator:regulatory-ops",
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Empty(second);
        Assert.Equal(afterFirst, (await LoadEnvelopesAsync(instanceId)).Count);
    }

    [Fact]
    public async Task Migrate_skips_an_instance_that_is_not_on_the_from_version()
    {
        var onTarget = await SeedInstanceAsync(ToPack); // never on the from-version

        var migrated = await Service().MigrateAsync(
            FromPack, ToPack, [onTarget], "mig-svc-skip", "operator:regulatory-ops",
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Empty(migrated);
        Assert.Single(await LoadEnvelopesAsync(onTarget)); // untouched
    }

    [Fact]
    public async Task Preview_rejects_a_selection_over_the_cap_before_reading_a_single_head()
    {
        // Two ids, cap of 1: the cap guard trips on the SELECTED count BEFORE any head read — the ids need
        // not even exist (no events seeded), proving the check is pre-read structural plumbing.
        var capped = CappedService(cap: 1);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

        var ex = await Assert.ThrowsAsync<PackMigrationCapExceededException>(
            () => capped.PreviewAsync(FromPack, ids));

        Assert.Equal("counter", ex.ProductFamily);
        Assert.Equal(2, ex.SelectedCount);
        Assert.Equal(1, ex.Cap);
    }

    [Fact]
    public async Task Migrate_rejects_a_selection_over_the_cap_and_appends_nothing()
    {
        var capped = CappedService(cap: 1);
        var onFrom = await SeedInstanceAsync(FromPack);
        var second = await SeedInstanceAsync(FromPack);
        var before = (await LoadEnvelopesAsync(onFrom)).Count;

        await Assert.ThrowsAsync<PackMigrationCapExceededException>(
            () => capped.MigrateAsync(
                FromPack, ToPack, [onFrom, second], "mig-over-cap", "operator:regulatory-ops",
                new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        // No PackVersionMigrated appended — the cap refuses BEFORE the per-instance loop.
        Assert.Equal(before, (await LoadEnvelopesAsync(onFrom)).Count);
    }

    [Fact]
    public async Task A_selection_exactly_at_the_cap_is_allowed()
    {
        // The cap is a ceiling, not a strict-less-than: exactly cap instances proceed.
        var capped = CappedService(cap: 2);
        var onFrom = await SeedInstanceAsync(FromPack);
        var alsoFrom = await SeedInstanceAsync(FromPack);

        var matched = await capped.PreviewAsync(FromPack, [onFrom, alsoFrom]);

        Assert.Equal(2, matched.Count);
    }

    private async Task<List<EventEnvelope>> LoadEnvelopesAsync(Guid streamId)
    {
        var envelopes = new List<EventEnvelope>();
        await foreach (var envelope in fixture.Store.LoadAsync(streamId, fromSequence: 0))
        {
            envelopes.Add(envelope);
        }

        return envelopes;
    }
}
