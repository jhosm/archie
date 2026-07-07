namespace Babelstone.Families.CurrentAccount.Application;

// The current_account LIFECYCLE commands (ADR-PC-021 §P3): the intents the
// lifecycle decider (CurrentAccountLifecycleDecider) turns into the family's own events, one command
// per driving transition (open / mark-dormant / reactivate / close). STRUCTURAL only — no PII
// (ADR-PC-004 §P2). The synchronous AUTHORIZE command (with its account_ref/amount/value_date/
// Idempotency-Key and declined taxonomy) is the sibling authorize path, a separate change, not
// modelled here.

/// <summary>Open a new demand account (→ <see cref="AccountOpened"/>). Opening is decided only from
/// the seed Pending state (open-once, LifecycleTransitions).</summary>
public sealed record OpenAccountCommand(
    Guid AccountId,
    string ProductCode,
    string Currency,
    DateOnly OpenedOn);

/// <summary>Mark a live account dormant after an inactivity horizon (→ <see cref="AccountMarkedDormant"/>).
/// Legal only from Active; Dormant is reversible via <see cref="ReactivateAccountCommand"/>.</summary>
public sealed record MarkAccountDormantCommand(
    Guid AccountId,
    DateOnly MarkedOn,
    string Reason);

/// <summary>Reactivate a dormant account on use (→ <see cref="AccountReactivated"/>). Legal only from
/// Dormant — the reverse leg of the <c>Dormant ⇄ Active</c> pair.</summary>
public sealed record ReactivateAccountCommand(
    Guid AccountId,
    DateOnly ReactivatedOn);

/// <summary>Close a live account (→ <see cref="AccountClosed"/>, a business terminal). Legal only from
/// Active (ADR-PC-037).</summary>
public sealed record CloseAccountCommand(
    Guid AccountId,
    DateOnly ClosedOn,
    string ClosureReason);
