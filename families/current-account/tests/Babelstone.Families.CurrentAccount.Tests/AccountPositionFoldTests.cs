using Babelstone.Engine;
using Xunit;

namespace Babelstone.Families.CurrentAccount.Tests;

/// <summary>
/// Replay/fold tests for the current_account account position: the pure folds LABEL the lifecycle and
/// record the structural facts the events carry — they touch NO balance (a transactional account's
/// balances are spine-owned folds, ADR-PC-033 / ADR-PC-037). These pin the demand-account shape
/// (open → transact → dormant ⇄ active → close) and the deterministic re-fold the engine's replay
/// relies on (ADR-PC-010 §P5): re-folding the same event sequence yields a value-equal position. (The
/// serialized-byte / cold-store rehydration is the durable-runtime lane, not this scaffold's.) They
/// fold through the family's OWN handler registry — the same one the durable runtime and the
/// projection runner fold through.
/// </summary>
public sealed class AccountPositionFoldTests
{
    private static readonly HandlerRegistry Registry = CurrentAccountFamilyModule.Registry();

    [Fact]
    public void Opening_activates_the_account_and_records_the_structural_facts()
    {
        var accountId = Guid.NewGuid();
        var position = Fold(AccountPosition.Empty, Opened(accountId));

        Assert.Equal(accountId, position.AccountId);
        Assert.Equal(AccountLifecycle.Active, position.Lifecycle);
        Assert.Equal("ca_pt_standard", position.ProductCode);
        Assert.Equal("EUR", position.Currency);
        Assert.Equal(new DateOnly(2026, 1, 1), position.OpenedOn);
    }

    [Fact]
    public void Opening_failure_folds_to_Failed_with_no_account_opened()
    {
        var accountId = Guid.NewGuid();
        var position = Fold(AccountPosition.Empty, new AccountOpeningFailed(
            accountId, "PRODUCT_NOT_FOUND", "No product ca_pt_unknown in the pinned pack."));

        Assert.Equal(accountId, position.AccountId);
        Assert.Equal(AccountLifecycle.Failed, position.Lifecycle);
        // No account was opened: the structural product facts were never folded.
        Assert.Equal(string.Empty, position.ProductCode);
    }

    [Fact]
    public void The_dormant_reactivate_cycle_is_reversible()
    {
        var accountId = Guid.NewGuid();
        var position = Fold(AccountPosition.Empty, Opened(accountId));

        position = Fold(position, new AccountMarkedDormant(accountId, new DateOnly(2026, 6, 1), "INACTIVITY_HORIZON"));
        Assert.Equal(AccountLifecycle.Dormant, position.Lifecycle);

        // Dormant is NON-terminal (ADR-PC-037): reactivation on use folds it back to Active, and
        // the structural facts survive the round-trip unchanged.
        position = Fold(position, new AccountReactivated(accountId, new DateOnly(2026, 9, 1)));
        Assert.Equal(AccountLifecycle.Active, position.Lifecycle);
        Assert.Equal("ca_pt_standard", position.ProductCode);
        Assert.Equal(new DateOnly(2026, 1, 1), position.OpenedOn);
    }

    [Fact]
    public void Closing_folds_the_account_to_the_Closed_terminal()
    {
        var accountId = Guid.NewGuid();
        var position = Fold(AccountPosition.Empty, Opened(accountId));

        position = Fold(position, new AccountClosed(accountId, new DateOnly(2027, 1, 1), "CUSTOMER_REQUEST"));

        Assert.Equal(AccountLifecycle.Closed, position.Lifecycle);
        // Structural facts remain queryable on the closed position.
        Assert.Equal(accountId, position.AccountId);
        Assert.Equal("EUR", position.Currency);
    }

    [Fact]
    public void Erasure_folds_to_Erased_leaving_structural_fields_queryable()
    {
        var accountId = Guid.NewGuid();
        var position = Fold(AccountPosition.Empty, Opened(accountId));

        // The engine-declared cross-cutting GDPR erasure, bound against AccountPosition via
        // CrossCuttingEventRegistrations.For<AccountPosition>() (IErasable → WithErased).
        position = Fold(position, new PersonalDataErasureRequested(
            accountId, "pseudo-abc", new DateOnly(2027, 1, 1), "GDPR_ARTICLE_17"));

        Assert.Equal(AccountLifecycle.Erased, position.Lifecycle);
        // Structural fields stay queryable post-erasure (the personal data lived behind the OpenBao key).
        Assert.Equal(accountId, position.AccountId);
        Assert.Equal("ca_pt_standard", position.ProductCode);
    }

    [Fact]
    public void Cold_replay_reproduces_a_byte_identical_position()
    {
        // Folds are deterministic — re-folding the same event sequence yields an equal position
        // (record value equality; no collection fields, so the synthesized equality is correct).
        var accountId = Guid.NewGuid();
        var events = new DomainEvent[]
        {
            Opened(accountId),
            new AccountMarkedDormant(accountId, new DateOnly(2026, 6, 1), "INACTIVITY_HORIZON"),
            new AccountReactivated(accountId, new DateOnly(2026, 9, 1)),
            new AccountClosed(accountId, new DateOnly(2027, 1, 1), "CUSTOMER_REQUEST"),
        };

        var first = events.Aggregate(AccountPosition.Empty, Fold);
        var second = events.Aggregate(AccountPosition.Empty, Fold);

        Assert.Equal(first, second);
        Assert.Equal(AccountLifecycle.Closed, first.Lifecycle);
    }

    // --- helpers ---

    private static AccountOpened Opened(Guid accountId) => new(
        AccountId: accountId,
        ProductCode: "ca_pt_standard",
        Currency: "EUR",
        OpenedOn: new DateOnly(2026, 1, 1));

    private static AccountPosition Fold(AccountPosition state, DomainEvent @event)
    {
        // event_type mirrors the engine's binding: a family event is `current_account.<Name>`, while an
        // engine-declared cross-cutting event (Babelstone.Engine namespace, e.g.
        // PersonalDataErasureRequested / HoldPlaced) is `operations.<Name>` (event-store §4.3).
        var name = @event.GetType().Name;
        var eventType = @event.GetType().Namespace == "Babelstone.Engine"
            ? $"operations.{name}"
            : $"current_account.{name}";
        Assert.True(Registry.TryResolve(eventType, out var handler), $"no handler for {eventType}");
        return (AccountPosition)handler.ApplyBoxed(state, @event).NewState;
    }
}
