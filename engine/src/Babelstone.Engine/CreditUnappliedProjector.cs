using Babelstone.EventStore;

namespace Babelstone.Engine;

/// <summary>
/// A credit resolution (<see cref="CreditReapplied"/>) that transitioned nothing — the detail the
/// projector hands its surfacing callback so the host can log it with the identifiers a reconciliation
/// needs (ADR-PC-043: surfaced, not silently absorbed; mirroring <see cref="HoldReleaseAnomaly"/> and
/// <see cref="FreezeLiftAnomaly"/>). Structural only, never PII (ADR-PC-004): an intent id, the
/// no-op kind, and the resolving event's identity.
/// </summary>
/// <param name="IntentId">The original economic-intent id the resolution named.</param>
/// <param name="Kind">Which no-op this was — see <see cref="CreditResolutionResult"/> (never
/// <see cref="CreditResolutionResult.Transitioned"/> here).</param>
/// <param name="ResolvingStreamId">The stream that carried the <see cref="CreditReapplied"/> event.</param>
/// <param name="ResolvingSequence">The resolving event's per-stream sequence.</param>
public sealed record CreditResolutionAnomaly(
    string IntentId,
    CreditResolutionResult Kind,
    Guid ResolvingStreamId,
    long ResolvingSequence);

/// <summary>
/// The spine-owned undeliverable-credit (IOU / escheat) fold projector (ADR-PC-043 slot 5): folds the
/// two credit-lifecycle events — <see cref="CreditUnapplied"/> → <see cref="CreditReapplied"/> — into
/// the <c>intent_id</c>-keyed IOU read model (migration 0024) an operator queries for "which credits
/// are still owed, to whom, and how old". In plain English: when a matured payout has nowhere to land
/// the engine records a NAMED IOU (CreditUnapplied); when a live destination later exists it records
/// the resolution (CreditReapplied) and the IOU leaves the outstanding set. This keeps the rebuildable
/// answer to "what does the bank still owe", mirroring the account-hold and account-freeze ledgers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Family-agnostic by construction (ENGINE_FAMILY_AGNOSTIC, ADR-PC-021).</b> It pattern-matches
/// the two SPINE cross-cutting event records and writes opaque <c>intent_id</c>/<c>beneficiary_ref</c>
/// primitives — never a family-typed shape. Any family whose payout can be undeliverable appends the
/// same two facts; this one projector folds them all.
/// </para>
/// <para>
/// <b>COMMUTATIVE fold — rebuild-deterministic regardless of stream fold order (ADR-PC-043 slot 3).</b>
/// The credit events are keyed by an economic INTENT id, NOT an InstanceId, and the resolution may
/// re-target a DIFFERENT beneficiary account — so unlike the ADR-PC-033 hold lifecycle (single-stream
/// by construction, guarded by <c>RequireOwnStream</c>) the open and the resolve are NOT guaranteed to
/// ride one stream, and the drainer folds streams in UNORDERED sequence. So a resolution can be folded
/// BEFORE its open. The fold is made order-independent in the store: a resolution with no open row yet
/// records a RESOLVED tombstone, and a later open no-ops on the <c>intent_id</c> conflict rather than
/// re-opening — either order converges to the same terminal state, so a full truncate-then-refold
/// re-derives the same OUTSTANDING set as the incremental build.
/// </para>
/// <para>
/// <b>Idempotent on the intent's own lifecycle key; a no-op resolution is SURFACED (ADR-PC-043).</b> A
/// re-delivered <see cref="CreditUnapplied"/> is absorbed by the store's <c>intent_id</c> conflict
/// no-op. A DUPLICATE resolution of an already-resolved intent transitions nothing — never a
/// double-pay — but is REPORTED, not swallowed: the intent/stream identifiers reach the host's
/// structured log through the <c>onResolutionAnomaly</c> callback (the same host-wired-sink idiom as
/// the hold/freeze projectors) as <see cref="CreditResolutionResult.AlreadyResolved"/>.
/// </para>
/// <para>
/// <b>The resolution key is the double-pay guard (ADR-PC-043).</b> A <see cref="CreditReapplied"/>
/// carries both the <c>OriginalIntentId</c> (the IOU it resolves) and the
/// <c>ResolutionIntentId = g(OriginalIntentId)</c> derived from it, never freshly minted. The fold
/// matches the resolution to its IOU by <c>OriginalIntentId</c> (the ledger's key) AND ENFORCES that
/// the carried resolution key was derived from that same intent — a resolution whose key is not
/// <c>g(OriginalIntentId)</c> is refused loud, because a fresh (non-derived) key would break the
/// structural collapse of a late original apply and the resolution to exactly one landing.
/// </para>
/// <para>
/// <b>Ordering + determinism (ADR-PC-043 slot 3).</b> Every column is event-derived (no clock, no
/// randomness) — the dates are command-supplied inputs (ADR-PC-023) — so truncate-then-refold
/// reproduces read-identical rows. IOU AGE is not stored: it is a projection-derived read against an
/// operator-supplied <c>as_of</c> horizon at query time, never a clock-manufactured column.
/// </para>
/// </remarks>
public sealed class CreditUnappliedProjector(
    IIouLedgerStore store,
    Action<CreditResolutionAnomaly>? onResolutionAnomaly = null) : ISpineProjector
{
    /// <summary>
    /// Fold one decoded event into the IOU set. A no-op unless <paramref name="event"/> is one of the
    /// two credit-lifecycle records.
    /// </summary>
    /// <param name="streamId">The stream the event was appended to (resolution/unapply provenance).</param>
    /// <param name="sequenceNumber">The event's per-stream sequence (resolution/unapply provenance).</param>
    /// <param name="event">The already-decoded domain event; folded only if it is a credit-lifecycle fact.</param>
    /// <exception cref="InvalidOperationException">A <see cref="CreditReapplied"/> carried a resolution
    /// key not derived from its own original intent — the double-pay guard the fold's correctness rests on.</exception>
    public async Task ApplyAsync(
        Guid streamId, long sequenceNumber, DomainEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        switch (@event)
        {
            case CreditUnapplied unapplied:
                await store.RecordUnappliedAsync(
                    new UndeliverableCreditRow(
                        IntentId: unapplied.IntentId,
                        BeneficiaryRef: unapplied.BeneficiaryAccountRef,
                        AmountCents: unapplied.Amount.Cents,
                        Reason: unapplied.Reason,
                        UnappliedAt: unapplied.UnappliedAt,
                        State: "OUTSTANDING",
                        UnappliedStreamId: streamId,
                        UnappliedSequence: sequenceNumber),
                    ct);
                break;

            case CreditReapplied reapplied:
                RequireDerivedResolutionKey(reapplied);
                Surface(
                    await store.ResolveAsync(
                        reapplied.OriginalIntentId,
                        reapplied.ResolutionIntentId,
                        reapplied.BeneficiaryAccountRef,
                        reapplied.Amount.Cents,
                        reapplied.ReappliedAt,
                        streamId,
                        sequenceNumber,
                        ct),
                    reapplied.OriginalIntentId, streamId, sequenceNumber);
                break;
        }
    }

    /// <summary>Truncate the IOU set for a rebuild (truncate-then-refold, ADR-PC-043).</summary>
    public Task ResetForRebuildAsync(CancellationToken ct = default) => store.TruncateAsync(ct);

    // The double-pay guard (ADR-PC-043): a resolution's key must be g(OriginalIntentId), derived from
    // the SAME intent it resolves — never a fresh value. If a producer minted a non-derived key the
    // structural collapse of a late original apply and the resolution to one landing is broken, so it
    // is refused loud here rather than folded as if the IOU were safely resolved. The derivation is a
    // pure prefix of the intent id (SettlementReferences.DeriveResolutionIntentId lives in the
    // orchestrator substrate, which the engine spine does not depend on — so the guard checks the
    // structural relationship, that the resolution key ENDS WITH the original intent, not the exact
    // helper, keeping the family → substrate arrow one-way, ADR-PC-021).
    private static void RequireDerivedResolutionKey(CreditReapplied reapplied)
    {
        if (!reapplied.ResolutionIntentId.EndsWith(reapplied.OriginalIntentId, StringComparison.Ordinal)
            || reapplied.ResolutionIntentId.Length == reapplied.OriginalIntentId.Length)
        {
            throw new InvalidOperationException(
                $"CreditReapplied for intent '{reapplied.OriginalIntentId}' carries resolution key "
                + $"'{reapplied.ResolutionIntentId}' that is not derived from the original intent — the "
                + "resolution key must be g(OriginalIntentId), never freshly minted (the double-pay guard, "
                + "ADR-PC-043).");
        }
    }

    // A non-normal resolution is surfaced, never silently absorbed (ADR-PC-043): a DUPLICATE resolution
    // of an already-resolved intent (AlreadyResolved) transitioned nothing — a reconciliation signal the
    // host's warning log receives via the callback. A resolve-before-open is NOT surfaced: it is the
    // commutative tombstone path and folds as the normal Transitioned outcome (never a lost resolution).
    private void Surface(
        CreditResolutionResult result, string intentId, Guid streamId, long sequence)
    {
        if (result == CreditResolutionResult.Transitioned)
        {
            return;
        }

        onResolutionAnomaly?.Invoke(new CreditResolutionAnomaly(intentId, result, streamId, sequence));
    }
}
