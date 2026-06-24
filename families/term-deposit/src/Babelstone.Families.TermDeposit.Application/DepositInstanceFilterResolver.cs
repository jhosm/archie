using Babelstone.Engine.Hosting;

namespace Babelstone.Families.TermDeposit.Application;

/// <summary>
/// The term-deposit family's <see cref="IPackMigrationInstanceResolver"/> (surface §3.6): turns the
/// family-agnostic <c>{ product_family, currently_active }</c> predicate into the concrete stream ids the
/// pack-migration write-path re-pins, by querying the FAMILY-OWNED read model
/// (<see cref="IDepositReadModelStore.ListActiveStreamIdsAsync"/>). In plain English: this is the family
/// side of the seam — it owns what "currently_active" MEANS for a deposit (the single live
/// <see cref="DepositLifecycle.Active"/>) and which read model answers it, so the spine never names a
/// family (ADR-PC-021 §P2).
/// </summary>
internal sealed class DepositInstanceFilterResolver(
    IDepositReadModelStore store, string productFamily, int migrationCap)
    : IPackMigrationInstanceResolver
{
    public string ProductFamily => productFamily;

    public Task<IReadOnlyList<Guid>> ResolveAsync(InstanceFilter filter, CancellationToken ct = default)
    {
        // The endpoint SELECTS this resolver by product_family and rejects currently_active=false with a
        // 422 before it ever gets here (v1 migrates only the live population). Re-assert both as internal
        // invariants — a violation is a wiring bug, not operator input, so it fails loud rather than
        // silently mis-resolving the migrated population.
        if (!string.Equals(filter.ProductFamily, ProductFamily, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"this resolver serves product_family '{ProductFamily}', not '{filter.ProductFamily}'.",
                nameof(filter));
        }

        if (!filter.CurrentlyActive)
        {
            throw new NotSupportedException("currently_active=false is not supported in v1.");
        }

        // Bound the read at cap+1 (ADR-PC-009 §A2): the write-path's cap guard rejects when the SELECTED
        // population exceeds the cap, so resolving exactly cap+1 is enough to trip it — and it stops
        // Postgres streaming an unbounded live population back just to throw it away. The +1 is the
        // overflow sentinel: cap rows fit, cap+1 means "over the cap, reject". (migrationCap is at most
        // int.MaxValue-1 in any realistic config, so cap+1 cannot overflow.)
        return store.ListActiveStreamIdsAsync(migrationCap + 1, ct);
    }
}
