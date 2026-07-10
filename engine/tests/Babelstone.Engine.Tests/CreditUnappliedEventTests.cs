using Babelstone.Engine;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// The undeliverable-credit cross-cutting pair (ADR-PC-043 slot 5, CreditUnappliedEvents.cs): the
/// engine-declared <see cref="CreditUnapplied"/> / <see cref="CreditReapplied"/> facts a family appends
/// when a matured payout has nowhere to land, and when a live destination later exists. In plain English:
/// if a payout cannot be delivered the money is NOT lost into a void nor swept into an anonymous pot — it
/// is held at source and recorded as a NAMED IOU to a specific beneficiary and intent, then reapplied
/// exactly once when the destination becomes receivable. These pure tests prove the FOLD half: each event
/// resolves through the handler registry against ANY family state, its fold is a deterministic NO-OP (the
/// IOU/escheat ledger is a SPINE-owned fold, never family state — so the credit is HELD AT SOURCE without
/// perturbing the family projection), and the resolution key is structurally DISTINCT from the original
/// intent (the double-pay guard's shape; the g(IntentId) derivation itself is pinned in the orchestrator's
/// SettlementReferences tests, which the engine layer must not depend on). Pure, no I/O — default CI lane.
/// Mirrors <see cref="CrossCuttingEventsTests"/>.
/// </summary>
public sealed class CreditUnappliedEventTests
{
    // A registry built exactly the way a family builds its own: the family's own handlers PLUS the
    // engine-declared cross-cutting registrations spliced in for that family's state. The
    // family-agnostic CounterState stands in for any family's projection state.
    private static readonly HandlerRegistry Registry = new(
    [
        .. new CounterFamilyModule().Handlers,
        .. CrossCuttingEventRegistrations.For<CounterState>(),
    ]);

    private static SimulationRuntime<CounterState> NewSim() => new(
        store: null!, handlers: Registry, serializer: new JsonEventSerializer(),
        seedState: () => new CounterState(0));

    [Theory]
    [InlineData("operations.CreditUnapplied")]
    [InlineData("operations.CreditReapplied")]
    public void Registry_resolves_the_undeliverable_credit_pair_under_the_operations_prefix(string eventType)
    {
        // event-store §4.3 / ADR-PC-021: a family-agnostic engine-declared event takes the synthetic
        // `operations` aggregate_type, so the stored event_type is `operations.<EventName>` (no family
        // prefix) and resolves against ANY family's registry once the cross-cutting set is spliced in.
        Assert.True(Registry.TryResolve(eventType, out var handler));
        Assert.NotNull(handler);
    }

    // ── CREDIT_UNDELIVERABLE_HELD_AT_SOURCE ─────────────────────────────────────────────────────────

    [Fact]
    public void Unapplied_credit_fold_is_a_no_op_so_the_credit_is_held_at_source_not_disgorged()
    {
        // ADR-PC-043 slot 5: an undeliverable credit is held AT SOURCE (the source stays payout-pending),
        // never disgorged into a void nor into the family projection. The IOU/escheat ledger is a
        // SPINE-owned fold over this operations fact, so the family fold leaves the projection UNCHANGED.
        var handler = new CreditUnappliedHandler<CounterState>();
        var before = new CounterState(42);

        var after = handler.Apply(before, UnappliedCredit());

        Assert.Equal(before, after.NewState);   // held at source — the family projection is untouched
        Assert.Empty(after.PendingEffects);      // no scheduled side effects
    }

    [Fact]
    public void Reapplied_credit_fold_is_a_no_op_the_resolution_is_a_spine_owned_ledger_fact()
    {
        // The resolution is likewise a spine-owned ledger fact (ADR-PC-043 slot 5): reapplying discharges
        // the IOU on the escheat ledger, not on the family projection — so this fold is a no-op too.
        var handler = new CreditReappliedHandler<CounterState>();
        var before = new CounterState(7);

        var after = handler.Apply(before, ReappliedCredit());

        Assert.Equal(before, after.NewState);
        Assert.Empty(after.PendingEffects);
    }

    [Fact]
    public void Folding_the_undeliverable_credit_pair_mid_stream_leaves_the_running_state_untouched()
    {
        // Held-at-source proven at the STREAM level: fold a counter through an increment, an unapplied
        // credit, a reapply, and another increment — the two operations facts contribute nothing, so the
        // total is 10 + 5, exactly as without them (deterministic no-op = held at source, not disgorged).
        var sim = NewSim();

        DomainEvent[] withUndeliverable =
            [new Incremented(10), UnappliedCredit(), ReappliedCredit(), new Incremented(5)];
        DomainEvent[] plain = [new Incremented(10), new Incremented(5)];

        var withCredit = sim.ProjectFromScratch(withUndeliverable);
        var without = sim.ProjectFromScratch(plain);

        Assert.Equal(15, withCredit.Total);
        Assert.Equal(without, withCredit);   // the credit facts are no-ops on the projection
    }

    // ── CREDIT_UNAPPLIED_IS_ATTRIBUTED ──────────────────────────────────────────────────────────────

    [Fact]
    public void Unapplied_credit_is_attributed_to_a_named_beneficiary_intent_and_amount()
    {
        // ADR-PC-043 slot 5: the credit is not swept into an anonymous pot — it names a beneficiary
        // reference, the economic intent it belongs to, the held amount, and the machine reason code.
        var e = UnappliedCredit();

        Assert.Equal("BENEFICIARY-ACCT-9", e.BeneficiaryAccountRef);
        Assert.Equal("BENEFICIARY_ACCOUNT_CLOSED", e.Reason);
        Assert.Equal(new Money(150_00), e.Amount);
        Assert.Equal(IntentId, e.IntentId);
    }

    [Fact]
    public void Reapply_resolution_key_is_structurally_distinct_from_the_original_intent()
    {
        // ADR-PC-043 §Idempotency: the ResolutionIntentId is g(IntentId) — a PURE function of the ORIGINAL
        // intent, never a fresh value — so a late original apply and the resolution collapse to exactly
        // one landing. The reapply carries BOTH the original intent and the derived resolution key, and
        // the resolution key is structurally DISTINCT from the original (a namespaced derivation, not a
        // fresh mint). The g(IntentId) derivation itself is pinned in the orchestrator SettlementReferences
        // tests; the engine layer must not depend on the orchestrator, so it asserts the carried shape.
        var reapply = ReappliedCredit();

        Assert.Equal(IntentId, reapply.OriginalIntentId);
        Assert.NotEqual(reapply.OriginalIntentId, reapply.ResolutionIntentId);
        // The resolution key CONTAINS the original intent (g is intent-derived, not a fresh mint) — the
        // structural double-pay guard, whichever prefix the orchestrator namespaces it under.
        Assert.Contains(IntentId, reapply.ResolutionIntentId);
    }

    private const string IntentId = "INTENT-11111111111111111111111111111111|maturity";
    private const string ResolutionIntentId = "RESOLVE-" + IntentId;

    private static CreditUnapplied UnappliedCredit() => new(
        IntentId: IntentId,
        BeneficiaryAccountRef: "BENEFICIARY-ACCT-9",
        Amount: new Money(150_00),
        Reason: "BENEFICIARY_ACCOUNT_CLOSED",
        UnappliedAt: new DateOnly(2026, 7, 1));

    private static CreditReapplied ReappliedCredit() => new(
        ResolutionIntentId: ResolutionIntentId,
        OriginalIntentId: IntentId,
        BeneficiaryAccountRef: "BENEFICIARY-ACCT-9",
        Amount: new Money(150_00),
        ReappliedAt: new DateOnly(2026, 7, 10));
}
