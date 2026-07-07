using Babelstone.Engine;
using Babelstone.Families.CurrentAccount;
using static Babelstone.Families.CurrentAccount.LifecycleTransitions;

namespace Babelstone.Families.CurrentAccount.Application;

/// <summary>
/// The pure decision core of the current_account LIFECYCLE decider (ADR-PC-021 §P3): given the
/// account's folded <see cref="AccountPosition"/> plus a lifecycle
/// command, it consults the <see cref="LifecycleTransitions"/> legality table BEFORE producing the
/// family event, rejecting an illegal transition with <see cref="DomainRejectedException"/>. No clock,
/// no I/O, no randomness — every date is supplied on the command, so this is unit-tested Docker-free.
/// The impure orchestration (rehydrate the position, append the event) is the command shell's; keeping
/// the decision pure is what lets it be replay-checked and reused.
/// </summary>
/// <remarks>
/// This decider owns only the account's OPEN/DORMANT/CLOSE lifecycle. The synchronous AUTHORIZE
/// decision — fold the available balance, apply pack rules + the arranged overdraft, and append
/// <c>HoldPlaced</c> or a refusal fact with the declined taxonomy — is a separate change on the
/// ADR-PC-034 synchronous technique, not this class. Both the authorize and the lifecycle deciders
/// live in this Application project (the ADR-PC-037 project topology).
/// </remarks>
public static class CurrentAccountLifecycleDecider
{
    /// <summary>Decide <see cref="OpenAccountCommand"/> → <see cref="AccountOpened"/>. Legal only from
    /// the seed Pending state (open-once). The scaffold guards the two structural facts the account
    /// cannot open without — a product code and a currency — rejecting an empty one before any append
    /// (the decider's rejection role, ADR-PC-021 §P3); richer opening preconditions stay UPSTREAM
    /// (ADR-PC-024 / ADR-PC-030 §P1) and arrive as recorded verdicts, never re-run here.</summary>
    /// <exception cref="DomainRejectedException">If the account is not Pending, or the product code /
    /// currency is empty.</exception>
    public static AccountOpened DecideOpen(AccountPosition current, OpenAccountCommand command)
    {
        RequireLegal(current.Lifecycle, Transition.Open);

        if (string.IsNullOrWhiteSpace(command.ProductCode))
        {
            throw new DomainRejectedException("current_account open requires a product code.");
        }

        if (string.IsNullOrWhiteSpace(command.Currency))
        {
            throw new DomainRejectedException("current_account open requires a currency.");
        }

        return new AccountOpened(command.AccountId, command.ProductCode, command.Currency, command.OpenedOn);
    }

    /// <summary>Decide <see cref="MarkAccountDormantCommand"/> → <see cref="AccountMarkedDormant"/>.
    /// Legal only from Active.</summary>
    /// <exception cref="DomainRejectedException">If the account is not Active.</exception>
    public static AccountMarkedDormant DecideMarkDormant(AccountPosition current, MarkAccountDormantCommand command)
    {
        RequireLegal(current.Lifecycle, Transition.MarkDormant);
        return new AccountMarkedDormant(command.AccountId, command.MarkedOn, command.Reason);
    }

    /// <summary>Decide <see cref="ReactivateAccountCommand"/> → <see cref="AccountReactivated"/>.
    /// Legal only from Dormant — the reverse leg of <c>Dormant ⇄ Active</c>.</summary>
    /// <exception cref="DomainRejectedException">If the account is not Dormant.</exception>
    public static AccountReactivated DecideReactivate(AccountPosition current, ReactivateAccountCommand command)
    {
        RequireLegal(current.Lifecycle, Transition.Reactivate);
        return new AccountReactivated(command.AccountId, command.ReactivatedOn);
    }

    /// <summary>Decide <see cref="CloseAccountCommand"/> → <see cref="AccountClosed"/>. Legal only from
    /// Active (ADR-PC-037).</summary>
    /// <exception cref="DomainRejectedException">If the account is not Active.</exception>
    public static AccountClosed DecideClose(AccountPosition current, CloseAccountCommand command)
    {
        RequireLegal(current.Lifecycle, Transition.Close);
        return new AccountClosed(command.AccountId, command.ClosedOn, command.ClosureReason);
    }

    // The one guard every lifecycle command runs before appending: a false from the legality table is
    // a DomainRejectedException, never a silent no-append. The message names the offending
    // (state, transition) pair — non-PII, machine-greppable — for the audit trail.
    private static void RequireLegal(AccountLifecycle current, Transition transition)
    {
        if (!IsLegal(current, transition))
        {
            throw new DomainRejectedException(
                $"current_account transition {transition} is illegal from lifecycle state {current}.");
        }
    }
}
