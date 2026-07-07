using Babelstone.Engine;
using Babelstone.Families.CurrentAccount;
using Babelstone.Families.CurrentAccount.Application;
using Xunit;

namespace Babelstone.Families.CurrentAccount.Application.Tests;

/// <summary>
/// Pure decider tests for the current_account LIFECYCLE decider (ADR-PC-021 §P3):
/// given a folded <see cref="AccountPosition"/> plus a command, the decider consults the
/// <see cref="LifecycleTransitions"/> legality table BEFORE producing the event and rejects an illegal
/// transition with <see cref="DomainRejectedException"/>. No engine, no Docker — every input is
/// explicit. The synchronous AUTHORIZE decider is a separate change, tested with it.
/// </summary>
public sealed class CurrentAccountLifecycleDeciderTests
{
    private static readonly Guid AccountId = Guid.NewGuid();

    private static AccountPosition InState(AccountLifecycle lifecycle) =>
        AccountPosition.Empty with { AccountId = AccountId, Lifecycle = lifecycle };

    // --- open ---

    [Fact]
    public void Open_from_Pending_yields_AccountOpened_carrying_the_command_facts()
    {
        var command = new OpenAccountCommand(AccountId, "ca_pt_standard", "EUR", new DateOnly(2026, 1, 1));

        var opened = CurrentAccountLifecycleDecider.DecideOpen(AccountPosition.Empty, command);

        Assert.Equal(AccountId, opened.AccountId);
        Assert.Equal("ca_pt_standard", opened.ProductCode);
        Assert.Equal("EUR", opened.Currency);
        Assert.Equal(new DateOnly(2026, 1, 1), opened.OpenedOn);
    }

    [Fact]
    public void Open_from_an_already_active_account_is_rejected_open_once()
    {
        var command = new OpenAccountCommand(AccountId, "ca_pt_standard", "EUR", new DateOnly(2026, 1, 1));

        Assert.Throws<DomainRejectedException>(
            () => CurrentAccountLifecycleDecider.DecideOpen(InState(AccountLifecycle.Active), command));
    }

    [Theory]
    [InlineData("", "EUR")]
    [InlineData("   ", "EUR")]
    [InlineData("ca_pt_standard", "")]
    public void Open_with_a_missing_product_code_or_currency_is_rejected(string productCode, string currency)
    {
        var command = new OpenAccountCommand(AccountId, productCode, currency, new DateOnly(2026, 1, 1));

        Assert.Throws<DomainRejectedException>(
            () => CurrentAccountLifecycleDecider.DecideOpen(AccountPosition.Empty, command));
    }

    // --- mark dormant ---

    [Fact]
    public void MarkDormant_from_Active_yields_AccountMarkedDormant()
    {
        var command = new MarkAccountDormantCommand(AccountId, new DateOnly(2026, 6, 1), "INACTIVITY_HORIZON");

        var dormant = CurrentAccountLifecycleDecider.DecideMarkDormant(InState(AccountLifecycle.Active), command);

        Assert.Equal(AccountId, dormant.AccountId);
        Assert.Equal("INACTIVITY_HORIZON", dormant.Reason);
    }

    [Theory]
    [InlineData(AccountLifecycle.Pending)]
    [InlineData(AccountLifecycle.Dormant)]
    [InlineData(AccountLifecycle.Closed)]
    public void MarkDormant_from_a_non_active_state_is_rejected(AccountLifecycle lifecycle)
    {
        var command = new MarkAccountDormantCommand(AccountId, new DateOnly(2026, 6, 1), "INACTIVITY_HORIZON");

        Assert.Throws<DomainRejectedException>(
            () => CurrentAccountLifecycleDecider.DecideMarkDormant(InState(lifecycle), command));
    }

    // --- reactivate ---

    [Fact]
    public void Reactivate_from_Dormant_yields_AccountReactivated()
    {
        var command = new ReactivateAccountCommand(AccountId, new DateOnly(2026, 9, 1));

        var reactivated = CurrentAccountLifecycleDecider.DecideReactivate(InState(AccountLifecycle.Dormant), command);

        Assert.Equal(AccountId, reactivated.AccountId);
        Assert.Equal(new DateOnly(2026, 9, 1), reactivated.ReactivatedOn);
    }

    [Fact]
    public void Reactivate_from_Active_is_rejected_only_a_dormant_account_reactivates()
    {
        var command = new ReactivateAccountCommand(AccountId, new DateOnly(2026, 9, 1));

        Assert.Throws<DomainRejectedException>(
            () => CurrentAccountLifecycleDecider.DecideReactivate(InState(AccountLifecycle.Active), command));
    }

    // --- close ---

    [Fact]
    public void Close_from_Active_yields_AccountClosed()
    {
        var command = new CloseAccountCommand(AccountId, new DateOnly(2027, 1, 1), "CUSTOMER_REQUEST");

        var closed = CurrentAccountLifecycleDecider.DecideClose(InState(AccountLifecycle.Active), command);

        Assert.Equal(AccountId, closed.AccountId);
        Assert.Equal("CUSTOMER_REQUEST", closed.ClosureReason);
    }

    [Theory]
    [InlineData(AccountLifecycle.Pending)]
    [InlineData(AccountLifecycle.Dormant)]
    [InlineData(AccountLifecycle.Closed)]
    [InlineData(AccountLifecycle.Failed)]
    public void Close_from_a_non_active_state_is_rejected(AccountLifecycle lifecycle)
    {
        // §D2: close runs only from Active. Closing a DORMANT account (Dormant → Closed) is a deliberate
        // additive extension the ADR defers (LifecycleTransitions class remarks), so it is rejected here.
        var command = new CloseAccountCommand(AccountId, new DateOnly(2027, 1, 1), "CUSTOMER_REQUEST");

        Assert.Throws<DomainRejectedException>(
            () => CurrentAccountLifecycleDecider.DecideClose(InState(lifecycle), command));
    }
}
