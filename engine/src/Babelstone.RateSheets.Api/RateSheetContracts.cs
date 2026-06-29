using Babelstone.Packs;
using Babelstone.RateSheets;

namespace Babelstone.RateSheets.Api;

/// <summary>
/// The <c>POST /v1/rate-sheets</c> request body (ADR-PC-008, surface §2.2). The
/// envelope fields are columns on the stored row; <see cref="Products"/> is the JSONB
/// body (1:1 with the deployed YAML). The deploying principal is NOT in the payload —
/// it arrives as the gateway-authenticated <c>X-Deploy-Actor</c> header (ADR-PC-008) and is
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
/// Resolves the pack-declared rate bounds a sheet must honour at deploy (ADR-PC-008,
/// surface §2.5) from the VERIFIED pack keyed on the sheet's <c>pack_version</c> — the bound
/// is the signed pack's <c>parameters/constants.yaml</c> <c>max_consumer_rate_bps</c>, never a
/// host-side configuration knob. <see cref="For"/> throws <see cref="PackLoadException"/> for a
/// <c>pack_version</c> the engine has not loaded (an unknown/unpinned pack); the deploy handler
/// turns that into a 400 rejection rather than letting it escape as a 500.
/// </summary>
public interface IRateBoundsSource
{
    RateBounds For(string packVersion);
}

/// <summary>
/// Resolves <see cref="RateBounds"/> from the verified pack (C.5, <see cref="IPackStore"/>):
/// <c>[0, max_consumer_rate_bps]</c> read from the signed pack's <c>parameters/constants.yaml</c>
/// keyed on <c>pack_version</c>. <see cref="IPackStore.Resolve"/> is the pure hot-path cache read
/// (the pack was pulled-by-digest and cosign-verified once at load time, ADR-PC-007); an
/// unknown <c>pack_version</c> surfaces as a <see cref="PackLoadException"/> the caller maps to a
/// clean deploy rejection. The floor is 0: a pack declares only the consumer-rate ceiling, and the
/// floor is what makes a negative TAN a deploy-time validation failure — <see cref="RateBand"/>
/// does not guard <c>tanBasisPoints</c> at construction (a negative TAN is a valid band shape, by
/// design, e.g. negative-rate environments), so this bound is the gate, not the type.
/// </summary>
public sealed class PackRateBoundsSource(IPackStore packStore) : IRateBoundsSource
{
    public RateBounds For(string packVersion) =>
        new(0, packStore.Resolve(packVersion).Parameters.MaxConsumerRateBps);
}

/// <summary>
/// Supplies the active product configs the cross-artefact rate-sheet invariants validate against
/// (surface §2.5): every product the sheet prices must exist in an active config, and every active
/// config's <c>rate_ref</c> must be covered by the sheet. This is the product-config registry seam,
/// consumed by both the validator and the deploy path. INTERIM: there is no in-engine product-config
/// registry until Epic E/F, so the v1 default (<see cref="EmptyProductConfigSource"/>) reports no
/// active configs and the two cross-artefact checks pass vacuously — a sheet is judged on its
/// self-contained shape alone.
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
