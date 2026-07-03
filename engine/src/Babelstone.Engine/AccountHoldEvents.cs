using Babelstone.FinancialTypes;

namespace Babelstone.Engine;

/// <summary>
/// The hold-lifecycle cross-cutting events (ADR-PC-033 slot 2): exactly three pure transitions,
/// <see cref="HoldPlaced"/> → <see cref="HoldCaptured"/> | <see cref="HoldExpired"/>, engine-declared
/// in the spine under the synthetic <c>operations</c> aggregate_type — the same posture as
/// <see cref="PackVersionMigrated"/> / <see cref="PersonalDataErasureRequested"/>. In plain English:
/// when an authorization is approved, money is EARMARKED (placed); when the matching settlement
/// arrives, the earmark ends and the money actually moves (captured); if the settlement never comes,
/// the earmark lapses with nothing posted (expired). Any transactional family records these same
/// three facts, so the engine declares them ONCE (it names no family, ADR-PC-021) rather than each
/// family re-deriving a <c>DepositFundsHeld</c>-style copy that would collide on the simple-name
/// codec (the exact failure the cross-cutting erasure event fixed, ADR-PC-004).
/// </summary>
/// <remarks>
/// <para>
/// STORE-ONLY today (ADR-IC-017): appended, folded, and replayable, with NO catalogued <c>.avsc</c>
/// — no named external consumer exists yet, so the events never reach the durable bus. Their
/// governed Avro wire SHAPES are authored as dormant contracts
/// (<c>contracts/avro/operations/Hold*.avsc.json</c>, the <c>_shared/Movement.avsc.json</c>
/// posture: outside the catalog glob by extension); bus promotion — renaming to <c>.avsc</c> plus
/// the AsyncAPI entry — is a deliberate later act with the first named consumer (ADR-PC-033 slot 6
/// / ADR-IC-017).
/// </para>
/// <para>
/// Two idempotency keys for two seams (ADR-PC-033 slot 4): the LIFECYCLE is keyed by
/// <c>HoldId</c> — all three events of one authorization carry the same one, so a re-delivered
/// release folds at most once — while the APPEND is keyed by the originating command's
/// <c>CommandId</c> (ADR-PC-029), carried on the append, never on this payload. NO PII
/// (ADR-PC-004): opaque ids, integer-cents <see cref="Money"/>, and input dates only; every date is
/// supplied by the command, never read from a clock in a fold (ADR-PC-023 — expiry in particular is
/// appended off a projection-derived read, not a timer).
/// </para>
/// <para>
/// The per-family folds below are NO-OPs by design: the active-hold set and both balances are
/// SPINE-owned rebuildable folds (the <see cref="AccountHoldProjector"/> over migration 0020 +
/// the movement ledger), not family projection state — the engine knows "this account has an
/// active hold of N", the family knows what authorization placed it (ADR-PC-033 slot 2). Each
/// family binds the no-op handlers via <see cref="CrossCuttingEventRegistrations.For{TState}"/> so
/// the events DECODE (and replay fail-closed) on every family stream that can carry them.
/// </para>
/// </remarks>

/// <summary>
/// An authorization was approved and its funds earmarked (ADR-PC-033 slot 2 / ADR-PC-030 stage 5).
/// From this event's sequence forward the hold is ACTIVE and lowers the account's available
/// balance — which is what makes concurrent authorization safe without locking: the first debit's
/// <c>HoldPlaced</c> lowers the fold before the second authorization is evaluated.
/// </summary>
/// <param name="InstanceId">The account-owning instance (stream) the hold rides — a structural id, not PII.</param>
/// <param name="HoldId">The hold's lifecycle dedup/correlation key (slot 4) — every later capture/expiry carries it.</param>
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
/// event, not by this fact — moves the accounting balance (ADR-PC-033 slot 2).
/// </summary>
/// <remarks>
/// A capture MAY be PARTIAL: <paramref name="CapturedAmount"/> less than the placed amount releases
/// the remainder (the whole hold leaves the active set; only the captured cents posted). The
/// partial/late/over-capture reconciliation POLICY is the transactional family's to own
/// (ADR-PC-033 §Residual-risks) — this fact records what settled, nothing more.
/// </remarks>
/// <param name="InstanceId">The account-owning instance (stream) the hold rides — a structural id, not PII.</param>
/// <param name="HoldId">The placed hold this capture releases (the slot-4 lifecycle key).</param>
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
/// with NO posting — no money moved (ADR-PC-033 slot 2). Appended by an operator/command-shell
/// action reading the projection-derived expiry horizon (<c>AccountBalanceReader.GetExpiryCandidatesAsync</c>),
/// NEVER by a clock-driven scheduler (ADR-PC-023) — which is what keeps the fold pure and replay
/// deterministic.
/// </summary>
/// <param name="InstanceId">The account-owning instance (stream) the hold rides — a structural id, not PII.</param>
/// <param name="HoldId">The placed hold this expiry releases (the slot-4 lifecycle key).</param>
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
/// set is the SPINE-owned <see cref="AccountHoldProjector"/> fold (ADR-PC-033 slot 2 — the hold
/// record is the cross-cutting fact the generic projector folds), never family projection state.
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
/// (ADR-PC-033 slot 2); the family state is untouched by the earmark's release.
/// </summary>
public sealed class HoldCapturedHandler<TState> : IEventHandler<TState, HoldCaptured>
{
    public HandlerResult<TState> Apply(TState state, HoldCaptured @event)
        => HandlerResult<TState>.From(state);
}

/// <summary>
/// The pure per-family fold for <see cref="HoldExpired"/> — the same no-op shape as
/// <see cref="HoldPlacedHandler{TState}"/> for the same reason: the hold ledger is spine-owned
/// (ADR-PC-033 slot 2); the family state is untouched by the earmark's lapse.
/// </summary>
public sealed class HoldExpiredHandler<TState> : IEventHandler<TState, HoldExpired>
{
    public HandlerResult<TState> Apply(TState state, HoldExpired @event)
        => HandlerResult<TState>.From(state);
}
