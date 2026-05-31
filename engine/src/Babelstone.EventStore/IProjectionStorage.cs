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
/// <see cref="ProjectionRecord.PiiCiphertext"/> are the caller's concern.
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
    /// superseding any prior belief first when this write is a forced correction.
    /// </summary>
    Task WriteAsync(ProjectionRecord record, CancellationToken ct = default);

    /// <summary>
    /// Supersedes the currently-believed row(s) for <paramref name="streamId"/> by
    /// stamping <c>superseded_at</c> (ADR-PC-002 §P2). Already-superseded rows are left
    /// untouched, so the full belief history remains queryable.
    /// </summary>
    Task SupersedeAsync(Guid streamId, DateTimeOffset supersededAt, CancellationToken ct = default);

    /// <summary>
    /// Returns the currently-believed projection row for <paramref name="streamId"/>
    /// (the row with <c>superseded_at IS NULL</c>), or <see langword="null"/> if none
    /// exists.
    /// </summary>
    Task<ProjectionRecord?> ReadCurrentBeliefAsync(Guid streamId, CancellationToken ct = default);
}
