namespace Babelstone.EventStore;

/// <summary>
/// Byte-oriented persistence for snapshots (feature-design event-store §8). Like
/// <see cref="IEventStore"/> this is the storage boundary — the only code that
/// touches the <c>snapshots</c> table. The typed, state-aware layer
/// (<c>Snapshot&lt;TState&gt;</c>, the take-snapshot policy, the discard-rebuild
/// drill) sits above this in <c>Babelstone.Engine</c>.
/// </summary>
public interface ISnapshotStorage
{
    /// <summary>Returns the highest-sequence snapshot for the stream, or null if none exists.</summary>
    Task<SnapshotRecord?> TryGetLatestAsync(Guid streamId, CancellationToken ct = default);

    /// <summary>
    /// Returns the highest-sequence snapshot whose <c>at_sequence</c> is at or below
    /// <paramref name="atOrBeforeSequence"/>, or null if no snapshot covers that point — the
    /// <c>readLatestSnapshot(stream_id, …, atOrBeforeSequence)</c> read of [ADR-PC-003] that
    /// the as-of / point-in-time replay needs. A snapshot taken PAST the requested point is in the
    /// future relative to the read and must be skipped, so the as-of fold seeds only from a snapshot
    /// at-or-before the point and folds the tail up to it (cold from zero when none qualifies — the
    /// §P3 correctness fallback). <see cref="TryGetLatestAsync"/> is the special case
    /// <c>atOrBeforeSequence = head</c>; the live-head load keeps using it.
    /// </summary>
    Task<SnapshotRecord?> TryGetAtOrBeforeAsync(
        Guid streamId, long atOrBeforeSequence, CancellationToken ct = default);

    /// <summary>
    /// Writes (or re-writes) the snapshot at its sequence. Eventually-consistent with
    /// the log, never transactional with the append (§8.1). Re-putting the same
    /// (stream, sequence) overwrites — e.g. to promote it to <c>trusted</c>.
    /// </summary>
    Task PutAsync(SnapshotRecord snapshot, CancellationToken ct = default);

    /// <summary>
    /// Discards all snapshots for a stream and returns how many were removed — the
    /// primitive the monthly discard-rebuild drill (§8.3) is built on. Safe by
    /// design: snapshots are a rebuildable cache, never the source of truth.
    /// </summary>
    Task<int> DiscardAsync(Guid streamId, CancellationToken ct = default);
}
