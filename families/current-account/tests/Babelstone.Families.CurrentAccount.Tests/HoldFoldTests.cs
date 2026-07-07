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
/// registry, and (b) each hold event folds as a NO-OP on the family position — because the active-hold
/// set and both balances are the SPINE-owned <c>AccountHoldProjector</c> fold, never family state.
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
