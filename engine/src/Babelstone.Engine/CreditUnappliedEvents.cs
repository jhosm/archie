using Babelstone.FinancialTypes;

namespace Babelstone.Engine;

// The undeliverable-credit cross-cutting operations events (ADR-PC-043 slot 5): the two pure
// facts operations.CreditUnapplied and operations.CreditReapplied, engine-declared in the spine
// under the synthetic `operations` aggregate_type — the same posture as the ADR-PC-033 hold
// lifecycle (AccountHoldEvents.cs) and PersonalDataErasureRequested. In plain English: when a
// matured payout has nowhere to land — the beneficiary account is closed, dormant-past-revival,
// or simply does not exist — the money is NOT disgorged into a void nor swept into an anonymous
// escheat pot. It is held AT SOURCE (the deposit stays payout-pending) and, if the credit is
// genuinely undeliverable, recorded as a NAMED IOU to the customer: operations.CreditUnapplied
// attributes the held amount to a specific beneficiary reference and intent. When a live
// destination later exists, operations.CreditReapplied records the resolution — keyed by a
// ResolutionIntentId DERIVED FROM the original IntentId (SettlementReferences.DeriveResolutionIntentId),
// so a late original apply and the resolution collapse to exactly one landing (the structural
// double-pay guard, ADR-PC-043).
//
// Family-agnostic (ADR-PC-021): the records and their pure folds name NO family — any family whose
// payout can be undeliverable records these same two facts, so the engine declares them ONCE rather
// than each family re-deriving a copy that would collide on the simple-name codec. Each family BINDS
// the no-op folds against its own projection state via CrossCuttingEventRegistrations.For<TState>()
// so the events DECODE (and replay fail-closed) on every family stream that can carry them.
//
// CATALOGUED / PROMOTED to the durable bus (ADR-IC-017): the catalog entry IS the promotion — the
// governed wire shapes (contracts/avro/operations/CreditUnapplied.avsc / CreditReapplied.avsc) and
// their AsyncAPI entries (action: send) publish these facts on the bus, consumed by acl (downstream
// core-banking escheat/IOU reconciliation) and notification (the customer-facing 'payout held,
// action needed' advice).
//
// NO PII (ADR-PC-004): opaque refs, integer-cents Money, stable machine reason codes, and input
// dates only; every date is supplied by the command, never read from a clock in a fold (ADR-PC-023).
//
// The per-family folds below are NO-OPs by design: the escheat/IOU ledger is a SPINE-owned
// rebuildable fold over these operations facts (the same posture as the hold ledger, ADR-PC-033),
// not family projection state — the engine knows "this intent's credit is unapplied / has been
// resolved", the family knows only that its own source stayed payout-pending until it could land.

/// <summary>
/// A matured/approved payout could not be delivered, so its credit is held UNAPPLIED rather than
/// disgorged (ADR-PC-043 slot 5). In plain English: the money had nowhere to land — the beneficiary
/// account is closed, dormant-past-revival, or does not exist — so instead of losing it into a void
/// or an anonymous pot, the engine records a NAMED IOU: this amount is owed to this beneficiary under
/// this economic intent. The source keeps the funds (it stays payout-pending) and this fact attributes
/// the undeliverable credit so it can be reconciled and later reapplied.
/// </summary>
/// <remarks>
/// The RESOLUTION key is NOT on this event: it is DERIVED from <paramref name="IntentId"/> by
/// <c>SettlementReferences.DeriveResolutionIntentId</c> when a reapply is attempted, so a late original
/// apply and the resolution both key off the SAME intent and collapse to one landing (the double-pay
/// guard, ADR-PC-043). Pure fold, no clock, no I/O (BENG001/002/003) — replay deterministic.
/// </remarks>
/// <param name="IntentId">The ADR-PC-043 slot-4 economic-intent id the undeliverable payout belongs to
/// (from <c>SettlementReferences.DeriveIntentId</c>) — the exactly-once axis AND the root the resolution
/// key is derived from. A structural token, never PII (ADR-PC-004).</param>
/// <param name="BeneficiaryAccountRef">The opaque beneficiary account the credit was meant to land on —
/// a reference the engine resolves internally, NEVER PII / an IBAN (ADR-PC-004).</param>
/// <param name="Amount">The undeliverable amount, integer-cents <see cref="Money"/> (ADR-PC-010) — held,
/// never disgorged.</param>
/// <param name="Reason">The stable machine reason code the credit was undeliverable (e.g.
/// <c>BENEFICIARY_ACCOUNT_CLOSED</c>, <c>BENEFICIARY_ACCOUNT_NOT_FOUND</c>) — never free-text PII.</param>
/// <param name="UnappliedAt">The economic date the credit was recorded unapplied — a command-supplied
/// input, never a clock read in a fold (ADR-PC-023).</param>
public sealed record CreditUnapplied(
    string IntentId,
    string BeneficiaryAccountRef,
    Money Amount,
    string Reason,
    DateOnly UnappliedAt) : DomainEvent;

/// <summary>
/// A previously-unapplied credit was reapplied once a live destination existed (ADR-PC-043 slot 5).
/// In plain English: the account that could not receive the payout became receivable again (re-opened,
/// reactivated, or a re-target to a valid account), so the held IOU is discharged and the money lands.
/// This fact records the resolution so the escheat/IOU ledger closes the intent exactly once.
/// </summary>
/// <remarks>
/// <para>
/// <b>The resolution key is a pure function of the original intent (the double-pay guard).</b>
/// <paramref name="ResolutionIntentId"/> is <c>g(IntentId)</c> — derived by
/// <c>SettlementReferences.DeriveResolutionIntentId</c> from the SAME <paramref name="OriginalIntentId"/>,
/// never freshly minted (ADR-PC-043). So a late apply of the ORIGINAL credit and this
/// resolution both structurally reference the same intent, and the CA-apply <c>command_id</c> derived
/// from the resolution key collapses at <c>command_dedup</c> to one landing. A second
/// <c>CreditReapplied</c> for an already-resolved intent is a reconciliation signal, not a double-pay.
/// </para>
/// <para>Pure fold, no clock, no I/O (BENG001/002/003) — replay deterministic.</para>
/// </remarks>
/// <param name="ResolutionIntentId">The resolution key <c>g(OriginalIntentId)</c> from
/// <c>SettlementReferences.DeriveResolutionIntentId</c> — derived from the original intent, never fresh
/// (the structural double-pay guard). A structural token, never PII (ADR-PC-004).</param>
/// <param name="OriginalIntentId">The original economic-intent id whose unapplied credit this resolves
/// (from the matching <see cref="CreditUnapplied"/>) — a structural token, never PII (ADR-PC-004).</param>
/// <param name="BeneficiaryAccountRef">The opaque account the reapplied credit lands on (the now-live
/// destination, possibly re-targeted) — a reference the engine resolves internally, never PII (ADR-PC-004).</param>
/// <param name="Amount">The reapplied amount, integer-cents <see cref="Money"/> (ADR-PC-010) — equals the
/// held amount unless a partial resolution policy applies.</param>
/// <param name="ReappliedAt">The economic date the credit was reapplied — a command-supplied input,
/// never a clock read in a fold (ADR-PC-023).</param>
public sealed record CreditReapplied(
    string ResolutionIntentId,
    string OriginalIntentId,
    string BeneficiaryAccountRef,
    Money Amount,
    DateOnly ReappliedAt) : DomainEvent;

/// <summary>
/// The pure per-family fold for <see cref="CreditUnapplied"/>, generic over ANY family projection
/// <typeparamref name="TState"/> so it stays FAMILY-AGNOSTIC (ADR-PC-021): the engine owns this
/// handler; a family BINDS it against its own state via
/// <see cref="CrossCuttingEventRegistrations.For{TState}"/>.
/// </summary>
/// <remarks>
/// The fold returns the state UNCHANGED — the conformant shape, not an omission: the undeliverable-credit
/// IOU/escheat ledger is a SPINE-owned rebuildable fold over these operations facts (the same posture as
/// the hold ledger, ADR-PC-033), never family projection state. The source's payout-pending flag is the
/// FAMILY's own read-model concern, transitioned by the family's payout-pending lifecycle event, not by
/// this cross-cutting fact. Pure — no clock, no I/O, no randomness (BENG001/002/003) — so replay is
/// deterministic (CREDIT_UNAPPLIED_IS_ATTRIBUTED).
/// </remarks>
public sealed class CreditUnappliedHandler<TState> : IEventHandler<TState, CreditUnapplied>
{
    public HandlerResult<TState> Apply(TState state, CreditUnapplied @event)
        => HandlerResult<TState>.From(state);
}

/// <summary>
/// The pure per-family fold for <see cref="CreditReapplied"/> — the same no-op shape as
/// <see cref="CreditUnappliedHandler{TState}"/> for the same reason: the IOU/escheat ledger is
/// spine-owned (ADR-PC-043 slot 5); the family state is untouched by the resolution.
/// </summary>
public sealed class CreditReappliedHandler<TState> : IEventHandler<TState, CreditReapplied>
{
    public HandlerResult<TState> Apply(TState state, CreditReapplied @event)
        => HandlerResult<TState>.From(state);
}
