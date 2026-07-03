using Babelstone.EventStore;

namespace Babelstone.Engine;

/// <summary>
/// The spine-owned generic active-hold fold projector (ADR-PC-033 slots 1–3): folds the three
/// hold-lifecycle events — <see cref="HoldPlaced"/> → <see cref="HoldCaptured"/> |
/// <see cref="HoldExpired"/> — into the <c>account_ref</c>-keyed active-hold read model
/// (migration 0020) the available-balance fold subtracts. In plain English: as authorizations
/// place, capture, and expire earmarks, this keeps the rebuildable answer to "how much of this
/// account's money is currently set aside", so
/// <c>available balance = accounting balance − Σ(active holds)</c> is always a fold read, never a
/// stored number.
/// </summary>
/// <remarks>
/// <para>
/// <b>Family-agnostic by construction (ENGINE_FAMILY_AGNOSTIC, ADR-PC-021).</b> It pattern-matches
/// the three SPINE event records and writes opaque <c>hold_id</c>/<c>account_ref</c> primitives —
/// never a family-typed shape. Any family's authorization path appends the same three facts; this
/// one projector folds them all.
/// </para>
/// <para>
/// <b>Idempotent on the hold's own lifecycle key (ADR-PC-033 slot 4; HOLD_LIFECYCLE_PURE).</b>
/// A re-delivered <see cref="HoldPlaced"/> is absorbed by the store's <c>hold_id</c> conflict
/// no-op; a re-delivered (or duplicate) <see cref="HoldCaptured"/>/<see cref="HoldExpired"/>
/// transitions zero rows because the row already left ACTIVE — a no-op, never a double-release.
/// A release for a hold never placed is likewise a fold no-op here: the fold trusts its input
/// stream, and the mismatch is a reconciliation surface the transactional family owns
/// (ADR-PC-033 slot 5 / §Residual-risks), not a fold failure.
/// </para>
/// <para>
/// <b>Ordering (ADR-PC-033 slot 3).</b> One hold's lifecycle events ride one account-owning
/// instance stream, so per-stream append order — the order the
/// <see cref="SpineProjectionDrainer"/> folds in — places every <see cref="HoldPlaced"/> before
/// its release, and a rebuild re-derives the same terminal state per <c>hold_id</c>. Every column
/// is event-derived (no clock, no randomness), so truncate-then-refold is deterministic
/// (ACCOUNT_BALANCE_IS_A_FOLD).
/// </para>
/// </remarks>
public sealed class AccountHoldProjector(IAccountHoldStore store) : ISpineProjector
{
    /// <summary>
    /// Fold one decoded event into the active-hold set. A no-op unless <paramref name="event"/> is
    /// one of the three hold-lifecycle records.
    /// </summary>
    /// <param name="streamId">The stream the event was appended to (release/placement provenance).</param>
    /// <param name="sequenceNumber">The event's per-stream sequence (release/placement provenance).</param>
    /// <param name="event">The already-decoded domain event; folded only if it is a hold-lifecycle fact.</param>
    public async Task ApplyAsync(
        Guid streamId, long sequenceNumber, DomainEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        switch (@event)
        {
            case HoldPlaced placed:
                await store.PlaceAsync(
                    new AccountHoldRow(
                        HoldId: placed.HoldId,
                        AccountRef: placed.AccountRef,
                        AmountCents: placed.Amount.Cents,
                        ValueDate: placed.ValueDate,
                        State: "ACTIVE",
                        PlacedStreamId: streamId,
                        PlacedSequence: sequenceNumber),
                    ct);
                break;

            case HoldCaptured captured:
                // A false return is the deliberate no-op path (already released, or never placed):
                // the guarantee is "at most one release per hold_id"; surfacing the mismatch is the
                // family's reconciliation lane (ADR-PC-033 §Residual-risks), not this fold's.
                await store.CaptureAsync(
                    captured.HoldId, captured.CapturedAmount.Cents, streamId, sequenceNumber, ct);
                break;

            case HoldExpired expired:
                await store.ExpireAsync(expired.HoldId, streamId, sequenceNumber, ct);
                break;
        }
    }

    /// <summary>Truncate the hold set for a rebuild (truncate-then-refold, ADR-PC-033 slot 3).</summary>
    public Task ResetForRebuildAsync(CancellationToken ct = default) => store.TruncateAsync(ct);
}
