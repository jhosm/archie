namespace Babelstone.RateSheets;

/// <summary>
/// Persistence for the product-config deploy registry (ADR-PC-009 §A2, ADR-PC-008): append-only
/// writes, idempotent fetch by version id, and the point-in-time resolution a constitution would stamp.
/// Hand-rolled against the <c>product_config_versions</c> table contract — no ORM (ADR-PC-010). This is
/// the exact seam <see cref="IRateSheetStore"/> is for rate sheets, applied to the product-config
/// artefact family (the separate-artefact-families principle, ADR-PC-008 surface §1).
/// </summary>
public interface IProductConfigVersionStore
{
    /// <summary>
    /// Appends a new immutable config version. Throws <see cref="DuplicateProductConfigVersionException"/>
    /// if the version id already exists or its <c>(product_id, effective_from)</c> is already taken —
    /// the deploy endpoint turns that into the ADR-PC-008 idempotency outcome.
    /// </summary>
    Task InsertAsync(ProductConfigVersion version, CancellationToken ct = default);

    /// <summary>Fetches a config version by its version id, or null if none exists.</summary>
    Task<ProductConfigVersion?> TryGetAsync(string productConfigVersionId, CancellationToken ct = default);

    /// <summary>
    /// Resolves the config version active at <paramref name="asOf"/> for a product (ADR-PC-008 §P3
    /// applied to product-configs): the highest <c>effective_from</c> not after the instant. Null if no
    /// version is yet effective. The <c>(product_id, effective_from)</c> uniqueness constraint makes
    /// this unambiguous. This is the resolve a later work item will call to mint the registry-issued
    /// <c>product_config_version</c> pin in place of the interim content hash (ADR-PC-009 §A2).
    /// </summary>
    Task<ProductConfigVersionResolution?> ResolveAsync(string productId, DateTimeOffset asOf, CancellationToken ct = default);
}
