using Babelstone.RateSheets;

namespace Babelstone.RateSheets.Api;

/// <summary>
/// The <c>POST /v1/rate-sheets</c> request body (ADR-PC-008 §P2, surface §2.2). The
/// envelope fields are columns on the stored row; <see cref="Products"/> is the JSONB
/// body (1:1 with the deployed YAML). The deploying principal is NOT in the payload —
/// it arrives as the gateway-authenticated <c>X-Deploy-Actor</c> header (§P4) and is
/// recorded as <c>published_by</c>; <see cref="ApprovedBy"/> / <see cref="ApprovalRef"/>
/// carry the treasury sign-off that the row must record.
/// </summary>
public sealed record RateSheetDeployRequest(
    string RateSheetVersionId,
    string ProductFamily,
    string PackVersion,
    DateTimeOffset EffectiveFrom,
    string ApprovedBy,
    string ApprovalRef,
    Dictionary<string, Dictionary<string, RoleRates>> Products);

/// <summary>The stored sheet as returned on 200/201 — symmetric with the request, plus the publication metadata.</summary>
public sealed record RateSheetResponse(
    string RateSheetVersionId,
    string ProductFamily,
    string PackVersion,
    DateTimeOffset EffectiveFrom,
    string ApprovedBy,
    string ApprovalRef,
    string PublishedBy,
    DateTimeOffset? PublishedAt,
    Dictionary<string, Dictionary<string, RoleRates>> Products)
{
    public static RateSheetResponse From(RateSheet sheet) => new(
        sheet.RateSheetVersionId,
        sheet.ProductFamily,
        sheet.PackVersion,
        sheet.EffectiveFrom,
        sheet.ApprovedBy,
        sheet.ApprovalRef,
        sheet.PublishedBy,
        sheet.PublishedAt,
        sheet.Body.Products);
}

/// <summary>
/// Resolves the pack-declared rate bounds a sheet must honour at deploy (ADR-PC-008 §P2,
/// surface §2.5). INTERIM: the v1 implementation reads a configured ceiling. When the
/// in-engine pack loader/verifier lands (C.5 / archie-bn8c), this must resolve the bound
/// from the verified pack's <c>parameters/constants.yaml</c> (<c>max_consumer_rate_bps</c>)
/// keyed on the sheet's <c>pack_version</c>, not from static configuration.
/// </summary>
public interface IRateBoundsSource
{
    RateBounds For(string packVersion);
}

/// <summary>Interim <see cref="IRateBoundsSource"/>: the same bound for every pack, from configuration.</summary>
public sealed class ConfiguredRateBoundsSource(RateBounds bounds) : IRateBoundsSource
{
    public RateBounds For(string packVersion) => bounds;
}

/// <summary>
/// Supplies the active product configs the cross-artefact rate-sheet invariants validate against
/// (surface §2.5): every product the sheet prices must exist in an active config, and every active
/// config's <c>rate_ref</c> must be covered by the sheet. This is the product-config registry seam.
/// INTERIM: there is no in-engine product-config registry until Epic E/F, so the v1 default
/// (<see cref="EmptyProductConfigSource"/>) reports no active configs and the two cross-artefact
/// checks pass vacuously — a sheet is judged on its self-contained shape alone, exactly as before.
/// When the registry lands, replace the registration with a source that reads the active configs;
/// the validator and the deploy path already consume this seam unchanged.
/// </summary>
public interface IProductConfigSource
{
    IReadOnlyList<ActiveProductConfig> Active();
}

/// <summary>
/// Backwards-compatible default <see cref="IProductConfigSource"/>: no active configs, so the
/// cross-artefact invariants pass vacuously and an existing deploy is never rejected merely because
/// the registry is not yet wired in.
/// </summary>
public sealed class EmptyProductConfigSource : IProductConfigSource
{
    public IReadOnlyList<ActiveProductConfig> Active() => [];
}
