using Babelstone.EventStore;

namespace Babelstone.Families.TermDeposit;

/// <summary>
/// The term-deposit family's read-model store (ADR-IC-005): the generic spine primitive
/// (<see cref="IReadModelStore{TRow}"/> — the UPSERT-with-§P2-guard write, the point lookup, and the
/// truncate-and-refold rebuild) PLUS this family's own range-scan read. The deposit-shaped table and
/// the maturity query are OWNED HERE, in the family layer, not in the engine spine — the spine knows
/// the row only through <see cref="IReadModelRow"/>, so adding a non-deposit family is zero
/// generic-engine diff (ADR-PC-021 §D2/§P2). This is the read-side mirror of how the family layers
/// its typed bitemporal query over the generic <c>IProjectionStorage</c>.
/// </summary>
public interface IDepositReadModelStore : IReadModelStore<DepositReadModelRow>
{
    /// <summary>
    /// The range-scan read (ADR-IC-005 <c>upcoming_maturities</c>): every deposit whose
    /// <see cref="DepositReadModelRow.MaturityDate"/> falls in the half-open <c>[from, to)</c>
    /// window, ordered by maturity date then by id (a deterministic, stable order). Backs the I.2
    /// Query API maturities listing. Family-specific (a non-deposit family has no maturity date), so
    /// it lives on the family store, not the generic spine primitive.
    /// </summary>
    Task<IReadOnlyList<DepositReadModelRow>> ListByMaturityAsync(
        DateOnly fromInclusive, DateOnly toExclusive, CancellationToken ct = default);

    /// <summary>
    /// Every instance currently in the live (<c>currently_active</c>) lifecycle, as stream ids only,
    /// ordered by <c>stream_id</c> (a deterministic, stable order). Backs the surface §3.6 pack-migration
    /// <c>instance_filter { product_family, currently_active }</c> predicate (ADR-PC-009 §P3): the operator
    /// names the target population by rule and the migration write-path re-pins exactly the matched,
    /// still-on-<c>from</c>-version subset. "Live" is the single <see cref="DepositLifecycle.Active"/>
    /// state — every other label is terminal. Family-specific (a non-deposit family has no
    /// <c>lifecycle</c> column), so it lives on the family store, not the generic spine primitive — the
    /// same placement as <see cref="ListByMaturityAsync"/>.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListActiveStreamIdsAsync(CancellationToken ct = default);
}
