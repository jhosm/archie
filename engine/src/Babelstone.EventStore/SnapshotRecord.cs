namespace Babelstone.EventStore;

/// <summary>
/// A persisted snapshot row (feature-design event-store §8) — the byte-oriented
/// storage shape. The typed <c>Snapshot&lt;TState&gt;</c> wrapper that serializes
/// projection state lives in <c>Babelstone.Engine</c>; this layer stays
/// Npgsql-only and domain-agnostic per the §3 dependency direction.
/// </summary>
/// <param name="StreamId">The stream this snapshot accelerates.</param>
/// <param name="AtSequence">The sequence_number the snapshot covers up to and including.</param>
/// <param name="LastEventId">The event_id at <paramref name="AtSequence"/>; folded into <paramref name="StateHash"/> (§8.3).</param>
/// <param name="StateHash">SHA-256 over (state || last_event_id), hex. Verified on rebuild.</param>
/// <param name="State">Serialized projection state.</param>
/// <param name="Trusted">Advisory-until-trusted (feature-design event-store §8.3): false until the
/// discard-rebuild drill has earned trust; the promotion policy lives in <c>Babelstone.Engine</c>.</param>
/// <param name="CreatedAt">When the snapshot row was physically written (a wall-clock DB stamp).</param>
/// <param name="TransactionTime">
/// The event-derived transaction_time of the head event the snapshot covers (the append-stamped
/// instant, ADR-PC-010 §P5) — distinct from <paramref name="CreatedAt"/>, which is when the ROW was
/// written. Lets rehydrate seed last_updated from the snapshot when a stream is fully covered with no
/// tail (ADR-PC-003 §P3). Nullable: pre-0017 snapshot rows carry null, for which rehydrate falls back
/// to the prior null-on-no-tail behaviour (the cold fold stays correct).
/// </param>
public sealed record SnapshotRecord(
    Guid                 StreamId,
    long                 AtSequence,
    Guid                 LastEventId,
    string               StateHash,
    ReadOnlyMemory<byte> State,
    bool                 Trusted,
    DateTimeOffset       CreatedAt,
    DateTimeOffset?      TransactionTime = null);
