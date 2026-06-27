using Babelstone.EventStore;

namespace Babelstone.Engine;

/// <summary>The current believed projection state plus the source event sequence that produced it.</summary>
public sealed record CurrentProjection<TState>(TState State, long SourceSequence);

/// <summary>
/// The typed layer over D.1's byte-oriented <see cref="IProjectionStorage"/>: serializes
/// <typeparamref name="TState"/> to/from the structural payload (mirroring
/// <see cref="SnapshotStore{TState}"/>). The bitemporal mechanics (supersede-then-insert,
/// the four temporal columns, the source-sequence idempotency stamp) live in the byte store;
/// this layer just maps state ⇄ bytes and assembles the <see cref="ProjectionRecord"/>.
/// No query helper (AsOf/CurrentBelief/HistoryOf) — that is D.3.
/// </summary>
public sealed class ProjectionStore<TState>(IProjectionStorage storage, IStateSerializer<TState> serializer)
    where TState : class
{
    /// <summary>The current belief for the pair, deserialized, or <see langword="null"/> if none exists.</summary>
    public async Task<CurrentProjection<TState>?> TryReadCurrentAsync(
        Guid streamId, string kind, CancellationToken ct = default)
    {
        var record = await storage.ReadCurrentBeliefAsync(streamId, kind, ct);
        return record is null
            ? null
            : new CurrentProjection<TState>(serializer.Deserialize(record.StructuralPayload), record.SourceSequence);
    }

    /// <summary>
    /// The steady-state bitemporal update (ADR-PC-002): atomically supersede the prior belief
    /// and insert <paramref name="state"/> as the new current belief, stamped with the deterministic
    /// temporal context and the producing event's <paramref name="sourceSequence"/>.
    /// </summary>
    public Task UpdateAsync(
        Guid streamId, string kind, TState state, ProjectionTemporalContext temporal, long sourceSequence,
        CancellationToken ct = default)
    {
        var record = new ProjectionRecord(
            StreamId: streamId,
            ProjectionKind: kind,
            SourceSequence: sourceSequence,
            ValidFrom: temporal.ValidFrom,
            ValidTo: temporal.ValidTo,
            RecordedAt: temporal.RecordedAt,
            SupersededAt: null,
            StructuralPayload: serializer.Serialize(state),
            // Structural state only; the PII ciphertext envelope stays empty until PII lands
            // (ADR-PC-004).
            PiiCiphertext: ReadOnlyMemory<byte>.Empty);
        return storage.SupersedeAndWriteAsync(record, ct);
    }

    /// <summary>Rebuild supersede-all for this kind (ADR-PC-002) before a cold re-fold.</summary>
    public Task SupersedeAllAsync(string kind, DateTimeOffset supersededAt, CancellationToken ct = default)
        => storage.SupersedeAllAsync(kind, supersededAt, ct);
}
