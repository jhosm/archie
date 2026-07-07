using Babelstone.Engine;

namespace Babelstone.Families.CurrentAccount;

// The five family-owned current_account lifecycle events (ADR-PC-037) —
// the conta à ordem's own facts: opened, opening-failed, marked-dormant, reactivated, closed. Same
// discipline as the term-deposit / personal-loan families: each is a past fact named
// <Entity><PastParticipleVerb> (08-event-catalog-governance §Naming), STRUCTURAL only (no depositor
// PII — name/NIF/IBAN — in cleartext OR ciphertext, ADR-PC-004 §P2), and folded by a PURE handler
// (Handlers.cs). The pack/schema/family pins ride on the EventEnvelope via AppendContext, not on the
// record.
//
// What is deliberately NOT here (ADR-PC-037): the HOLDS (HoldPlaced → HoldCaptured |
// HoldExpired) and the posted MOVEMENTS are NOT family-owned events — they are the engine
// cross-cutting operations.Hold* records and the ADR-PC-032 Movements the spine already owns,
// instantiated here (bound via CrossCuttingEventRegistrations.For in CurrentAccountFamilyModule),
// not re-decided. GDPR erasure is likewise the engine-declared operations.PersonalDataErasureRequested
// folded via IErasable (AccountPosition.WithErased). This file carries only the account's own
// open/dormant/close lifecycle. The authorize command with its declined taxonomy, and the
// arranged-overdraft pack rule, are separate later changes on this family.

/// <summary>The demand account is opened and starts transacting (ADR-PC-037: Pending → Active).
/// Carries the structural product identity fixed at opening; the account's balances are NOT carried
/// here — both are spine-owned folds over the movement ledger + hold set (ADR-PC-033), never a
/// stored number on this event or on <see cref="AccountPosition"/>.</summary>
/// <param name="AccountId">The account stream id — the opaque instance identifier that is also this
/// account's <c>account_ref</c> (<see cref="AccountPosition.AccountRef"/>); never PII (ADR-PC-004 §P2).</param>
/// <param name="ProductCode">The catalogue product code (e.g. <c>ca_pt_standard</c>) — the STRUCTURAL
/// product identifier, not PII (ADR-PC-004 §P2).</param>
/// <param name="Currency">The account's ISO-4217 currency (e.g. <c>EUR</c>) — a structural token.</param>
/// <param name="OpenedOn">The value-date the account opened — an input date, never a clock read in a fold.</param>
public sealed record AccountOpened(
    Guid AccountId,
    string ProductCode,
    string Currency,
    DateOnly OpenedOn) : DomainEvent
{
    // Opening is a snapshot lifecycle boundary (ADR-PC-003 §P2 / event-store §8.1): the instance's
    // state is interpretable on its own here (the stream's first event).
    public override bool IsLifecycleBoundary => true;
}

/// <summary>Opening was rejected by a config/rule/precondition check, so no account exists
/// (ADR-PC-037: Pending → Failed). Carries failure CODES only — never anything about the customer
/// (ADR-PC-004 §P2). Account opening (KYC/onboarding) stays UPSTREAM (ADR-PC-024 / ADR-PC-030 §P1);
/// the engine records the verdict, it never runs it.</summary>
/// <param name="AccountId">The account stream id the rejected open targeted — never PII (ADR-PC-004 §P2).</param>
/// <param name="FailureReason">Stable machine failure code (e.g. <c>PRODUCT_NOT_FOUND</c>).</param>
/// <param name="FailureDetail">Human-readable detail about the config/rule that failed — never PII.</param>
public sealed record AccountOpeningFailed(
    Guid AccountId,
    string FailureReason,
    string FailureDetail) : DomainEvent;

/// <summary>The account is marked dormant after an inactivity horizon (ADR-PC-037: Active →
/// Dormant). <see cref="AccountPosition.Lifecycle"/> Dormant is a NON-terminal, reversible state — an
/// inactive account reactivates on use (<see cref="AccountReactivated"/>), distinguishing the demand
/// account from the loan's good-or-closed binary. The dormancy CRITERIA (the inactivity horizon) are
/// pack/product policy, not fixed here (ADR-PC-037).</summary>
/// <param name="AccountId">The account stream id — never PII (ADR-PC-004 §P2).</param>
/// <param name="MarkedOn">The value-date dormancy took effect — an input date, never a clock read.</param>
/// <param name="Reason">The structural dormancy reason (e.g. <c>INACTIVITY_HORIZON</c>) — never PII.</param>
public sealed record AccountMarkedDormant(
    Guid AccountId,
    DateOnly MarkedOn,
    string Reason) : DomainEvent;

/// <summary>A dormant account is used again and reactivates (ADR-PC-037: Dormant → Active) — the
/// reverse leg of the reversible <c>Dormant ⇄ Active</c> pair.</summary>
/// <param name="AccountId">The account stream id — never PII (ADR-PC-004 §P2).</param>
/// <param name="ReactivatedOn">The value-date the account reactivated — an input date, never a clock read.</param>
public sealed record AccountReactivated(
    Guid AccountId,
    DateOnly ReactivatedOn) : DomainEvent;

/// <summary>The account is closed (ADR-PC-037: Active → Closed, a business terminal). A closed
/// account still holds the subject's PII until erased, so the GDPR-erasure transition remains legal
/// from Closed (LifecycleTransitions).</summary>
/// <param name="AccountId">The account stream id — never PII (ADR-PC-004 §P2).</param>
/// <param name="ClosedOn">The value-date the account closed — an input date, never a clock read.</param>
/// <param name="ClosureReason">The structural closure reason (e.g. <c>CUSTOMER_REQUEST</c>) — never PII.</param>
public sealed record AccountClosed(
    Guid AccountId,
    DateOnly ClosedOn,
    string ClosureReason) : DomainEvent
{
    // Closing is a snapshot lifecycle boundary (ADR-PC-003 §P2 / event-store §8.1) — a terminal
    // point where the instance's state is interpretable on its own.
    public override bool IsLifecycleBoundary => true;
}
