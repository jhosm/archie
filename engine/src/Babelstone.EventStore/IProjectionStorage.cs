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
/// The typed bitemporal query helper (AsOf / CurrentBelief / HistoryOf, ADR-PC-002 §P3)
/// is a separate task (D.3) and sits ABOVE this byte-oriented boundary. Do NOT add those
/// query helpers here — this interface intentionally exposes only the current-belief
/// read needed by the write path.
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
}
