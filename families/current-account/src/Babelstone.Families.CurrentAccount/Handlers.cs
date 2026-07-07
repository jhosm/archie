using Babelstone.Engine;

namespace Babelstone.Families.CurrentAccount;

// Pure folds (state, event) → state — one per family event, mirroring the term-deposit / personal-loan
// families. No clock, no I/O, no randomness (BENG001/002/003); each body is a single `state with { … }`
// that only LABELS the lifecycle and records the structural facts the event carries. There is no money
// arithmetic here: a current account's balances are spine-owned folds (the AccountHoldProjector + the
// movement ledger, ADR-PC-033), so these folds never touch a balance — they track only the account's
// own open/dormant/close lifecycle (ADR-PC-037). Legality (which transition is allowed from
// which state) is LifecycleTransitions, consulted command-side by the decider BEFORE the append; the
// folds stay guard-free label-only writes.

public sealed class AccountOpenedHandler : IEventHandler<AccountPosition, AccountOpened>
{
    public HandlerResult<AccountPosition> Apply(AccountPosition state, AccountOpened @event)
        => HandlerResult<AccountPosition>.From(state with
        {
            AccountId = @event.AccountId,
            ProductCode = @event.ProductCode,
            Currency = @event.Currency,
            OpenedOn = @event.OpenedOn,
            Lifecycle = AccountLifecycle.Active,
        });
}

public sealed class AccountOpeningFailedHandler : IEventHandler<AccountPosition, AccountOpeningFailed>
{
    public HandlerResult<AccountPosition> Apply(AccountPosition state, AccountOpeningFailed @event)
        => HandlerResult<AccountPosition>.From(state with
        {
            // No account opened: record the id for lineage and label the terminal Failed state; the
            // failure codes live on the event (store-only audit), not on this structural position.
            AccountId = @event.AccountId,
            Lifecycle = AccountLifecycle.Failed,
        });
}

public sealed class AccountMarkedDormantHandler : IEventHandler<AccountPosition, AccountMarkedDormant>
{
    public HandlerResult<AccountPosition> Apply(AccountPosition state, AccountMarkedDormant @event)
        => HandlerResult<AccountPosition>.From(state with
        {
            // Dormant is NON-terminal and reversible (ADR-PC-037): reactivation folds it back to
            // Active. Only the lifecycle label moves; the structural facts are untouched.
            Lifecycle = AccountLifecycle.Dormant,
        });
}

public sealed class AccountReactivatedHandler : IEventHandler<AccountPosition, AccountReactivated>
{
    public HandlerResult<AccountPosition> Apply(AccountPosition state, AccountReactivated @event)
        => HandlerResult<AccountPosition>.From(state with
        {
            Lifecycle = AccountLifecycle.Active,
        });
}

public sealed class AccountClosedHandler : IEventHandler<AccountPosition, AccountClosed>
{
    public HandlerResult<AccountPosition> Apply(AccountPosition state, AccountClosed @event)
        => HandlerResult<AccountPosition>.From(state with
        {
            Lifecycle = AccountLifecycle.Closed,
        });
}
