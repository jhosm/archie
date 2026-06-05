using Babelstone.RateSheets;
using Babelstone.RateSheets.Api;

namespace Babelstone.RateSheets.Tests;

/// <summary>
/// The surface §2.2 worked example as test fixtures: a term-deposit sheet with a
/// banded <c>standard</c> role and an open-ended <c>new_money</c> role, all within
/// pt.2026.1's <c>[0, 2000]</c> bound.
/// </summary>
internal static class RateSheetTestData
{
    public static readonly RateBounds Bounds = new(0, 2000);

    public static readonly DateTimeOffset DefaultEffectiveFrom =
        new(2026, 5, 19, 0, 0, 0, TimeSpan.Zero);

    public static RateBand Band(long from, long? to, int bps) =>
        new(from, to, bps);

    public static RateSheetBody ValidBody() => new()
    {
        Products = new()
        {
            ["dpz_pt_12m_juros_venc"] = new()
            {
                ["standard"] = new RoleRates
                {
                    Bands =
                    [
                        Band(50_000, 5_000_000, 300),
                        Band(5_000_000, 25_000_000, 325),
                        Band(25_000_000, null, 350),
                    ],
                },
                ["new_money"] = new RoleRates
                {
                    Bands = [Band(50_000, null, 400)],
                },
            },
        },
    };

    public static RateSheet ValidSheet(
        string versionId = "pt-deposits-2026.1",
        string family = "term_deposit",
        DateTimeOffset? effectiveFrom = null,
        RateSheetBody? body = null) => new(
            versionId,
            family,
            "pt.2026.1",
            effectiveFrom ?? DefaultEffectiveFrom,
            body ?? ValidBody(),
            ApprovedBy: "treasury.alm@bank.internal",
            ApprovalRef: "ALM-2026-019",
            PublishedBy: "deploy-bot@engine.internal");

    public static RateSheetDeployRequest ValidRequest(
        string versionId = "pt-deposits-2026.1",
        DateTimeOffset? effectiveFrom = null,
        RateSheetBody? body = null) => new(
            versionId,
            "term_deposit",
            "pt.2026.1",
            effectiveFrom ?? DefaultEffectiveFrom,
            "treasury.alm@bank.internal",
            "ALM-2026-019",
            (body ?? ValidBody()).Products);
}
