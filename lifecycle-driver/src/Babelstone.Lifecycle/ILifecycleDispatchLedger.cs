using Babelstone.Engine.Hosting;

namespace Babelstone.Lifecycle;

/// <summary>
/// The dispatch ledger's CLAIM port (ADR-PC-038 §Decision 2) — the seam through which the per-tick
/// <see cref="LifecycleSchedulePass"/> turns "this occurrence is due" into "and I am the ONE replica firing
/// it". In plain terms: with N driver replicas all ticking over the same forward calendar, every replica
/// finds the same due maturity/installment; the ledger hands the occurrence to exactly one of them (the
/// claim), the winner POSTs, and only a successful POST is recorded as dispatched. There is NO elected
/// leader — single-firing emerges from the atomic claim on the shared durable ledger, the estate's one
/// competing-consumers pattern (the saga dispatcher's and outbox relay's <c>FOR UPDATE SKIP LOCKED</c>
/// claim, reused per ADR-PC-038).
/// </summary>
/// <remarks>
/// <para>
/// <b>Claim → POST → record, in that order (ADR-PC-038 §Decision 3).</b> A claim is a LEASE, not a record:
/// it reserves the occurrence for this pass's fallible POST without marking it dispatched. Only
/// <see cref="ILifecycleDispatchClaim.RecordDispatchedAsync"/> — called AFTER the sink's POST succeeds —
/// commits the durable dispatched record (releasing the lease in the same commit). Disposing an
/// un-recorded claim RELEASES the occurrence, so a failed or crashed POST leaves it re-claimable by the
/// next pass — never reserve-before-POST, which would strand an occurrence on a transient failure. The
/// engine's <c>command_dedup</c> (ADR-PC-029 slot 4, <c>ENGINE_COMMAND_IDEMPOTENT</c>) stays the
/// authoritative correctness floor: a re-claimed re-POST replays the original outcome, one money leg.
/// </para>
/// <para>
/// The production implementation is <see cref="PostgresLifecycleDispatchLedger"/> — the durable
/// <c>lifecycle_dispatch_ledger</c> table whose row claim is the single-firing guard
/// (<c>LIFECYCLE_DRIVER_SINGLE_FIRING</c>) and whose persistence is the crash-survival + audit trail
/// (<c>LIFECYCLE_DISPATCH_LEDGER_DURABLE</c>). <see cref="InMemoryLifecycleDispatchLedger"/> is the
/// process-local test double with the same claim semantics.
/// </para>
/// </remarks>
public interface ILifecycleDispatchLedger
{
    /// <summary>
    /// Atomically claim <paramref name="decision"/>'s occurrence for dispatch, keyed on its canonical
    /// number-pinned dispatch id (<see cref="LifecycleDispatchId.Of"/>). Returns <see langword="null"/>
    /// when the occurrence is already recorded dispatched (the durable re-tick/restart no-op) OR is
    /// currently claimed by a competing replica mid-POST (skip it this tick; the winner records it or
    /// releases it). A non-null claim MUST be disposed: record it after a successful POST, or dispose it
    /// un-recorded to release the occurrence for the next pass.
    /// </summary>
    /// <param name="decision">The due occurrence to claim.</param>
    /// <param name="ct">Cancellation propagated from the host's stopping token.</param>
    Task<ILifecycleDispatchClaim?> TryClaimAsync(
        LifecycleCommandDecision decision, CancellationToken ct = default);
}

/// <summary>
/// One held claim on one due occurrence (ADR-PC-038 §Decision 3) — the lease the pass holds across the
/// fallible POST. Exactly one of two things happens to it: <see cref="RecordDispatchedAsync"/> after the
/// POST succeeds (the durable dispatched record commits and the lease releases, atomically), or disposal
/// without recording (the lease releases and the occurrence stays claimable — the crash/backpressure
/// path, safe because the engine's <c>command_dedup</c> dedupes the eventual re-POST).
/// </summary>
public interface ILifecycleDispatchClaim : IAsyncDisposable
{
    /// <summary>The claimed occurrence's canonical, server-derived, number-pinned dispatch id (LCD-1) —
    /// the SAME value the sink presents as the engine <c>Idempotency-Key</c>.</summary>
    Guid DispatchId { get; }

    /// <summary>
    /// Record the claimed occurrence as dispatched — called ONLY after the sink's POST succeeds. Commits
    /// the durable dispatched-at record and releases the claim in the same commit, so a crash between the
    /// engine's 2xx and this commit leaves the occurrence un-recorded and re-claimable (the engine
    /// deduping the re-POST — effectively-once, ADR-PC-038 §Decision 3).
    /// </summary>
    /// <param name="ct">Cancellation propagated from the host's stopping token.</param>
    Task RecordDispatchedAsync(CancellationToken ct = default);
}

/// <summary>
/// Derives the canonical, SERVER-DERIVED, number-pinned dispatch id for one due occurrence — the engine
/// <c>Idempotency-Key</c> the sink presents AND the dispatch ledger's claim key, the SAME value, via
/// <see cref="LifecycleCommandKey"/>.Derive over <c>(instance_id, command_kind, stable_occurrence_key)</c>
/// (ADR-PC-036 §Decision 1+3, LCD-1; the ledger claim key per ADR-PC-038 §Decision 1). Pure: the same
/// decision identity always yields the same id, so the ledger's "have I fired this?" and the engine's
/// "have I applied this?" agree on occurrence identity by construction.
/// </summary>
public static class LifecycleDispatchId
{
    /// <summary>The dispatch id for <paramref name="decision"/> — number-pinned (the recurring occurrence
    /// key is the stable installment NUMBER, never the due-date), so a re-dated or backfilled retry of
    /// occurrence N re-derives the SAME id (<c>LIFECYCLE_COMMAND_NUMBER_PINNED_IDEMPOTENT</c>).</summary>
    public static Guid Of(LifecycleCommandDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return LifecycleCommandKey.Derive(decision.InstanceId, decision.CommandKind, decision.OccurrenceKey);
    }
}
