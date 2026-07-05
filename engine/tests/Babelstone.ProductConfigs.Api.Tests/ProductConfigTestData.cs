using System.Text.Json.Nodes;
using Babelstone.ProductConfigs.Api;
using Babelstone.RateSheets;

namespace Babelstone.ProductConfigs.Api.Tests;

/// <summary>
/// A worked-example product-config as test fixtures (ADR-PC-009 §A2): the structural shape of
/// <c>dpz_pt_12m_juros_venc</c> (a 365-day AT_MATURITY term deposit, no auto-renewal), 1:1 with the
/// committed <c>product-configs/*.yaml</c> shape the registry versions.
/// </summary>
internal static class ProductConfigTestData
{
    public static readonly DateTimeOffset DefaultEffectiveFrom =
        new(2026, 5, 19, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The structural config body — the SHAPE fields (term / interest variant / renewal /
    /// partial-withdrawal gates), never a price. A JsonObject so the registry stores it verbatim as JSONB.</summary>
    public static JsonObject ValidBody() => new()
    {
        ["variant_id"] = "dpz_pt_12m_juros_venc",
        ["term_days"] = 365,
        ["interest_variant"] = "AT_MATURITY",
        ["auto_renewal_policy"] = "NONE",
        ["payment_period_months"] = 0,
        ["partial_withdrawal"] = new JsonObject
        {
            ["min_withdrawal_cents"] = 50_000,
            ["min_remaining_balance_cents"] = 100_000,
            ["lockup_period_days"] = 30,
        },
    };

    public static ProductConfigVersion ValidVersion(
        string versionId = "dpz_pt_12m_juros_venc@2026.1",
        string productId = "dpz_pt_12m_juros_venc",
        DateTimeOffset? effectiveFrom = null,
        JsonObject? body = null)
    {
        var b = body ?? ValidBody();
        return new ProductConfigVersion(
            versionId,
            productId,
            "pt.2026.1",
            effectiveFrom ?? DefaultEffectiveFrom,
            b,
            ProductConfigJson.ContentHash(b),
            ApprovedBy: "product.owner@bank.internal",
            ApprovalRef: "PC-2026-011",
            PublishedBy: "deploy-bot@engine.internal");
    }

    public static ProductConfigDeployRequest ValidRequest(
        string versionId = "dpz_pt_12m_juros_venc@2026.1",
        string productId = "dpz_pt_12m_juros_venc",
        DateTimeOffset? effectiveFrom = null,
        JsonObject? body = null) => new(
            versionId,
            productId,
            "pt.2026.1",
            effectiveFrom ?? DefaultEffectiveFrom,
            "product.owner@bank.internal",
            "PC-2026-011",
            body ?? ValidBody());
}
