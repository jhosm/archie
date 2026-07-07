using Babelstone.Engine;
using Xunit;

namespace Babelstone.Families.CurrentAccount.Tests;

/// <summary>
/// Account-seam conformance (ADR-PC-033 slot 1, ADR-PC-037): the current
/// account is the FIRST TRANSACTIONAL account — so <see cref="AccountPosition"/> implements BOTH the
/// spine-owned <see cref="IAccount"/> seam AND the <see cref="IHoldable"/> refinement (the inverse of
/// the deposit/loan degenerate accounts, which are <see cref="IAccount"/> but NOT
/// <see cref="IHoldable"/>). These tests pin that the account declares the transactional seam, that
/// the <c>account_ref</c> is the account's own opaque stream id (never PII — ADR-PC-004 §P2), that it
/// is stable across the fold, and that the computed seam left the record equality — the
/// replay-determinism backstop (ADR-PC-010 §P5) — untouched.
/// </summary>
public sealed class AccountPositionAccountSeamTests
{
    private static readonly HandlerRegistry Registry = CurrentAccountFamilyModule.Registry();

    [Fact]
    public void AccountPosition_implements_both_IAccount_and_the_IHoldable_refinement()
    {
        // The seam declares "my state IS an account" (ADR-PC-033 slot 1); the IHoldable refinement
        // declares it carries the accounting/available split + a hold ledger. The current account is
        // the first NON-degenerate account (ADR-PC-037), so both hold — unlike the deposit/loan.
        Assert.IsAssignableFrom<IAccount>(AccountPosition.Empty);
        Assert.IsAssignableFrom<IHoldable>(AccountPosition.Empty);
    }

    [Fact]
    public void AccountRef_is_the_accounts_own_stream_id_after_the_open_fold()
    {
        var accountId = Guid.NewGuid();
        var active = Fold(AccountPosition.Empty, Opened(accountId));

        IAccount account = active;
        // The account_ref is the account's OWN opaque instance id — the key the movement ledger and
        // active-hold set fold under — and never PII (ADR-PC-004 §P2).
        Assert.Equal(accountId.ToString(), account.AccountRef);
        Assert.False(string.IsNullOrEmpty(account.AccountRef));
    }

    [Fact]
    public void AccountRef_is_stable_across_the_dormant_reactivate_cycle()
    {
        var accountId = Guid.NewGuid();
        var active = Fold(AccountPosition.Empty, Opened(accountId));
        var refAtOpen = ((IAccount)active).AccountRef;

        var dormant = Fold(active, new AccountMarkedDormant(accountId, new DateOnly(2026, 6, 1), "INACTIVITY_HORIZON"));
        var reactivated = Fold(dormant, new AccountReactivated(accountId, new DateOnly(2026, 9, 1)));

        // The stream id never moves once folded, so the account_ref the movement ledger + hold set
        // key by is stable across the reversible Dormant ⇄ Active cycle.
        Assert.Equal(refAtOpen, ((IAccount)dormant).AccountRef);
        Assert.Equal(refAtOpen, ((IAccount)reactivated).AccountRef);
    }

    [Fact]
    public void The_seam_leaves_the_record_equality_semantics_unchanged()
    {
        // AccountRef is a COMPUTED property over the already-folded AccountId, not a record positional
        // parameter, so the compiler-synthesised record equality (the byte-identical replay
        // determinism the engine relies on, ADR-PC-010 §P5) must behave exactly as before: two
        // independently-folded but identical positions are equal and hash identically.
        var accountId = Guid.NewGuid();
        var first = Fold(AccountPosition.Empty, Opened(accountId));
        var second = Fold(AccountPosition.Empty, Opened(accountId));

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());

        // And two DIFFERENT accounts stay unequal — the seam added no equality wrinkle either way.
        var other = Fold(AccountPosition.Empty, Opened(Guid.NewGuid()));
        Assert.NotEqual(first, other);
    }

    // --- helpers ---

    private static AccountOpened Opened(Guid accountId) => new(
        AccountId: accountId,
        ProductCode: "ca_pt_standard",
        Currency: "EUR",
        OpenedOn: new DateOnly(2026, 1, 1));

    private static AccountPosition Fold(AccountPosition state, DomainEvent @event)
    {
        // Mirrors AccountPositionFoldTests: fold through the family's OWN handler registry — the same
        // one the durable runtime folds through — so the seam is proved on real folded state. A family
        // event is `current_account.<Name>`; an engine-declared cross-cutting event
        // (Babelstone.Engine namespace) is `operations.<Name>` (event-store §4.3).
        var name = @event.GetType().Name;
        var eventType = @event.GetType().Namespace == "Babelstone.Engine"
            ? $"operations.{name}"
            : $"current_account.{name}";
        Assert.True(Registry.TryResolve(eventType, out var handler), $"no handler for {eventType}");
        return (AccountPosition)handler.ApplyBoxed(state, @event).NewState;
    }
}
