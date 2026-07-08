using Babelstone.Engine;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.CurrentAccount.Tests;

/// <summary>
/// Cross-cutting hold-lifecycle conformance (ADR-PC-033): the current account is
/// the first <see cref="IHoldable"/> family, so the engine-declared authorization-hold events
/// (<c>operations.HoldPlaced → HoldCaptured | HoldExpired</c>) — and the ADR-PC-041 legal-hold/freeze
/// events — MUST decode and replay fail-closed on its stream. That is exactly what the
/// <c>.. CrossCuttingEventRegistrations.For&lt;AccountPosition&gt;()</c> splice in
/// <see cref="CurrentAccountFamilyModule"/> buys: a transactional family that omitted it would throw on
/// the first hold that lands. These tests pin (a) the whole cross-cutting set resolves on this family's
/// registry, (b) each hold event folds as a NO-OP on the family position — because the active-hold
/// set and both balances are the SPINE-owned <c>AccountHoldProjector</c> fold, never family state — and
/// (c) the whole ADR-PC-037 hold-reconciliation vocabulary (partial / over / late / reversal / expiry)
/// folds inert and identically on replay on a family stream: <c>HOLD_LIFECYCLE_PURE</c>'s first *family*
/// instance (the reconciliation-arithmetic determinism is the spine's <c>AccountHoldProjectorTests</c>).
/// </summary>
public sealed class HoldFoldTests
{
    private static readonly HandlerRegistry Registry = CurrentAccountFamilyModule.Registry();

    [Theory]
    [InlineData("operations.HoldPlaced")]
    [InlineData("operations.HoldCaptured")]
    [InlineData("operations.HoldExpired")]
    [InlineData("operations.FundsHeld")]
    [InlineData("operations.FundsReleased")]
    [InlineData("operations.AccountFrozen")]
    [InlineData("operations.AccountUnfrozen")]
    [InlineData("operations.PersonalDataErasureRequested")]
    [InlineData("operations.PackVersionMigrated")]
    [InlineData("operations.SchemaVersionMigrated")]
    public void The_whole_cross_cutting_set_resolves_on_this_familys_registry(string eventType)
    {
        // The IHoldable family binds the engine-declared cross-cutting set in one splice; every member
        // must resolve so it decodes (and replays fail-closed) on an account stream (ADR-PC-033 / -041).
        Assert.True(Registry.TryResolve(eventType, out _), $"cross-cutting {eventType} did not resolve");
    }

    [Fact]
    public void A_placed_hold_folds_as_a_no_op_leaving_the_family_position_unchanged()
    {
        var accountId = Guid.NewGuid();
        var active = Fold(AccountPosition.Empty, Opened(accountId));

        var afterHold = Fold(active, new HoldPlaced(
            InstanceId: accountId,
            HoldId: "hold-1",
            AccountRef: accountId.ToString(),
            Amount: new Money(5_000),
            ValueDate: new DateOnly(2026, 2, 1)));

        // The earmark is spine-owned (AccountHoldProjector); the FAMILY position is untouched — same
        // record, still Active. "The engine knows this account has an active hold of N; the family knows
        // what authorization placed it" (ADR-PC-033).
        Assert.Equal(active, afterHold);
        Assert.Equal(AccountLifecycle.Active, afterHold.Lifecycle);
    }

    [Fact]
    public void A_place_then_capture_then_expire_sequence_never_moves_the_family_lifecycle()
    {
        var accountId = Guid.NewGuid();
        var position = Fold(AccountPosition.Empty, Opened(accountId));

        position = Fold(position, new HoldPlaced(accountId, "hold-1", accountId.ToString(), new Money(5_000), new DateOnly(2026, 2, 1)));
        position = Fold(position, new HoldCaptured(accountId, "hold-1", accountId.ToString(), new Money(5_000), new DateOnly(2026, 2, 3)));
        position = Fold(position, new HoldExpired(accountId, "hold-2", accountId.ToString(), new DateOnly(2026, 2, 5)));

        // Holds are cross-cutting facts the spine folds; the account's own lifecycle stays exactly where
        // its family events left it — Active throughout the hold lifecycle.
        Assert.Equal(AccountLifecycle.Active, position.Lifecycle);
        Assert.Equal(accountId, position.AccountId);
    }

    [Fact]
    public void The_whole_D4_hold_vocabulary_folds_inert_and_identically_on_a_family_stream()
    {
        // HOLD_LIFECYCLE_PURE — the FIRST *family* instance (ADR-PC-037). The reconciliation-arithmetic
        // determinism (partial vs over-capture, terminal-state precedence, the release-anomaly signal) is the
        // SPINE projector's, owned by the engine-side AccountHoldProjectorTests. This pins the complement on a
        // current_account STREAM: every event the D4 policy admits — (a) partial capture, (c) over-capture,
        // (d) reversal (expire before capture), (b) late capture as a re-presentment after expiry, and (e) a
        // plain projection-derived expiry — DECODES on the family registry and folds INERT (holds are
        // spine-owned, ADR-PC-033), so the family fold is a deterministic no-op over the whole hold
        // vocabulary. Replaying the sequence yields identical state.
        var accountId = Guid.NewGuid();
        var opened = Fold(AccountPosition.Empty, Opened(accountId));
        var acct = accountId.ToString();

        // One stream carrying every ADR-PC-037 reconciliation scenario as its holds resolve.
        DomainEvent[] holdLifecycle =
        [
            // (a) partial capture — captured < held.
            new HoldPlaced(accountId, "hold-partial", acct, new Money(5_000), new DateOnly(2026, 2, 1)),
            new HoldCaptured(accountId, "hold-partial", acct, new Money(3_000), new DateOnly(2026, 2, 3)),
            // (c) over-capture — captured > held.
            new HoldPlaced(accountId, "hold-over", acct, new Money(5_000), new DateOnly(2026, 2, 1)),
            new HoldCaptured(accountId, "hold-over", acct, new Money(6_000), new DateOnly(2026, 2, 4)),
            // (d) reversal — an authorization voided before capture (a HoldExpired-style release, no posting).
            new HoldPlaced(accountId, "hold-reversed", acct, new Money(2_000), new DateOnly(2026, 2, 1)),
            new HoldExpired(accountId, "hold-reversed", acct, new DateOnly(2026, 2, 5)),
            // (b) late capture vs expiry — a capture arriving AFTER the matching HoldExpired (a re-presentment).
            new HoldPlaced(accountId, "hold-late", acct, new Money(4_000), new DateOnly(2026, 2, 1)),
            new HoldExpired(accountId, "hold-late", acct, new DateOnly(2026, 2, 2)),
            new HoldCaptured(accountId, "hold-late", acct, new Money(4_000), new DateOnly(2026, 2, 6)),
            // (e) expiry horizon — a plain projection-derived expiry of an un-captured hold (the driver path).
            new HoldPlaced(accountId, "hold-expired", acct, new Money(1_000), new DateOnly(2026, 2, 1)),
            new HoldExpired(accountId, "hold-expired", acct, new DateOnly(2026, 2, 7)),
        ];

        var first = holdLifecycle.Aggregate(opened, Fold);
        var second = holdLifecycle.Aggregate(opened, Fold);

        // Deterministic: the same event sequence folds to the same state on every replay (HOLD_LIFECYCLE_PURE).
        Assert.Equal(first, second);
        // Inert: not one of the five reconciliation scenarios moves the family position — it is exactly the
        // opened account, still Active (the whole hold lifecycle is a spine-owned fold).
        Assert.Equal(opened, first);
        Assert.Equal(AccountLifecycle.Active, first.Lifecycle);
    }

    // --- helpers ---

    private static AccountOpened Opened(Guid accountId) => new(
        AccountId: accountId,
        ProductCode: "ca_pt_standard",
        Currency: "EUR",
        OpenedOn: new DateOnly(2026, 1, 1));

    private static AccountPosition Fold(AccountPosition state, DomainEvent @event)
    {
        var name = @event.GetType().Name;
        var eventType = @event.GetType().Namespace == "Babelstone.Engine"
            ? $"operations.{name}"
            : $"current_account.{name}";
        Assert.True(Registry.TryResolve(eventType, out var handler), $"no handler for {eventType}");
        return (AccountPosition)handler.ApplyBoxed(state, @event).NewState;
    }
}
