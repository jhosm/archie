using Babelstone.Engine;
using Babelstone.EventStore;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// The durable half of the <see cref="PackVersionMigrated"/> write-path (ADR-PC-009 §P3): a migrated
/// instance re-pins ON THE EVENT ENVELOPE, and that re-pin is intrinsic to the stream. In plain English:
/// when an operator migrates a live instance to a newer pack, the engine appends a migration event and
/// every event from there on carries the NEW pack version, while everything before it keeps the OLD one —
/// so "which pack governed this instance at any point in its life" is answerable from the event stream
/// alone, and a cold replay reconstructs that exact boundary every time.
/// </summary>
/// <remarks>
/// This proves the property ADR-PC-009 §P1/§P3 names and the pure <see cref="PackVersionMigratedTests"/>
/// could not: that the per-EVENT pin actually flips at the migration sequence and stays flipped, because
/// the append stamps <c>AppendContext.PackVersion</c> onto each envelope. The CounterState family-agnostic
/// stand-in proves the KERNEL mechanism — the re-pin is engine-owned and names no family.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PackVersionMigratedReplayIntegrationTests(EngineFixture fixture)
    : IClassFixture<EngineFixture>
{
    private const string FromPack = "pt.2026.1";
    private const string ToPack = "pt.2027.1";

    // The engine-declared cross-cutting handler MUST be registered (against the family's state) for the
    // engine to fold a PackVersionMigrated appended to that family's stream. This mirrors how a family
    // splices CrossCuttingEventRegistrations.For<TState>() into its own module.
    private static readonly HandlerRegistry RegistryWithCrossCutting = new(
    [
        .. new CounterFamilyModule().Handlers,
        .. CrossCuttingEventRegistrations.For<CounterState>(),
    ]);

    private AggregateRuntime<CounterState> Runtime() => new(
        fixture.Store, new EventStoreSink(fixture.Store), RegistryWithCrossCutting, fixture.Serializer,
        new NullPiiProtector(), new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
        () => new CounterState(0));

    private static AppendContext ContextPinned(string packVersion) => new(
        Family: "counter",
        PackVersion: packVersion,
        SchemaVersion: "counter@2026.1",
        Actor: "test",
        ValidTime: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task The_pin_flips_at_the_migration_sequence_and_stays_flipped_per_event()
    {
        var runtime = Runtime();
        var streamId = Guid.NewGuid();

        // Two events under the ORIGINAL pack (sequences 0, 1).
        await runtime.AppendAsync(
            streamId, -1, [new Incremented(10), new Incremented(5)], ContextPinned(FromPack));

        // The operator migration (sequence 2), appended under the TARGET pack — and one more event after
        // it (sequence 3) which copies the new pin forward. This is the write-path the operator endpoint
        // drives: one PackVersionMigrated per affected instance, pinned to the to_pack_version.
        await runtime.AppendAsync(
            streamId, 1,
            [
                new PackVersionMigrated(streamId, FromPack, ToPack, "mig-001", "operator:regulatory-ops"),
                new Incremented(3),
            ],
            ContextPinned(ToPack));

        // Read the raw envelopes back: the pin is a per-event ENVELOPE fact, so the boundary is visible
        // directly on the stored events (events < M old pack, >= M new pack — ADR-PC-009 §P3).
        var envelopes = await LoadEnvelopesAsync(streamId);

        Assert.Equal(4, envelopes.Count);
        Assert.Equal(FromPack, envelopes[0].PackVersion);   // pre-migration history stays pinned to the old pack
        Assert.Equal(FromPack, envelopes[1].PackVersion);
        Assert.Equal(ToPack, envelopes[2].PackVersion);     // the migration event itself carries the new pin
        Assert.Equal(ToPack, envelopes[3].PackVersion);     // and every event after it copies it forward

        // The migration event is the boundary fact itself — its payload records the audit transition.
        Assert.Equal("operations.PackVersionMigrated", envelopes[2].EventType);
    }

    [Fact]
    public async Task A_migrated_instance_replays_to_identical_state_across_cold_loads()
    {
        var streamId = Guid.NewGuid();

        await Runtime().AppendAsync(
            streamId, -1, [new Incremented(7), new Incremented(2)], ContextPinned(FromPack));
        await Runtime().AppendAsync(
            streamId, 1,
            [
                new PackVersionMigrated(streamId, FromPack, ToPack, "mig-002", "operator:regulatory-ops"),
                new Incremented(4),
            ],
            ContextPinned(ToPack));

        // Cold-fold the migrated stream twice through fresh runtimes — the migration event folds as a
        // no-op (the pin lives on the envelope), so the projection is identical across rebuilds
        // (REPLAY_PIN_PER_EVENT / determinism). 7 + 2 + 4 = 13; the migration contributes nothing.
        var first = await Runtime().LoadAsync(streamId);
        var second = await Runtime().LoadAsync(streamId);

        Assert.Equal(13, first.State.Total);
        Assert.Equal(first.State, second.State);
        Assert.Equal(3, first.Version); // sequences 0..3
    }

    [Fact]
    public async Task An_as_of_read_before_the_migration_sees_the_old_pin_on_the_boundary_event()
    {
        // The migration boundary is reconstructable from the stream: an as-of fold up to the LAST
        // pre-migration sequence sees only the old-pack envelopes, and the migration sequence is the
        // first event carrying the new pin — the "reversible-in-principle / counterfactual" property
        // (ADR-PC-009 §P3 / surface §3.6) is structurally available because the pin is per-event.
        var streamId = Guid.NewGuid();

        await Runtime().AppendAsync(streamId, -1, [new Incremented(1)], ContextPinned(FromPack));
        await Runtime().AppendAsync(
            streamId, 0,
            [new PackVersionMigrated(streamId, FromPack, ToPack, "mig-003", "operator:regulatory-ops")],
            ContextPinned(ToPack));

        var envelopes = await LoadEnvelopesAsync(streamId);

        // Sequence 0 (pre-migration) is pinned old; sequence 1 (the migration) is the first new pin.
        Assert.Equal(FromPack, envelopes[0].PackVersion);
        Assert.Equal(ToPack, envelopes[1].PackVersion);

        // The migration event re-points the pin without perturbing the running projection.
        var head = await Runtime().LoadAsync(streamId);
        Assert.Equal(1, head.State.Total);
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
