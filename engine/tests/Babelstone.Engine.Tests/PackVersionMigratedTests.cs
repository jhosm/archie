using Babelstone.Engine;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// The engine-declared <see cref="PackVersionMigrated"/> cross-cutting event (ADR-PC-009 §P3,
/// event-store §4.1). In plain English: a deposit is locked for life to the pack it was opened under,
/// and the only sanctioned way to move it to a newer pack is an explicit, audited operator migration.
/// These pure tests prove the FOLD half: the event resolves through the handler registry against ANY
/// family state, and its fold is a deterministic NO-OP — because the re-pin lives on the event ENVELOPE
/// (<c>pack_version</c>), not on the projection (the durable boundary that the envelope re-pin produces
/// is proved in <c>PackVersionMigratedReplayIntegrationTests</c>). Pure, no I/O — default CI lane.
/// </summary>
public sealed class PackVersionMigratedTests
{
    // A registry built exactly the way a family builds its own: the family's own handlers PLUS the
    // engine-declared cross-cutting registrations spliced in for that family's state (the
    // CrossCuttingEventRegistrations.For<TState>() seam). Here the family-agnostic CounterState stands
    // in for any family's projection state, proving the generic fold is genuinely state-agnostic.
    private static readonly HandlerRegistry Registry = new(
    [
        .. new CounterFamilyModule().Handlers,
        .. CrossCuttingEventRegistrations.For<CounterState>(),
    ]);

    [Fact]
    public void Registry_resolves_the_cross_cutting_event_type_under_the_operations_prefix()
    {
        // event-store §4.3: a family-agnostic engine-declared event takes the synthetic `operations`
        // aggregate_type, so the stored event_type is `operations.PackVersionMigrated` (no family prefix).
        Assert.True(Registry.TryResolve("operations.PackVersionMigrated", out var handler));
        Assert.NotNull(handler);
    }

    [Fact]
    public void The_fold_is_a_no_op_the_pin_lives_on_the_envelope_not_the_projection()
    {
        // ADR-PC-009 §Decision/§P1/§P3: the pin is a per-EVENT ENVELOPE fact; re-pinning is achieved by
        // the append stamping `to_pack_version` onto the envelope, NOT by mutating the projection.
        // Writing the new pin onto state would re-introduce the explicitly-rejected projection-column
        // pin (§B). So the fold must leave the projection unchanged.
        var handler = new PackVersionMigratedHandler<CounterState>();
        var before = new CounterState(42);

        var after = handler.Apply(before, Migration());

        Assert.Equal(before, after.NewState);          // state unchanged — the pin is not on the projection
        Assert.Empty(after.PendingEffects);            // no scheduled side effects
    }

    [Fact]
    public void Folding_a_migration_in_the_middle_of_a_stream_leaves_the_running_state_untouched()
    {
        // A migration appended mid-life must not perturb the family's accumulated state — only the
        // envelope pin moves. Fold a counter through an increment, a migration, and another increment:
        // the migration contributes nothing, so the total is 10 + 5, exactly as without it.
        var sim = new SimulationRuntime<CounterState>(
            store: null!, handlers: Registry, serializer: new JsonEventSerializer(),
            seedState: () => new CounterState(0));

        DomainEvent[] withMigration = [new Incremented(10), Migration(), new Incremented(5)];
        DomainEvent[] withoutMigration = [new Incremented(10), new Incremented(5)];

        var migrated = sim.ProjectFromScratch(withMigration);
        var plain = sim.ProjectFromScratch(withoutMigration);

        Assert.Equal(15, migrated.Total);
        Assert.Equal(plain, migrated);                 // the migration event is a no-op on the projection
    }

    [Fact]
    public void Folding_the_same_migration_fixture_twice_is_byte_identical_deterministic()
    {
        // REPLAY_PIN_PER_EVENT / DETERMINISM_GATE (ADR-PC-009, ADR-PC-010 §P5): the fold is pure (no
        // clock, no I/O, no randomness), so a stream carrying a migration replays to identical state.
        var sim = new SimulationRuntime<CounterState>(
            store: null!, handlers: Registry, serializer: new JsonEventSerializer(),
            seedState: () => new CounterState(0));
        var serializer = new JsonStateSerializer<CounterState>();

        DomainEvent[] fixture = [new Incremented(7), Migration(), new Incremented(3), Migration()];

        var first = serializer.Serialize(sim.ProjectFromScratch(fixture));
        var second = serializer.Serialize(sim.ProjectFromScratch(fixture));

        Assert.Equal(first, second);                   // byte-identical projection across runs
    }

    [Fact]
    public void Migration_is_a_lifecycle_boundary_so_a_snapshot_fires_at_the_re_pin()
    {
        // ADR-PC-003 §P2 / event-store §8.1: the re-pin is a natural point where the instance's state
        // (now governed by a new pack from here forward) is interpretable on its own, so it is marked a
        // lifecycle boundary — the engine ORs this into the per-append snapshot trigger.
        Assert.True(Migration().IsLifecycleBoundary);
    }

    private static PackVersionMigrated Migration() => new(
        InstanceId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        FromPackVersion: "pt.2026.1",
        ToPackVersion: "pt.2027.1",
        MigrationId: "mig-2027-rate-change-001",
        OperatorActor: "operator:regulatory-ops");
}
