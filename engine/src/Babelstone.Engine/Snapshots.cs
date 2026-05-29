using Babelstone.EventStore;

namespace Babelstone.Engine;

/// <summary>A typed snapshot of projection state (feature-design event-store §8).</summary>
public sealed record Snapshot<TState>(
    long AtSequence, Guid LastEventId, string StateHash, TState State, bool Trusted, DateTimeOffset CreatedAt);

/// <summary>Serializes projection state to/from bytes for snapshot persistence. Tests supply one (e.g. JSON).</summary>
public interface IStateSerializer<TState>
{
    byte[] Serialize(TState state);
    TState Deserialize(ReadOnlyMemory<byte> bytes);
}

/// <summary>
/// The typed layer over A.4's byte-oriented <see cref="ISnapshotStorage"/>: serializes
/// <typeparamref name="TState"/>, computes the §8.3 hash (state ‖ last_event_id), and
/// verifies it on read so a tampered or mis-sequenced snapshot is rejected, not trusted.
/// </summary>
public sealed class SnapshotStore<TState>(ISnapshotStorage storage, IStateSerializer<TState> serializer)
{
    public async Task<Snapshot<TState>?> TryGetAsync(Guid streamId, CancellationToken ct = default)
    {
        var record = await storage.TryGetLatestAsync(streamId, ct);
        if (record is null)
        {
            return null;
        }

        // Verify the stored hash before trusting the snapshot (§8.3): the worst
        // event-sourcing failure mode is a silently-wrong snapshot read as truth.
        var expected = SnapshotHash.Compute(record.State.Span, record.LastEventId);
        if (!string.Equals(expected, record.StateHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Snapshot for stream {streamId} at sequence {record.AtSequence} failed hash verification.");
        }

        return new Snapshot<TState>(
            record.AtSequence,
            record.LastEventId,
            record.StateHash,
            serializer.Deserialize(record.State),
            record.Trusted,
            record.CreatedAt);
    }

    public Task PutAsync(Guid streamId, long atSequence, Guid lastEventId, TState state, DateTimeOffset createdAt, CancellationToken ct = default)
    {
        var bytes = serializer.Serialize(state);
        var hash = SnapshotHash.Compute(bytes, lastEventId);
        // Trusted defaults false — advisory until six months of passing drills (§8.3).
        var record = new SnapshotRecord(streamId, atSequence, lastEventId, hash, bytes, Trusted: false, createdAt);
        return storage.PutAsync(record, ct);
    }
}

/// <summary>Inputs a snapshot policy weighs (feature-design event-store §8.1).</summary>
/// <param name="EventsSinceSnapshot">Count of events folded since the last snapshot (drives the per-N trigger).</param>
/// <param name="IsLifecycleBoundary">The just-folded event is a lifecycle boundary (constitution, maturity, …).</param>
/// <param name="IsCalendarBoundary">A reporting-period boundary (month/year end) was crossed.</param>
public sealed record SnapshotContext(long EventsSinceSnapshot, bool IsLifecycleBoundary, bool IsCalendarBoundary);

/// <summary>The §8.1 trigger decision. Triggers compose: a snapshot is taken if any fires.</summary>
public interface ISnapshotPolicy
{
    bool ShouldSnapshot(SnapshotContext ctx);
}

/// <summary>
/// Default policy: the per-N trigger (§8.1), configurable threshold. Lifecycle and
/// calendar boundaries also fire when the caller flags them — those flags are supplied
/// by the family (which knows its lifecycle events), so the engine stays family-agnostic.
/// </summary>
public sealed class CountBasedSnapshotPolicy(long threshold = 100) : ISnapshotPolicy
{
    public bool ShouldSnapshot(SnapshotContext ctx)
        => ctx.EventsSinceSnapshot >= threshold || ctx.IsLifecycleBoundary || ctx.IsCalendarBoundary;
}
