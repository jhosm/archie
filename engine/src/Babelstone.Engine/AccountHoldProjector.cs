using System.Diagnostics.Metrics;
using Babelstone.EventStore;
using Babelstone.Telemetry;

namespace Babelstone.Engine;

/// <summary>
/// A hold release (<see cref="HoldCaptured"/> / <see cref="HoldExpired"/>) that transitioned
/// nothing — the detail the projector hands its surfacing callback so the host can log it with the
/// identifiers a reconciliation needs (ADR-PC-033: surfaced, not silently absorbed). Structural
/// only, never PII (ADR-PC-004): a hold id, an event-type name, and the releasing event's identity.
/// </summary>
/// <param name="HoldId">The hold the release named.</param>
/// <param name="Kind">Which no-op this was — see <see cref="HoldReleaseResult"/> (never
/// <see cref="HoldReleaseResult.Transitioned"/> here).</param>
/// <param name="ReleaseEventType">The releasing event's CLR name (<c>HoldCaptured</c> / <c>HoldExpired</c>).</param>
/// <param name="ReleasingStreamId">The stream that carried the releasing event.</param>
/// <param name="ReleasingSequence">The releasing event's per-stream sequence.</param>
public sealed record HoldReleaseAnomaly(
    string HoldId,
    HoldReleaseResult Kind,
    string ReleaseEventType,
    Guid ReleasingStreamId,
    long ReleasingSequence);

/// <summary>
/// The spine-owned generic active-hold fold projector (ADR-PC-033): folds the three
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
/// <b>Idempotent on the hold's own lifecycle key; a no-op release is SURFACED (ADR-PC-033;
/// HOLD_LIFECYCLE_PURE).</b> A re-delivered <see cref="HoldPlaced"/> is absorbed by the store's
/// <c>hold_id</c> conflict no-op. A release that transitions nothing still folds as a no-op —
/// never a double-release — but is REPORTED, not swallowed: a warning-tier signal distinguishing
/// the fold-order error (<see cref="HoldReleaseResult.NeverPlaced"/>) from the duplicate/late
/// release (<see cref="HoldReleaseResult.AlreadyReleased"/>) goes to the
/// <c>hold_release_anomalies_total</c> counter, and the hold/stream identifiers reach the host's
/// structured log through the <c>onReleaseAnomaly</c> callback (the same host-wired-sink idiom as
/// the runtime's snapshot-error callback — the kernel stays logging-library-agnostic). Acting on
/// the signal is the transactional family's reconciliation lane (ADR-PC-033); emitting it is this
/// fold's duty.
/// </para>
/// <para>
/// <b>Ordering (ADR-PC-033).</b> One hold's lifecycle events ride ONE account-owning instance
/// stream, so per-stream append order — the order the <see cref="SpineProjectionDrainer"/> folds
/// in — places every <see cref="HoldPlaced"/> before its release, and a rebuild re-derives the
/// same terminal state per <c>hold_id</c>. That precondition is ENFORCED, not assumed:
/// <see cref="ApplyAsync"/> refuses a hold event whose <c>InstanceId</c> differs from the stream
/// it was appended to. Every column is event-derived (no clock, no randomness), so
/// truncate-then-refold reproduces read-identical rows (ACCOUNT_BALANCE_IS_A_FOLD).
/// </para>
/// </remarks>
public sealed class AccountHoldProjector(
    IAccountHoldStore store,
    Action<HoldReleaseAnomaly>? onReleaseAnomaly = null) : ISpineProjector
{
    /// <summary>
    /// Fold one decoded event into the active-hold set. A no-op unless <paramref name="event"/> is
    /// one of the three hold-lifecycle records.
    /// </summary>
    /// <param name="streamId">The stream the event was appended to (release/placement provenance).</param>
    /// <param name="sequenceNumber">The event's per-stream sequence (release/placement provenance).</param>
    /// <param name="event">The already-decoded domain event; folded only if it is a hold-lifecycle fact.</param>
    /// <exception cref="InvalidOperationException">A hold event rode a stream other than its own
    /// <c>InstanceId</c> — the single-stream ordering precondition the fold's determinism rests on.</exception>
    public async Task ApplyAsync(
        Guid streamId, long sequenceNumber, DomainEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        switch (@event)
        {
            case HoldPlaced placed:
                RequireOwnStream(streamId, placed.InstanceId, nameof(HoldPlaced), placed.HoldId);
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
                RequireOwnStream(streamId, captured.InstanceId, nameof(HoldCaptured), captured.HoldId);
                Surface(
                    await store.CaptureAsync(
                        captured.HoldId, captured.CapturedAmount.Cents, streamId, sequenceNumber, ct),
                    captured.HoldId, nameof(HoldCaptured), streamId, sequenceNumber);
                break;

            case HoldExpired expired:
                RequireOwnStream(streamId, expired.InstanceId, nameof(HoldExpired), expired.HoldId);
                Surface(
                    await store.ExpireAsync(expired.HoldId, streamId, sequenceNumber, ct),
                    expired.HoldId, nameof(HoldExpired), streamId, sequenceNumber);
                break;

            // The ADR-PC-041 legal-hold lifecycle folds into the SAME active-hold set as an
            // authorization hold (kind = LEGAL), so it lowers available balance for free. FundsHeld
            // carries only an InstanceId, so the account_ref is the instance itself (the degenerate
            // single-account 1:1 mapping every IAccount seam exposes today) — the same stream, so the
            // RequireOwnStream precondition is trivially met.
            case FundsHeld held:
                RequireOwnStream(streamId, held.InstanceId, nameof(FundsHeld), held.HoldId);
                await store.PlaceLegalAsync(
                    new AccountHoldRow(
                        HoldId: held.HoldId,
                        AccountRef: held.InstanceId.ToString(),
                        AmountCents: held.HeldAmount.Cents,
                        ValueDate: null,
                        State: "ACTIVE",
                        PlacedStreamId: streamId,
                        PlacedSequence: sequenceNumber,
                        Kind: "LEGAL",
                        LegalReference: held.LegalReference,
                        ExpiresAt: held.HoldExpiresAt),
                    ct);
                break;

            case FundsReleased released:
                RequireOwnStream(streamId, released.InstanceId, nameof(FundsReleased), released.HoldId);
                Surface(
                    await store.ReleaseLegalAsync(released.HoldId, streamId, sequenceNumber, ct),
                    released.HoldId, nameof(FundsReleased), streamId, sequenceNumber);
                break;
        }
    }

    /// <summary>Truncate the hold set for a rebuild (truncate-then-refold, ADR-PC-033).</summary>
    public Task ResetForRebuildAsync(CancellationToken ct = default) => store.TruncateAsync(ct);

    // The single-stream precondition (ADR-PC-033 per-account ordering): a hold's whole lifecycle
    // rides its account-owning instance stream, which is what makes per-stream fold order — and
    // hence the rebuilt terminal state per hold_id — deterministic. A producer that appends a hold
    // event onto a FOREIGN stream would let rebuild iteration order decide the outcome, so it is
    // refused loud here rather than folded nondeterministically.
    private static void RequireOwnStream(Guid streamId, Guid instanceId, string eventType, string holdId)
    {
        if (streamId != instanceId)
        {
            throw new InvalidOperationException(
                $"{eventType} for hold '{holdId}' names instance '{instanceId}' but was appended to "
                + $"stream '{streamId}' — a hold's lifecycle must ride its own instance stream (ADR-PC-033).");
        }
    }

    // A non-normal release is surfaced, never silently absorbed (ADR-PC-033 / ADR-PC-037 §D4): the
    // no-op releases (AlreadyReleased/NeverPlaced) transitioned nothing, and an over-capture DID
    // transition (the money moved) yet exceeded the held amount — all three are reconciliation signals,
    // counted (tagged by kind) with the identifiers handed to the host's warning log via the callback.
    // Only a plain Transitioned is the normal, silent outcome.
    private void Surface(
        HoldReleaseResult result, string holdId, string releaseEventType, Guid streamId, long sequence)
    {
        if (result == HoldReleaseResult.Transitioned)
        {
            return;
        }

        AccountHoldMetrics.RecordReleaseAnomaly(result);
        onReleaseAnomaly?.Invoke(new HoldReleaseAnomaly(holdId, result, releaseEventType, streamId, sequence));
    }
}

/// <summary>
/// The active-hold projector's anomaly SLI: <c>hold_release_anomalies_total</c>, bumped when a
/// <c>HoldCaptured</c>/<c>HoldExpired</c> folds as a no-op (ADR-PC-033 — surfaced, never silently
/// absorbed). Lives on the shared <see cref="BabelstoneTelemetry.Meter"/> (ADR-IC-007) so a host
/// turns it on with one <c>AddMeter</c>; with no listener attached <c>Add</c> is a near-zero-cost
/// no-op. Emitted from the impure projector shell only — never a pure fold — so replay determinism
/// is untouched (ADR-PC-010).
/// </summary>
internal static class AccountHoldMetrics
{
    private static readonly Counter<long> ReleaseAnomalies =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.HoldReleaseAnomaliesMetric,
            description: "Hold releases needing reconciliation — never_placed is a fold-order error, already_released a duplicate/late release, over_captured a capture exceeding the held amount (ADR-PC-033 / ADR-PC-037 §D4).");

    /// <summary>One reconciliation signal, tagged by its closed-set kind. The hold id rides the host's
    /// structured warning log (unbounded cardinality never becomes a metric dimension).</summary>
    public static void RecordReleaseAnomaly(HoldReleaseResult kind) =>
        ReleaseAnomalies.Add(1, new KeyValuePair<string, object?>(
            BabelstoneAttributes.HoldReleaseAnomalyKindTag,
            kind switch
            {
                HoldReleaseResult.NeverPlaced => "never_placed",
                HoldReleaseResult.TransitionedOverCaptured => "over_captured",
                _ => "already_released",
            }));
}
