namespace Babelstone.EventStore;

/// <summary>
/// Byte-oriented persistence for bitemporal projections (ADR-PC-002 §P1/§P2).
/// Like <see cref="ISnapshotStorage"/> this is the storage boundary — the only
/// code that touches the <c>deposit_position_projection</c> table. The typed,
/// domain-aware query layer (D.3 / ADR-PC-002 §P3) sits above this in
/// <c>Babelstone.Engine</c>.
/// <para>
/// PII columns are BYTEA ciphertext (ADR-PC-004 §P2); the engine resolves them
/// via OpenBao. This interface sees opaque bytes only.
/// </para>
/// </summary>
public interface IProjectionStorage
{
    /// <summary>
    /// Writes a new projection row (INSERT). Each call appends a new row; the
    /// caller is responsible for superseding stale current-belief rows first when
    /// performing a correction (ADR-PC-002 §P2).
    /// </summary>
    Task WriteAsync(ProjectionRecord record, CancellationToken ct = default);

    /// <summary>
    /// Closes all currently-believed rows for <paramref name="streamId"/> by setting
    /// <c>superseded_at</c> to <paramref name="supersededAt"/>
    /// (<c>WHERE superseded_at IS NULL</c>). The caller follows this with
    /// <see cref="WriteAsync"/> for the corrected row. Together the two calls
    /// implement the bitemporal correction primitive (ADR-PC-002 §P2 / §6.3
    /// criterion #1) without losing history.
    /// </summary>
    Task SupersedeAsync(Guid streamId, DateTimeOffset supersededAt, CancellationToken ct = default);

    /// <summary>
    /// Returns the currently-believed projection for <paramref name="streamId"/>
    /// (<c>WHERE superseded_at IS NULL</c>), or <c>null</c> if none exists.
    /// This is the hot read path; backed by the partial index on the projection
    /// table. The typed query layer (ADR-PC-002 §P3) builds domain-specific
    /// point-in-time reads on top of this primitive.
    /// </summary>
    Task<ProjectionRecord?> ReadCurrentBeliefAsync(Guid streamId, CancellationToken ct = default);
}
