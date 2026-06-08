namespace Babelstone.EventStore;

/// <summary>
/// The storage boundary for the denormalized CQRS deposit read model (ADR-IC-005). The ONLY code
/// that touches the <c>read_model.deposits</c> table — the same storage-boundary discipline as
/// <see cref="IProjectionStorage"/> and <see cref="IEventStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the byte-oriented, family-agnostic store: it persists and reads <see cref="ReadModelRow"/>
/// rows as typed query columns plus an opaque <see cref="ReadModelRow.Detail"/> body (ADR-PC-021 §P2 —
/// the spine never names a deposit's body shape). The serialization of the detail payload is the
/// caller's concern, the same split as <see cref="ProjectionRecord.StructuralPayload"/> over
/// <see cref="IProjectionStorage"/>.
/// </para>
/// <para>
/// The write path is the ADR-IC-005 §P2 canonical projection write: an UPSERT keyed by
/// <see cref="ReadModelRow.StreamId"/> with a <see cref="ReadModelRow.LastSequence"/> monotonicity
/// guard, so a re-delivered or out-of-order event (the at-least-once drainer after a crash) never
/// overwrites a fresher row. The read path serves the two access patterns ADR-IC-005 names for the
/// deposit projections: a point lookup by id and a range scan by maturity date.
/// </para>
/// </remarks>
public interface IReadModelStore
{
    /// <summary>
    /// The ADR-IC-005 §P2 canonical write: UPSERT the row by <see cref="ReadModelRow.StreamId"/>,
    /// overwriting an existing row ONLY when <paramref name="row"/>'s
    /// <see cref="ReadModelRow.LastSequence"/> is strictly greater than the stored row's. A
    /// duplicate or out-of-order event (sequence at or below the stored value) is a no-op — this is
    /// what makes the at-least-once projection drainer safe to replay.
    /// </summary>
    Task UpsertAsync(ReadModelRow row, CancellationToken ct = default);

    /// <summary>
    /// The point-lookup read (ADR-IC-005 <c>deposit_detail</c>): the denormalized row for the
    /// deposit, or <see langword="null"/> if it has not been projected yet.
    /// </summary>
    Task<ReadModelRow?> GetAsync(Guid streamId, CancellationToken ct = default);

    /// <summary>
    /// The range-scan read (ADR-IC-005 <c>upcoming_maturities</c>): every deposit whose
    /// <see cref="ReadModelRow.MaturityDate"/> falls in the half-open <c>[from, to)</c> window,
    /// ordered by maturity date then by id (a deterministic, stable order). Backs the I.2 Query API
    /// maturities listing.
    /// </summary>
    Task<IReadOnlyList<ReadModelRow>> ListByMaturityAsync(
        DateOnly fromInclusive, DateOnly toExclusive, CancellationToken ct = default);

    /// <summary>
    /// Truncates the read model for a clean rebuild (ADR-IC-005 §P5): the rebuild path TRUNCATEs
    /// then re-folds from the event log. A first-class operation, not a break-glass procedure.
    /// </summary>
    Task TruncateAsync(CancellationToken ct = default);
}
