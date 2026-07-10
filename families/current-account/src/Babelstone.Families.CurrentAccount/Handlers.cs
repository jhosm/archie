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

public sealed class AuthorizationDeclinedHandler : IEventHandler<AccountPosition, AuthorizationDeclined>
{
    // A no-op fold, the conformant shape (not an omission): a decline is a recorded audit fact that
    // changes NOTHING on the position — the lifecycle is untouched (the account stays Active/Dormant/…),
    // and no money moved (the decider placed no hold, so both spine-owned balances are unaffected,
    // ADR-PC-033). The refusal's own codes live on the event (store-only audit), not on this state.
    public HandlerResult<AccountPosition> Apply(AccountPosition state, AuthorizationDeclined @event)
        => HandlerResult<AccountPosition>.From(state);
}

public sealed class OverdraftInterestAccruedHandler : IEventHandler<AccountPosition, OverdraftInterestAccrued>
{
    // A no-op fold on the FAMILY position, the conformant shape: the accrual's effect on the balance is the
    // fee Movement the event carries, folded by the SPINE's account-keyed movement ledger (the event is
    // IMovementBearing), never family state — a demand account's accounting balance is a spine-owned fold
    // (ADR-PC-033), so the family position is untouched by an accrual, exactly as it is by a hold. The
    // accrual's own audit facts (rate, sheet version, fee) live on the store-only event, not on this state.
    public HandlerResult<AccountPosition> Apply(AccountPosition state, OverdraftInterestAccrued @event)
        => HandlerResult<AccountPosition>.From(state);
}

public sealed class AccountCreditedHandler : IEventHandler<AccountPosition, AccountCredited>
{
    // A no-op fold on the FAMILY position, the conformant shape (ADR-PC-043 / ADR-PC-033): a received credit's
    // effect on the balance is the Credit Movement the event carries, folded by the SPINE's account-keyed
    // movement ledger (the event is IMovementBearing), never family state — a demand account's accounting
    // balance is a spine-owned fold, so the family position is untouched by a credit, exactly as it is by a
    // hold or an accrual. The lifecycle is unchanged too: a credit into an Active/Dormant account does not
    // relabel it (admission was decided UPSTREAM by ICreditAdmissible — the fold only ever sees an admitted
    // credit, so it never needs a lifecycle guard here). The credit's audit facts (amount, intent ref) live
    // on the store-only event, not on this state.
    public HandlerResult<AccountPosition> Apply(AccountPosition state, AccountCredited @event)
        => HandlerResult<AccountPosition>.From(state);
}

public sealed class AccountDebitedHandler : IEventHandler<AccountPosition, AccountDebited>
{
    // A no-op fold on the FAMILY position, the conformant shape (ADR-PC-043 / ADR-PC-033): a capture debit's
    // effect on the balance is the Debit Movement the event carries, folded by the SPINE's account-keyed
    // movement ledger (the event is IMovementBearing), never family state; the matching HoldCaptured (also in
    // the append batch) leaves the spine-owned active-hold set, likewise not tracked here. The family position
    // is untouched by a capture, exactly as it is by the placing authorize's HoldPlaced. The capture's audit
    // facts (amount, hold id, intent ref) live on the store-only event, not on this state.
    public HandlerResult<AccountPosition> Apply(AccountPosition state, AccountDebited @event)
        => HandlerResult<AccountPosition>.From(state);
}
