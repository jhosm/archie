using Babelstone.FinancialTypes;

namespace Babelstone.Engine;

// The hold-lifecycle cross-cutting events (ADR-PC-033): exactly three pure transitions,
// HoldPlaced -> HoldCaptured | HoldExpired, engine-declared in the spine under the synthetic
// `operations` aggregate_type — the same posture as PackVersionMigrated /
// PersonalDataErasureRequested. In plain English: when an authorization is approved, money is
// EARMARKED (placed); when the matching settlement arrives, the earmark ends and the money
// actually moves (captured); if the settlement never comes, the earmark lapses with nothing
// posted (expired). Any transactional family records these same three facts, so the engine
// declares them ONCE (it names no family, ADR-PC-021) rather than each family re-deriving a
// per-family copy that would collide on the simple-name codec (the failure the cross-cutting
// erasure event fixed, ADR-PC-004).
//
// STORE-ONLY (ADR-IC-017): no catalogued .avsc, so the events never reach the durable bus. Their
// governed wire shapes live as dormant contracts under contracts/avro/operations/ — the
// promotion mechanics are that README's, not this file's.
//
// Two idempotency keys for two seams (ADR-PC-033): the LIFECYCLE is keyed by HoldId — all three
// events of one authorization carry the same one, so a re-delivered release folds at most once —
// while the APPEND is keyed by the originating command's CommandId (ADR-PC-029), carried on the
// append, never on this payload. NO PII (ADR-PC-004): opaque ids, integer-cents Money, and input
// dates only; every date is supplied by the command, never read from a clock in a fold
// (ADR-PC-023 — expiry in particular is appended off a projection-derived read, not a timer).
//
// The per-family folds below are NO-OPs by design: the active-hold set and both balances are
// SPINE-owned rebuildable folds (the AccountHoldProjector over migration 0020 + the movement
// ledger), not family projection state — the engine knows "this account has an active hold of
// N", the family knows what authorization placed it (ADR-PC-033). Each family binds the no-op
// handlers via CrossCuttingEventRegistrations.For<TState>() so the events DECODE (and replay
// fail-closed) on every family stream that can carry them.

/// <summary>
/// An authorization was approved and its funds earmarked (ADR-PC-033 / ADR-PC-030 stage 5).
/// From this event's sequence forward the hold is ACTIVE, and once the spine projection drive
/// folds it the account's available balance drops — which is what makes concurrent authorization
/// safe without locking, provided the deciding shell drains before it decides (read-your-writes):
/// the first debit's earmark reaches the fold before the second authorization reads it.
/// </summary>
/// <param name="InstanceId">The account-owning instance (stream) the hold rides — a structural id, not PII.</param>
/// <param name="HoldId">The hold's lifecycle dedup/correlation key (ADR-PC-033) — every later capture/expiry carries it.</param>
/// <param name="AccountRef">The opaque account whose available balance the earmark reduces — never PII (ADR-PC-004).</param>
/// <param name="Amount">The earmarked amount, integer-cents <see cref="Money"/> (ADR-PC-010).</param>
/// <param name="ValueDate">The economic date the hold takes effect — supplied by the command, never a clock read.</param>
public sealed record HoldPlaced(
    Guid InstanceId,
    string HoldId,
    string AccountRef,
    Money Amount,
    DateOnly ValueDate) : DomainEvent;

/// <summary>
/// The matching settlement/capture arrived: the hold leaves the active set (stops reducing the
/// available balance) and the posting <see cref="Movement"/> — carried by its own Movement-bearing
/// event, not by this fact — moves the accounting balance (ADR-PC-033).
/// </summary>
/// <remarks>
/// A capture MAY be PARTIAL: <paramref name="CapturedAmount"/> less than the placed amount releases
/// the remainder (the whole hold leaves the active set; only the captured cents posted). The
/// partial/late/over-capture reconciliation POLICY is the transactional family's to own
/// (ADR-PC-033) — this fact records what settled, nothing more.
/// </remarks>
/// <param name="InstanceId">The account-owning instance (stream) the hold rides — a structural id, not PII.</param>
/// <param name="HoldId">The placed hold this capture releases (the ADR-PC-033 lifecycle key).</param>
/// <param name="AccountRef">The opaque account the earmark applied to — never PII (ADR-PC-004).</param>
/// <param name="CapturedAmount">The settled amount, integer-cents <see cref="Money"/> — may be less than the placed amount.</param>
/// <param name="ValueDate">The economic date the capture settled — an input date, never a clock read.</param>
public sealed record HoldCaptured(
    Guid InstanceId,
    string HoldId,
    string AccountRef,
    Money CapturedAmount,
    DateOnly ValueDate) : DomainEvent;

/// <summary>
/// The hold timed out before capture: it leaves the active set, restoring the available balance,
/// with NO posting — no money moved (ADR-PC-033). Appended by an operator/command-shell
/// action reading the projection-derived expiry horizon (<c>AccountBalanceReader.GetExpiryCandidatesAsync</c>),
/// NEVER by a clock-driven scheduler (ADR-PC-023) — which is what keeps the fold pure and replay
/// deterministic.
/// </summary>
/// <param name="InstanceId">The account-owning instance (stream) the hold rides — a structural id, not PII.</param>
/// <param name="HoldId">The placed hold this expiry releases (the ADR-PC-033 lifecycle key).</param>
/// <param name="AccountRef">The opaque account whose available balance the release restores — never PII (ADR-PC-004).</param>
/// <param name="ValueDate">The economic date the expiry took effect — an input from the expiry read, never a clock read.</param>
public sealed record HoldExpired(
    Guid InstanceId,
    string HoldId,
    string AccountRef,
    DateOnly ValueDate) : DomainEvent;

/// <summary>
/// The pure per-family fold for <see cref="HoldPlaced"/>, generic over ANY family projection
/// <typeparamref name="TState"/> so it stays FAMILY-AGNOSTIC (ADR-PC-021): the engine owns this
/// handler; a family BINDS it against its own state via
/// <see cref="CrossCuttingEventRegistrations.For{TState}"/>.
/// </summary>
/// <remarks>
/// The fold returns the state UNCHANGED — the conformant shape, not an omission: the active-hold
/// set is the SPINE-owned <see cref="AccountHoldProjector"/> fold (ADR-PC-033 — the hold record
/// is the cross-cutting fact the generic projector folds), never family projection state.
/// Pure — no clock, no I/O, no randomness (BENG001/002/003) — so replay is deterministic
/// (HOLD_LIFECYCLE_PURE).
/// </remarks>
public sealed class HoldPlacedHandler<TState> : IEventHandler<TState, HoldPlaced>
{
    public HandlerResult<TState> Apply(TState state, HoldPlaced @event)
        => HandlerResult<TState>.From(state);
}

/// <summary>
/// The pure per-family fold for <see cref="HoldCaptured"/> — the same no-op shape as
/// <see cref="HoldPlacedHandler{TState}"/> for the same reason: the hold ledger is spine-owned
/// (ADR-PC-033); the family state is untouched by the earmark's release.
/// </summary>
public sealed class HoldCapturedHandler<TState> : IEventHandler<TState, HoldCaptured>
{
    public HandlerResult<TState> Apply(TState state, HoldCaptured @event)
        => HandlerResult<TState>.From(state);
}

/// <summary>
/// The pure per-family fold for <see cref="HoldExpired"/> — the same no-op shape as
/// <see cref="HoldPlacedHandler{TState}"/> for the same reason: the hold ledger is spine-owned
/// (ADR-PC-033); the family state is untouched by the earmark's lapse.
/// </summary>
public sealed class HoldExpiredHandler<TState> : IEventHandler<TState, HoldExpired>
{
    public HandlerResult<TState> Apply(TState state, HoldExpired @event)
        => HandlerResult<TState>.From(state);
}
