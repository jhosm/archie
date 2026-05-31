using Babelstone.EventStore;

namespace Babelstone.Engine;

/// <summary>
/// Drives a projection forward from the event log (two-modes §5.4 async path). Because the
/// events table has no cluster-wide total order, draining is PER STREAM: for each stream of the
/// runner's family, fold the tail from the per-stream checkpoint forward, then advance the
/// checkpoint. Project-THEN-checkpoint, so a crash mid-stream replays the last events — safe
/// because <see cref="ProjectionRunner{TState}"/> is idempotent (the <c>source_sequence</c> guard).
/// The runtime owns the clock (ADR-PC-010 §P5); handlers never read it.
/// </summary>
public sealed class ProjectionDrainer(
    IEventStore eventStore,
    IProjectionCheckpointStore checkpoints,
    TimeProvider clock)
{
    /// <summary>Drains one full pass for a runner across all its family's streams. Returns events folded.</summary>
    public async Task<int> DrainOnceAsync(IProjectionRunner runner, CancellationToken ct = default)
    {
        var streamIds = await eventStore.ReadStreamIdsAsync(runner.Family, ct);
        var folded = 0;

        foreach (var streamId in streamIds)
        {
            var checkpoint = await checkpoints.ReadAsync(runner.Kind, streamId, ct);
            var lastSequence = checkpoint?.LastSequenceNumber ?? -1;
            var fromSequence = lastSequence + 1;
            var advancedTo = lastSequence;

            await foreach (var envelope in eventStore.LoadAsync(streamId, fromSequence, ct))
            {
                await runner.ApplyAsync(envelope, ct);
                advancedTo = envelope.SequenceNumber;
                folded++;
            }

            if (advancedTo > lastSequence)
            {
                // The checkpoint is informational/high-water only (last_processed_at uses the
                // wall clock); it is NEVER read by the rebuild logic, so wall-clock here is fine.
                await checkpoints.WriteAsync(
                    new ProjectionCheckpointRecord(runner.Kind, streamId, advancedTo, clock.GetUtcNow()), ct);
            }
        }

        return folded;
    }

    /// <summary>
    /// Cold rebuild (ADR-PC-002 §P4): supersede every current belief for the kind, reset its
    /// checkpoints, then re-fold from sequence 0. Stays within the SELECT/INSERT/UPDATE grant on
    /// <c>projections</c> (supersede, never delete) plus DELETE on the ephemeral checkpoints.
    /// The current beliefs it re-creates are bit-identical to the first run because every stamp is
    /// event-derived (the rebuild-determinism gate).
    /// </summary>
    /// <remarks>
    /// Assumes the <c>ProjectionRelayService</c> is PAUSED for this kind (the §7.2 drill is a
    /// quiescent, non-production operation). A rebuild interleaved with the live relay still
    /// converges to the same byte-identical result — the final event-derived fold wins and the
    /// unique index forbids two current beliefs — but it can transiently expose a partially-folded
    /// belief. v1 has no enforcement (single-process dev); v4 should gate this behind an advisory
    /// lock or a relay-pause for the kind. Tracked as a follow-up, not enforced here.
    /// </remarks>
    public async Task<int> RebuildAsync(IProjectionRunner runner, CancellationToken ct = default)
    {
        await runner.SupersedeAllForRebuildAsync(clock.GetUtcNow(), ct);
        await checkpoints.ResetAsync(runner.Kind, ct);
        return await DrainOnceAsync(runner, ct);
    }
}
