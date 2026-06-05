namespace Babelstone.EventStore;

/// <summary>
/// Byte-oriented storage boundary for Path-A bitemporal projection rows
/// (ADR-PC-002 §P1/§P2).
/// </summary>
/// <remarks>
/// <para>
/// This is the low-level store: it persists, supersedes, and reads back
/// <see cref="ProjectionRecord"/> rows as opaque payloads. The serialization of
/// <see cref="ProjectionRecord.StructuralPayload"/> and the meaning of
/// <see cref="ProjectionRecord.PiiCiphertext"/> are the caller's concern. Every operation
/// scopes to the <c>(streamId, projectionKind)</c> pair — one stream carries more than one
/// projection (F.6), so supersession and current-belief reads must not bleed across kinds.
/// </para>
/// <para>
/// The TYPED bitemporal query helper (AsOf / CurrentBelief / HistoryOf, ADR-PC-002 §P3)
/// is a separate task (D.3) and sits ABOVE this byte-oriented boundary in
/// <c>Babelstone.Engine</c> — it deserializes <see cref="ProjectionRecord.StructuralPayload"/>
/// into <c>TState</c>, the same split as <c>SnapshotStore&lt;TState&gt;</c> over
/// <see cref="ISnapshotStorage"/>. That typed layer composes the two-axis temporal reads this
/// boundary exposes (<see cref="ReadAsOfAsync"/> and <see cref="ReadHistoryOfAsync"/>); the SQL
/// for the four-column join stays HERE, private to the store, because this is the only code that
/// touches the <c>projections</c> table.
/// </para>
/// </remarks>
public interface IProjectionStorage
{
    /// <summary>
    /// Inserts a new projection row (ADR-PC-002 §P1). The caller is responsible for
    /// superseding any prior belief first when this write is a forced correction — or
    /// uses <see cref="SupersedeAndWriteAsync"/> to do both atomically.
    /// </summary>
    Task WriteAsync(ProjectionRecord record, CancellationToken ct = default);

    /// <summary>
    /// Supersedes the currently-believed row(s) for the <c>(streamId, projectionKind)</c>
    /// pair by stamping <c>superseded_at</c> (ADR-PC-002 §P2). Already-superseded rows are
    /// left untouched, so the full belief history remains queryable. Used for the rebuild
    /// supersede-all step; the steady-state update path is <see cref="SupersedeAndWriteAsync"/>.
    /// </summary>
    Task SupersedeAsync(Guid streamId, string projectionKind, DateTimeOffset supersededAt, CancellationToken ct = default);

    /// <summary>
    /// The steady-state bitemporal update (ADR-PC-002 §P2): supersedes the currently-believed
    /// row for the record's <c>(StreamId, ProjectionKind)</c> at <paramref name="record"/>'s
    /// <see cref="ProjectionRecord.RecordedAt"/>, then inserts <paramref name="record"/> as the
    /// new current belief — BOTH in ONE local transaction on ONE connection, so a crash can
    /// never leave the pair half-applied (zero or two current-belief rows). This is where the
    /// supersede-then-insert atomicity lives; the byte store owns the transaction because it
    /// owns the connection (the engine stays SQL-free). The PARTIAL UNIQUE index
    /// <c>projections_current_belief_uq</c> (migration 0010) is the backstop: a missed
    /// supersede fails loud rather than silently leaving two current beliefs.
    /// </summary>
    Task SupersedeAndWriteAsync(ProjectionRecord record, CancellationToken ct = default);

    /// <summary>
    /// Supersedes EVERY currently-believed row for <paramref name="projectionKind"/> across all
    /// streams — the rebuild supersede-all step (ADR-PC-002 §P4): the cold rebuild closes the old
    /// beliefs (preserving them as history, never deleting) before re-folding each stream from
    /// <c>sequence_number</c> 0 (draining is per stream; the events table carries no cluster-wide
    /// order). Stays inside the SELECT/INSERT/UPDATE grant; no DELETE/TRUNCATE.
    /// </summary>
    Task SupersedeAllAsync(string projectionKind, DateTimeOffset supersededAt, CancellationToken ct = default);

    /// <summary>
    /// Returns the currently-believed projection row for the <c>(streamId, projectionKind)</c>
    /// pair (the row with <c>superseded_at IS NULL</c>), or <see langword="null"/> if none
    /// exists.
    /// </summary>
    Task<ProjectionRecord?> ReadCurrentBeliefAsync(Guid streamId, string projectionKind, CancellationToken ct = default);

    /// <summary>
    /// The two-axis bitemporal read (ADR-PC-002 §P1) backing the typed <c>AsOf</c> helper (§P3):
    /// returns the row for the <c>(streamId, projectionKind)</c> pair whose world-time slice
    /// covers <paramref name="validTime"/> AND whose belief-time slice covers
    /// <paramref name="knownAt"/>, or <see langword="null"/> if no row was believed for that
    /// valid-time at that transaction-time. World-time is covered when
    /// <c>valid_from &lt;= validTime AND (valid_to IS NULL OR valid_to &gt; validTime)</c>;
    /// belief-time is covered when <c>recorded_at &lt;= knownAt AND (superseded_at IS NULL OR
    /// superseded_at &gt; knownAt)</c> — the half-open <c>[recorded_at, superseded_at)</c>
    /// interval, so the row a correction superseded is invisible once <paramref name="knownAt"/>
    /// reaches the correction. This is what makes "as we knew it then" differ from
    /// "as we know it now" after a retroactive correction (§P2).
    /// </summary>
    Task<ProjectionRecord?> ReadAsOfAsync(
        Guid streamId, string projectionKind, DateTimeOffset validTime, DateTimeOffset knownAt,
        CancellationToken ct = default);

    /// <summary>
    /// The full belief history for the <c>(streamId, projectionKind)</c> pair (ADR-PC-002 §P2)
    /// backing the typed <c>HistoryOf</c> helper (§P3) — the audit trail of how belief about
    /// this projection changed: every row, superseded and current alike, ordered by belief-time
    /// (<c>recorded_at</c> ascending, <c>row_id</c> ascending only as a deterministic tie-break).
    /// A forced correction supersedes rather than deletes, so the disavowed beliefs remain here;
    /// the currently-believed row (if any) is the one with <c>superseded_at IS NULL</c> and sorts
    /// last. Returns an empty list if the pair was never projected.
    /// </summary>
    Task<IReadOnlyList<ProjectionRecord>> ReadHistoryOfAsync(
        Guid streamId, string projectionKind, CancellationToken ct = default);
}
