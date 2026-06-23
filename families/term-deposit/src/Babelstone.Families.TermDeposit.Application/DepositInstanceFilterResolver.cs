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
internal sealed class DepositInstanceFilterResolver(IDepositReadModelStore store, string productFamily)
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

        return store.ListActiveStreamIdsAsync(ct);
    }
}
