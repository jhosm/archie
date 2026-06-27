namespace Babelstone.EventStore;

/// <summary>
/// The generic, family-agnostic storage boundary for a denormalized CQRS read model (ADR-IC-005):
/// the point-lookup / UPSERT / truncate primitives every read model needs, parameterised over a
/// family-supplied <typeparamref name="TRow"/>. The spine knows a row only through
/// <see cref="IReadModelRow"/> (stream id + the §P2 sequence guard + the opaque body); the family
/// owns the concrete row's typed query columns and any family-specific reads (e.g. a range scan),
/// declared on its OWN store interface over this primitive — the same split that keeps
/// <see cref="IProjectionStorage"/> generic while a family layers typed reads on top
/// (ADR-PC-021). A new family materialises its read model by supplying its row type + a
/// store; <c>Babelstone.EventStore</c>/<c>Babelstone.Engine</c> take zero diff.
/// </summary>
/// <remarks>
/// <para>
/// The write path is the ADR-IC-005 canonical projection write: an UPSERT keyed by
/// <see cref="IReadModelRow.StreamId"/> with a <see cref="IReadModelRow.LastSequence"/> monotonicity
/// guard, so a re-delivered or out-of-order event (the at-least-once drainer after a crash) never
/// overwrites a fresher row. <see cref="GetAsync"/> is the §"six projections" point lookup;
/// <see cref="TruncateAsync"/> is the §P5 truncate-and-refold rebuild.
/// </para>
/// </remarks>
public interface IReadModelStore<TRow>
    where TRow : IReadModelRow
{
    /// <summary>
    /// The ADR-IC-005 canonical write: UPSERT the row by <see cref="IReadModelRow.StreamId"/>,
    /// overwriting an existing row ONLY when <paramref name="row"/>'s
    /// <see cref="IReadModelRow.LastSequence"/> is strictly greater than the stored row's. A
    /// duplicate or out-of-order event (sequence at or below the stored value) is a no-op — this is
    /// what makes the at-least-once projection drainer safe to replay.
    /// </summary>
    Task UpsertAsync(TRow row, CancellationToken ct = default);

    /// <summary>
    /// The point-lookup read (ADR-IC-005 <c>deposit_detail</c>): the denormalized row for the
    /// stream, or <see langword="null"/> if it has not been projected yet.
    /// </summary>
    Task<TRow?> GetAsync(Guid streamId, CancellationToken ct = default);

    /// <summary>
    /// Truncates the read model for a clean rebuild (ADR-IC-005): the rebuild path TRUNCATEs
    /// then re-folds from the event log. A first-class operation, not a break-glass procedure.
    /// </summary>
    Task TruncateAsync(CancellationToken ct = default);
}
