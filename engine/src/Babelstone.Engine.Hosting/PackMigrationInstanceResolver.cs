namespace Babelstone.Engine.Hosting;

/// <summary>
/// The surface §3.6 pack-migration instance-selection predicate, as a family-AGNOSTIC value object. In
/// plain English: instead of an operator hand-listing every instance id to re-pin, they describe the
/// target population by rule — "every term_deposit that is still live" — and the engine looks up which
/// instances match. The spine carries this opaquely: <see cref="ProductFamily"/> selects which family's
/// resolver answers it, and <see cref="CurrentlyActive"/> is a bool the resolving family maps to its OWN
/// notion of "live" (a term_deposit: <c>DepositLifecycle.Active</c>). No family vocabulary, no column
/// name, no PII (ADR-PC-004 §P2) ever reaches the spine.
/// </summary>
/// <param name="ProductFamily">The product family whose population the predicate selects (e.g. <c>term_deposit</c>) — the resolver-selection key, not a read-model column.</param>
/// <param name="CurrentlyActive">Select only instances currently in the family's live lifecycle. v1 supports only <c>true</c> (re-pinning a terminal instance accrues no further events) — the endpoint rejects <c>false</c> with 422.</param>
public sealed record InstanceFilter(string ProductFamily, bool CurrentlyActive);

/// <summary>
/// Resolves an <see cref="InstanceFilter"/> to the concrete instance (stream) ids it matches, over
/// whatever cross-stream read the IMPLEMENTING family owns. In plain English: this is the family side of
/// the predicate seam — the spine hands over a family-agnostic predicate and gets back a flat id list it
/// feeds, UNCHANGED, into the existing <see cref="PackMigrationService{TState}"/> preview/migrate loop
/// (so preview, idempotency, and audit are preserved for free).
/// </summary>
/// <remarks>
/// The spine names no family (ADR-PC-021 §P2): a family supplies the implementation over its read model
/// and decides what each predicate dimension MEANS. Registered per family and selected by
/// <see cref="ProductFamily"/> — a host with several families has several resolvers, and the single
/// <c>POST /v1/pack-migrations</c> route dispatches to the right one (no per-family route collision).
/// </remarks>
public interface IPackMigrationInstanceResolver
{
    /// <summary>
    /// The product family this resolver answers for (e.g. <c>term_deposit</c>). The endpoint selects the
    /// resolver by matching this against the request's <c>product_family</c>; a miss is a 422.
    /// </summary>
    string ProductFamily { get; }

    /// <summary>
    /// The CANDIDATE instance ids the predicate selects — the full live population, NOT pre-filtered by
    /// pack version (the read model carries no pack pin; the pin lives on the event envelope, ADR-PC-009
    /// §P1). The migration write-path narrows this to the subset still on <c>from_pack_version</c> via the
    /// per-head pin check, so the predicate WIDENS and the head-pin guard NARROWS.
    /// </summary>
    Task<IReadOnlyList<Guid>> ResolveAsync(InstanceFilter filter, CancellationToken ct = default);
}
