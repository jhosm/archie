using Babelstone.Engine;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// The remaining store-only engine-declared cross-cutting events (event-store §4.1, ADR-PC-009 §P3): the
/// schema twin of pack migration (<see cref="SchemaVersionMigrated"/>) and the two operational holds a
/// production engine must record — a legal hold (<see cref="FundsHeld"/>) and a compliance freeze
/// (<see cref="AccountFrozen"/>). In plain English: alongside re-pinning an instance to a newer regulatory
/// pack, the engine also re-pins it to a newer family schema, and it records when a court or a
/// compliance process places a hold/freeze on an instance — all as cross-cutting facts that apply to any
/// product family. These pure tests prove the FOLD half: each event resolves through the handler registry
/// against ANY family state, and its fold is a deterministic NO-OP — these are STORE-ONLY v1 audit facts
/// (ADR-IC-017 §P1), so they neither carry a governed schema nor mutate the projection (no Held/Frozen
/// lifecycle state, no operation-blocking guard — that would be a new ADR-PC decision, ADR-PC-033 §P1).
/// Pure, no I/O — default CI lane. Mirrors <see cref="PackVersionMigratedTests"/>.
/// </summary>
public sealed class CrossCuttingEventsTests
{
    // A registry built exactly the way a family builds its own: the family's own handlers PLUS the
    // engine-declared cross-cutting registrations spliced in for that family's state (the
    // CrossCuttingEventRegistrations.For<TState>() seam). The family-agnostic CounterState stands in for
    // any family's projection state, proving the generic folds are genuinely state-agnostic.
    private static readonly HandlerRegistry Registry = new(
    [
        .. new CounterFamilyModule().Handlers,
        .. CrossCuttingEventRegistrations.For<CounterState>(),
    ]);

    [Theory]
    [InlineData("operations.SchemaVersionMigrated")]
    [InlineData("operations.FundsHeld")]
    [InlineData("operations.AccountFrozen")]
    public void Registry_resolves_each_cross_cutting_event_type_under_the_operations_prefix(string eventType)
    {
        // event-store §4.3: a family-agnostic engine-declared event takes the synthetic `operations`
        // aggregate_type, so the stored event_type is `operations.<EventName>` (no family prefix).
        Assert.True(Registry.TryResolve(eventType, out var handler));
        Assert.NotNull(handler);
    }

    // ── SchemaVersionMigrated ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Schema_migration_fold_is_a_no_op_the_pin_lives_on_the_envelope_not_the_projection()
    {
        // ADR-PC-009 §Decision/§P1/§P3: the schema pin is a per-EVENT ENVELOPE fact; re-pinning is
        // achieved by the append stamping `to_schema_version` onto the envelope, NOT by mutating the
        // projection. Writing the new pin onto state would re-introduce the explicitly-rejected
        // projection-column pin (§B). So the fold must leave the projection unchanged.
        var handler = new SchemaVersionMigratedHandler<CounterState>();
        var before = new CounterState(42);

        var after = handler.Apply(before, SchemaMigration());

        Assert.Equal(before, after.NewState);          // state unchanged — the pin is not on the projection
        Assert.Empty(after.PendingEffects);            // no scheduled side effects
    }

    [Fact]
    public void Schema_migration_is_a_lifecycle_boundary_so_a_snapshot_fires_at_the_re_pin()
    {
        // ADR-PC-003 §P2 / event-store §8.1: the re-pin is a natural point where the instance's state
        // (now interpreted under a new schema from here forward) is interpretable on its own, so it is
        // marked a lifecycle boundary — the engine ORs this into the per-append snapshot trigger.
        Assert.True(SchemaMigration().IsLifecycleBoundary);
    }

    [Fact]
    public void Folding_a_schema_migration_mid_stream_leaves_the_running_state_untouched()
    {
        // A migration appended mid-life must not perturb the family's accumulated state — only the
        // envelope pin moves. Fold a counter through an increment, a migration, and another increment:
        // the migration contributes nothing, so the total is 10 + 5, exactly as without it.
        var sim = NewSim();

        DomainEvent[] withMigration = [new Incremented(10), SchemaMigration(), new Incremented(5)];
        DomainEvent[] withoutMigration = [new Incremented(10), new Incremented(5)];

        var migrated = sim.ProjectFromScratch(withMigration);
        var plain = sim.ProjectFromScratch(withoutMigration);

        Assert.Equal(15, migrated.Total);
        Assert.Equal(plain, migrated);                 // the migration event is a no-op on the projection
    }

    // ── FundsHeld ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Funds_held_fold_is_a_store_only_no_op_no_held_lifecycle_state()
    {
        // ADR-IC-017 §P1 / event-store §4.1: a legal hold is a v1 STORE-ONLY audit fact. The fold adds no
        // Held lifecycle state and constrains no operation — any operation-blocking / available-balance
        // semantics would be a new ADR-PC decision (ADR-PC-033 §P1's hold ledger), not this fact.
        var handler = new FundsHeldHandler<CounterState>();
        var before = new CounterState(42);

        var after = handler.Apply(before, Hold());

        Assert.Equal(before, after.NewState);          // store-only — projection unchanged
        Assert.Empty(after.PendingEffects);
    }

    [Fact]
    public void Funds_held_is_not_a_lifecycle_boundary_a_hold_is_an_audit_fact_not_a_state_transition()
    {
        // A store-only audit fact is not a snapshot boundary: it does not move the instance to a new
        // interpretable state (ADR-PC-003 §P2). The base DomainEvent default (false) must stand.
        Assert.False(Hold().IsLifecycleBoundary);
    }

    [Fact]
    public void Funds_held_carries_the_held_amount_as_integer_cents_money_never_a_float()
    {
        // event-store §4.1 `held_amount_cents` / ADR-PC-010 §P1: the held amount is integer-cents Money,
        // never a float. The fact records it verbatim — the fold neither rounds nor recomputes it.
        var hold = Hold();
        Assert.Equal(150_000L, hold.HeldAmount.Cents);
        Assert.Null(hold.HoldExpiresAt);               // an open-ended hold carries no expiry
    }

    [Fact]
    public void Folding_a_hold_mid_stream_leaves_the_running_state_untouched()
    {
        var sim = NewSim();

        DomainEvent[] withHold = [new Incremented(10), Hold(), new Incremented(5)];
        DomainEvent[] withoutHold = [new Incremented(10), new Incremented(5)];

        Assert.Equal(sim.ProjectFromScratch(withoutHold), sim.ProjectFromScratch(withHold));
    }

    // ── AccountFrozen ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Account_frozen_fold_is_a_store_only_no_op_no_frozen_lifecycle_state()
    {
        // ADR-IC-017 §P1 / event-store §4.1: a compliance freeze is a v1 STORE-ONLY audit fact. The fold
        // adds no Frozen lifecycle state and blocks no operation (operation-constraining semantics are a
        // new ADR-PC decision, not this fact).
        var handler = new AccountFrozenHandler<CounterState>();
        var before = new CounterState(42);

        var after = handler.Apply(before, Freeze());

        Assert.Equal(before, after.NewState);          // store-only — projection unchanged
        Assert.Empty(after.PendingEffects);
    }

    [Fact]
    public void Account_frozen_is_not_a_lifecycle_boundary_a_freeze_is_an_audit_fact_not_a_state_transition()
    {
        Assert.False(Freeze().IsLifecycleBoundary);
    }

    [Fact]
    public void Folding_a_freeze_mid_stream_leaves_the_running_state_untouched()
    {
        var sim = NewSim();

        DomainEvent[] withFreeze = [new Incremented(10), Freeze(), new Incremented(5)];
        DomainEvent[] withoutFreeze = [new Incremented(10), new Incremented(5)];

        Assert.Equal(sim.ProjectFromScratch(withoutFreeze), sim.ProjectFromScratch(withFreeze));
    }

    // ── Determinism (all three together) ─────────────────────────────────────────────────────────────

    [Fact]
    public void Folding_a_fixture_carrying_all_three_events_twice_is_byte_identical_deterministic()
    {
        // DETERMINISM_GATE (ADR-PC-010 §P5): each fold is pure (no clock, no I/O, no randomness), so a
        // stream carrying a schema migration, a hold, and a freeze replays to byte-identical state.
        var sim = NewSim();
        var serializer = new JsonStateSerializer<CounterState>();

        DomainEvent[] fixture =
        [
            new Incremented(7), SchemaMigration(), Hold(), new Incremented(3), Freeze(), SchemaMigration(),
        ];

        var first = serializer.Serialize(sim.ProjectFromScratch(fixture));
        var second = serializer.Serialize(sim.ProjectFromScratch(fixture));

        Assert.Equal(first, second);                   // byte-identical projection across runs
        // None of the store-only facts perturb the running total: only the two increments contribute.
        Assert.Equal(10, sim.ProjectFromScratch(fixture).Total);
    }

    private static SimulationRuntime<CounterState> NewSim() => new(
        store: null!, handlers: Registry, serializer: new JsonEventSerializer(),
        seedState: () => new CounterState(0));

    private static SchemaVersionMigrated SchemaMigration() => new(
        InstanceId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        FromSchemaVersion: "term_deposit@2026.1",
        ToSchemaVersion: "term_deposit@2027.1",
        MigrationId: "schema-mig-2027-consolidation-001",
        OperatorActor: "operator:engineering-ops");

    private static FundsHeld Hold() => new(
        InstanceId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
        HoldId: "hold-court-2026-00042",
        HeldAmount: new Money(150_000),
        LegalReference: "proc:tribunal-lisboa-2026/1234");

    private static AccountFrozen Freeze() => new(
        InstanceId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
        FreezeId: "freeze-aml-2026-00007",
        FreezeReason: "AML_SCREENING",
        ComplianceActor: "operator:compliance-ops");
}
