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
