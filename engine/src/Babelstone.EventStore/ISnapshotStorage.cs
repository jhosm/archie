namespace Babelstone.EventStore;

/// <summary>
/// Byte-oriented persistence for snapshots (feature-design event-store §8). Like
/// <see cref="IEventStore"/> this is the storage boundary — the only code that
/// touches the <c>snapshots</c> table. The typed, state-aware layer
/// (<c>Snapshot&lt;TState&gt;</c>, the take-snapshot policy, the discard-rebuild
/// drill) sits above this in <c>Babelstone.Engine</c> (A.6).
/// </summary>
public interface ISnapshotStorage
{
    /// <summary>Returns the highest-sequence snapshot for the stream, or null if none exists.</summary>
    Task<SnapshotRecord?> TryGetLatestAsync(Guid streamId, CancellationToken ct = default);

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
