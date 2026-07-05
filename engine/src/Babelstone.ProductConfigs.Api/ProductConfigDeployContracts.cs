using System.Text.Json.Nodes;
using Babelstone.RateSheets;

namespace Babelstone.ProductConfigs.Api;

/// <summary>
/// The <c>POST /v1/product-configs</c> request body (ADR-PC-009 §A2, ADR-PC-008). Mirrors
/// <c>RateSheetDeployRequest</c>: the envelope fields are columns on the stored row; <see cref="Body"/>
/// is the JSONB config body (1:1 with the deployed YAML). The deploying principal is NOT in the payload
/// — it arrives as the gateway-authenticated <c>X-Deploy-Actor</c> header (ADR-PC-008) and is recorded
/// as <c>published_by</c>; <see cref="ApprovedBy"/> / <see cref="ApprovalRef"/> carry the product/config
/// sign-off the row must record. The content hash is NOT accepted from the caller — the registry
/// computes it server-side from the canonical body (the bridge to the interim content-hash pin,
/// bd babelstone-fk7m.9), so a caller can neither spoof nor omit it.
/// </summary>
public sealed record ProductConfigDeployRequest(
    string ProductConfigVersionId,
    string ProductId,
    string PackVersion,
    DateTimeOffset EffectiveFrom,
    string ApprovedBy,
    string ApprovalRef,
    JsonObject Body);

/// <summary>The stored config version as returned on 200/201 — symmetric with the request, plus the
/// server-computed content hash and publication metadata.</summary>
public sealed record ProductConfigDeployResponse(
    string ProductConfigVersionId,
    string ProductId,
    string PackVersion,
    DateTimeOffset EffectiveFrom,
    string ApprovedBy,
    string ApprovalRef,
    string PublishedBy,
    DateTimeOffset? PublishedAt,
    string ContentHash,
    JsonObject Body)
{
    public static ProductConfigDeployResponse From(ProductConfigVersion version) => new(
        version.ProductConfigVersionId,
        version.ProductId,
        version.PackVersion,
        version.EffectiveFrom,
        version.ApprovedBy,
        version.ApprovalRef,
        version.PublishedBy,
        version.PublishedAt,
        version.ContentHash,
        version.Body);
}

/// <summary>
/// The 409 forward-only-immutability breach envelope (ADR-PC-008 §P5, ADR-PC-009 §A2): what the deploy
/// returns when a <c>product_config_version_id</c> already exists with a different definition, or the
/// product's <c>effective_from</c> is already claimed. Public (not a handler-private detail) because it
/// is the deploy surface's catalogued wire shape, exactly like <c>RateSheetConflict</c>.
/// </summary>
public sealed record ProductConfigConflict(string Error);
