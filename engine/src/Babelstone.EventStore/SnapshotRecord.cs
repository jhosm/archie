namespace Babelstone.EventStore;

/// <summary>
/// A persisted snapshot row (feature-design event-store §8) — the byte-oriented
/// storage shape. The typed <c>Snapshot&lt;TState&gt;</c> wrapper that serializes
/// projection state lives in <c>Babelstone.Engine</c> (A.6); this layer stays
/// Npgsql-only and domain-agnostic per the §3 dependency direction.
/// </summary>
/// <param name="StreamId">The stream this snapshot accelerates.</param>
/// <param name="AtSequence">The sequence_number the snapshot covers up to and including.</param>
/// <param name="LastEventId">The event_id at <paramref name="AtSequence"/>; folded into <paramref name="StateHash"/> (§8.3).</param>
/// <param name="StateHash">SHA-256 over (state || last_event_id), hex. Verified on rebuild.</param>
/// <param name="State">Serialized projection state.</param>
/// <param name="Trusted">Advisory-until-trusted (§8.3): false until six months of passing drills.</param>
/// <param name="CreatedAt">When the snapshot was written.</param>
public sealed record SnapshotRecord(
    Guid                 StreamId,
    long                 AtSequence,
    Guid                 LastEventId,
    string               StateHash,
    ReadOnlyMemory<byte> State,
    bool                 Trusted,
    DateTimeOffset       CreatedAt);
