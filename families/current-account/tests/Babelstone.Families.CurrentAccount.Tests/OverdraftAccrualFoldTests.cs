using Babelstone.Engine;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.CurrentAccount.Tests;

/// <summary>
/// Fold conformance for the store-only <c>current_account.OverdraftInterestAccrued</c> event (ADR-PC-037 §D5):
/// it must decode on the family registry and fold replay-deterministically. In plain English: the overdraft
/// accrual's effect on the balance is the fee <see cref="Movement"/> it carries — folded by the SPINE's
/// account-keyed movement ledger (the event is <see cref="IMovementBearing"/>, tested by the engine-side
/// MovementLedgerProjectorTests) — so on the FAMILY position it is a pure no-op, exactly like the hold events
/// (a demand account's balance is a spine-owned fold, never family state — ADR-PC-033). These pin that the
/// event resolves on this family's registry and that folding it leaves the family position unchanged and
/// identical on replay (the family half of "rebuild/replay reproduces the accrued fees identically").
/// </summary>
public sealed class OverdraftAccrualFoldTests
{
    private static readonly HandlerRegistry Registry = CurrentAccountFamilyModule.Registry();

    [Fact]
    public void The_overdraft_accrual_event_resolves_on_the_family_registry()
    {
        // Registered as a family event alongside the lifecycle facts (a store-only event still needs its
        // binding, so it decodes and replays fail-closed on an account stream).
        Assert.True(
            Registry.TryResolve("current_account.OverdraftInterestAccrued", out _),
            "current_account.OverdraftInterestAccrued did not resolve on the family registry");
    }

    [Fact]
    public void An_overdraft_accrual_folds_as_a_no_op_and_replays_identically()
    {
        var accountId = Guid.NewGuid();
        var active = Fold(AccountPosition.Empty, Opened(accountId));

        var accrual = Accrued(accountId);
        var afterFirst = Fold(active, accrual);
        var afterSecond = Fold(afterFirst, accrual);

        // The family position is untouched by an accrual — same record, still Active (the fee moves the
        // spine-owned accounting balance, not this state), and folding again is a deterministic no-op, so a
        // replay reproduces the state.
        Assert.Equal(active, afterFirst);
        Assert.Equal(afterFirst, afterSecond);
        Assert.Equal(AccountLifecycle.Active, afterSecond.Lifecycle);
    }

    // --- helpers ---

    // A representative accrual: EUR 1.00 (100-cent) fee posted as ONE Observed Debit Movement (ADR-PC-043
    // engine-internal-already-effected) — the carrier the spine folds; the family fold ignores it.
    private static OverdraftInterestAccrued Accrued(Guid accountId)
    {
        var accountRef = accountId.ToString();
        var movement = new Movement(
            accountRef, SettlementDirection.Debit, new Money(100), new DateOnly(2026, 3, 5),
            MovementOperation.AccrueOverdraftInterest, MovementOrigin.Observed, Guid.NewGuid());
        return new OverdraftInterestAccrued(
            accountId, accountRef, new Money(100), 1600, "pt-overdrafts-2026.1", new DateOnly(2026, 3, 5), [movement]);
    }

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
