using Babelstone.Engine;
using Babelstone.Engine.Hosting;
using Babelstone.EventStore;
using Babelstone.Families.PersonalLoan;

namespace Babelstone.Families.PersonalLoan.Application;

/// <summary>
/// The personal_loan family's <see cref="IPackMigrationInstanceResolver"/> (surface §3.6): turns the
/// family-agnostic <c>{ product_family, currently_active }</c> predicate into the concrete stream ids the
/// pack-migration write-path re-pins. In plain English: this is the family side of the seam — it owns what
/// "currently_active" MEANS for a loan (a loan still <see cref="LoanLifecycle.Active"/>) and how to find
/// them, so the spine never names a family (ADR-PC-021 §P2).
/// </summary>
/// <remarks>
/// It must NOT mirror term-deposit's <c>DepositInstanceFilterResolver</c> verbatim: personal_loan has NO
/// family read model listing active loans (no <c>read_model.deposits</c> analogue with a queryable
/// <c>lifecycle</c> column), so there is nothing to <c>SELECT … WHERE lifecycle = 'Active'</c> against.
/// Instead it folds the EVENT STORE: it enumerates the family's streams
/// (<see cref="IEventStore.ReadStreamIdsAsync"/>) and keeps the ones whose folded
/// <see cref="LoanPosition"/> is still <see cref="LoanLifecycle.Active"/> — every terminal state
/// (<see cref="LoanLifecycle.Failed"/>, <see cref="LoanLifecycle.Settled"/>,
/// <see cref="LoanLifecycle.WrittenOff"/>, <see cref="LoanLifecycle.Erased"/>, per
/// <c>LifecycleTransitions</c>) is excluded. This is v1 scale only (the same per-stream enumeration the
/// projection drainer uses); a denormalized active-loan read surface would replace the fold if the
/// population grows. The CANDIDATE set WIDENS to the live population — it is NOT pre-filtered by pack
/// version (the pin lives on the event envelope, ADR-PC-009 §P1); the migration write-path's per-head pin
/// check NARROWS it to the subset still on <c>from_pack_version</c>.
/// </remarks>
internal sealed class LoanInstanceFilterResolver(
    IEventStore eventStore, AggregateRuntime<LoanPosition> runtime, string productFamily)
    : IPackMigrationInstanceResolver
{
    public string ProductFamily => productFamily;

    public async Task<IReadOnlyList<Guid>> ResolveAsync(InstanceFilter filter, CancellationToken ct = default)
    {
        // The endpoint SELECTS this resolver by product_family and rejects currently_active=false with a
        // 422 before it ever gets here (v1 migrates only the live population). Re-assert both as internal
        // invariants — a violation is a wiring bug, not operator input, so it fails loud rather than
        // silently mis-resolving the migrated population (mirrors DepositInstanceFilterResolver's contract).
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

        // Fold the event store: enumerate every personal_loan stream, rehydrate each loan, and keep the
        // still-Active ones. ReadStreamIdsAsync gives no order guarantee, so sort the kept ids for a STABLE
        // result (determinism is the contract — a second resolve yields the identical sequence).
        var streamIds = await eventStore.ReadStreamIdsAsync(productFamily, ct);
        var active = new List<Guid>();
        foreach (var streamId in streamIds)
        {
            var hydrated = await runtime.LoadAsync(streamId, ct);
            if (hydrated.State.Lifecycle == LoanLifecycle.Active)
            {
                active.Add(streamId);
            }
        }

        active.Sort();
        return active;
    }
}
