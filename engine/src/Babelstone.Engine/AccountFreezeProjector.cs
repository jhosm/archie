using Babelstone.EventStore;

namespace Babelstone.Engine;

/// <summary>
/// An unfreeze (<see cref="AccountUnfrozen"/>) that transitioned nothing — the detail the projector
/// hands its surfacing callback so the host can log it with the identifiers a reconciliation needs
/// (ADR-PC-041, mirroring <see cref="HoldReleaseAnomaly"/>). Structural only, never PII (ADR-PC-004).
/// </summary>
/// <param name="FreezeId">The freeze the unfreeze named.</param>
/// <param name="Kind">Which no-op this was (never <see cref="FreezeLiftResult.Transitioned"/> here).</param>
/// <param name="LiftingStreamId">The stream that carried the <see cref="AccountUnfrozen"/> event.</param>
/// <param name="LiftingSequence">The unfreeze event's per-stream sequence.</param>
public sealed record FreezeLiftAnomaly(
    string FreezeId,
    FreezeLiftResult Kind,
    Guid LiftingStreamId,
    long LiftingSequence);

/// <summary>
/// The spine-owned generic frozen-predicate fold projector (ADR-PC-041): folds the two freeze
/// lifecycle events — <see cref="AccountFrozen"/> → <see cref="AccountUnfrozen"/> — into the
/// instance-keyed frozen-predicate read model (migration 0022) the stages-3–5 authorization decider
/// consults. In plain English: as compliance freezes are placed and lifted, this keeps the
/// rebuildable answer to "is this instance frozen right now, and why", so the freeze gate is a fold
/// read, never a stored mutable flag.
/// </summary>
/// <remarks>
/// <para>
/// <b>Family-agnostic by construction (ENGINE_FAMILY_AGNOSTIC, ADR-PC-021).</b> It pattern-matches
/// the two SPINE event records and writes opaque <c>freeze_id</c>/<c>instance_id</c> primitives —
/// never a family-typed shape.
/// </para>
/// <para>
/// <b>Idempotent on the freeze's own lifecycle key; a no-op unfreeze is SURFACED (ADR-PC-041).</b> A
/// re-delivered <see cref="AccountFrozen"/> is absorbed by the store's <c>freeze_id</c> conflict
/// no-op. An unfreeze that transitions nothing folds as a no-op — never a double-lift — but is
/// REPORTED through the <c>onLiftAnomaly</c> callback (the same host-wired-sink idiom as the hold
/// projector), distinguishing the fold-order error (<see cref="FreezeLiftResult.NeverFrozen"/>) from
/// the duplicate/late lift (<see cref="FreezeLiftResult.AlreadyLifted"/>).
/// </para>
/// <para>
/// <b>Ordering (ADR-PC-041).</b> One freeze's lifecycle events ride ONE instance stream, so
/// per-stream append order places every <see cref="AccountFrozen"/> before its lift, and a rebuild
/// re-derives the same terminal state per <c>freeze_id</c>. That precondition is ENFORCED, not
/// assumed: <see cref="ApplyAsync"/> refuses a freeze event whose <c>InstanceId</c> differs from the
/// stream it was appended to. Every column is event-derived (no clock, no randomness), so
/// truncate-then-refold reproduces read-identical rows (ACCOUNT_BALANCE_IS_A_FOLD / DETERMINISM_GATE).
/// </para>
/// </remarks>
public sealed class AccountFreezeProjector(
    IAccountFreezeStore store,
    Action<FreezeLiftAnomaly>? onLiftAnomaly = null) : ISpineProjector
{
    /// <summary>
    /// Fold one decoded event into the frozen-predicate set. A no-op unless <paramref name="event"/>
    /// is one of the two freeze-lifecycle records.
    /// </summary>
    public async Task ApplyAsync(
        Guid streamId, long sequenceNumber, DomainEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        switch (@event)
        {
            case AccountFrozen frozen:
                RequireOwnStream(streamId, frozen.InstanceId, nameof(AccountFrozen), frozen.FreezeId);
                await store.FreezeAsync(
                    new AccountFreezeRow(
                        FreezeId: frozen.FreezeId,
                        InstanceId: frozen.InstanceId,
                        FreezeReason: frozen.FreezeReason,
                        ComplianceActor: frozen.ComplianceActor,
                        FreezeExpiresAt: frozen.FreezeExpiresAt,
                        State: "ACTIVE",
                        PlacedStreamId: streamId,
                        PlacedSequence: sequenceNumber),
                    ct);
                break;

            case AccountUnfrozen unfrozen:
                RequireOwnStream(streamId, unfrozen.InstanceId, nameof(AccountUnfrozen), unfrozen.FreezeId);
                Surface(
                    await store.UnfreezeAsync(
                        unfrozen.FreezeId, streamId, sequenceNumber, unfrozen.UnfreezeActor,
                        unfrozen.UnfreezeReason, ct),
                    unfrozen.FreezeId, streamId, sequenceNumber);
                break;
        }
    }

    /// <summary>Truncate the freeze set for a rebuild (truncate-then-refold, ADR-PC-041).</summary>
    public Task ResetForRebuildAsync(CancellationToken ct = default) => store.TruncateAsync(ct);

    // The single-stream precondition (ADR-PC-041, mirroring ADR-PC-033): a freeze's whole lifecycle
    // rides its instance stream, which is what makes per-stream fold order — and the rebuilt terminal
    // state per freeze_id — deterministic. A foreign-stream freeze event is refused loud.
    private static void RequireOwnStream(Guid streamId, Guid instanceId, string eventType, string freezeId)
    {
        if (streamId != instanceId)
        {
            throw new InvalidOperationException(
                $"{eventType} for freeze '{freezeId}' names instance '{instanceId}' but was appended to "
                + $"stream '{streamId}' — a freeze's lifecycle must ride its own instance stream (ADR-PC-041).");
        }
    }

    // An unfreeze that transitioned nothing folds as a no-op but never SILENTLY (ADR-PC-041): hand
    // the identifiers to the host's warning log via the callback.
    private void Surface(FreezeLiftResult result, string freezeId, Guid streamId, long sequence)
    {
        if (result == FreezeLiftResult.Transitioned)
        {
            return;
        }

        onLiftAnomaly?.Invoke(new FreezeLiftAnomaly(freezeId, result, streamId, sequence));
    }
}
