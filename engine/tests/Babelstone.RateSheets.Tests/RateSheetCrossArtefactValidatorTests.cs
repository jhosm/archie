using Babelstone.RateSheets;
using Xunit;

namespace Babelstone.RateSheets.Tests;

/// <summary>
/// The deploy-time <em>cross-artefact</em> §2.5 invariants (ADR-PC-008 §P2, surface §2.5):
/// every product the sheet prices exists in an active product config, and every active config's
/// <c>rate_ref</c> is covered by the sheet. The symmetric reverse — a config rejected when the
/// active sheet doesn't cover its <c>rate_ref</c> — reuses the same coverage primitive
/// (<see cref="RateSheetValidator.Covers"/>), exercised here so a future product-config deploy
/// path inherits a tested check. With no active configs the cross-artefact checks pass vacuously,
/// so an existing deploy is never rejected merely because the registry is unwired.
/// </summary>
public sealed class RateSheetCrossArtefactValidatorTests
{
    private readonly RateSheetValidator _validator = new();

    [Fact]
    public void Accepts_a_sheet_covering_every_active_config()
    {
        var result = _validator.Validate(
            RateSheetTestData.ValidBody(), RateSheetTestData.Bounds, RateSheetTestData.ActiveConfigs());

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Passes_vacuously_with_an_empty_registry()
    {
        // No active configs: the cross-artefact checks are skipped, so a self-contained-valid
        // sheet deploys — the backwards-compatible default that doesn't reject existing deploys
        // just because no product-config registry is wired in yet (Epic E/F).
        var result = _validator.Validate(
            RateSheetTestData.ValidBody(), RateSheetTestData.Bounds, []);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void The_no_config_overload_matches_an_empty_registry()
    {
        // The 2-arg overload (no registry) is exactly the 3-arg overload with an empty config list:
        // both judge the sheet on its self-contained shape alone.
        var twoArg = _validator.Validate(RateSheetTestData.ValidBody(), RateSheetTestData.Bounds);
        var emptyConfigs = _validator.Validate(RateSheetTestData.ValidBody(), RateSheetTestData.Bounds, []);

        Assert.Equal(twoArg.IsValid, emptyConfigs.IsValid);
        Assert.Equal(twoArg.Diagnostics, emptyConfigs.Diagnostics);
    }

    [Fact]
    public void Rejects_a_sheet_pricing_a_product_no_active_config_references()
    {
        // The sheet prices dpz_pt_12m_juros_venc, but the only active config is for a different
        // product — the priced product is an orphaned reference (invariant 1).
        var activeConfigs = new[]
        {
            new ActiveProductConfig(
                "dpz_pt_24m_juros_venc",
                new[] { new RateRef("dpz_pt_24m_juros_venc", "standard") }),
        };

        var result = _validator.Validate(
            RateSheetTestData.ValidBody(), RateSheetTestData.Bounds, activeConfigs);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            d => d.Contains("dpz_pt_12m_juros_venc", StringComparison.Ordinal)
                 && d.Contains("no active product config", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rejects_a_sheet_that_does_not_cover_an_active_configs_rate_ref()
    {
        // The active config asks for a 'promo' role the sheet doesn't price (invariant 2): a
        // constitution selecting that role would be unpriceable, so the deploy is rejected.
        var activeConfigs = new[]
        {
            new ActiveProductConfig(
                "dpz_pt_12m_juros_venc",
                new[]
                {
                    new RateRef("dpz_pt_12m_juros_venc", "standard"),
                    new RateRef("dpz_pt_12m_juros_venc", "promo"),
                }),
        };

        var result = _validator.Validate(
            RateSheetTestData.ValidBody(), RateSheetTestData.Bounds, activeConfigs);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            d => d.Contains("promo", StringComparison.Ordinal)
                 && d.Contains("does not cover", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Covers_is_true_for_a_priced_rate_ref()
    {
        Assert.True(RateSheetValidator.Covers(
            RateSheetTestData.ValidBody(), new RateRef("dpz_pt_12m_juros_venc", "standard")));
        Assert.True(RateSheetValidator.Covers(
            RateSheetTestData.ValidBody(), new RateRef("dpz_pt_12m_juros_venc", "new_money")));
    }

    [Fact]
    public void Covers_is_false_for_an_unpriced_product_or_role()
    {
        var body = RateSheetTestData.ValidBody();

        // Unpriced role on a priced product, and a wholly unpriced product — the two ways a
        // future product-config deploy's rate_ref can fall outside the active sheet (surface §2.5).
        Assert.False(RateSheetValidator.Covers(body, new RateRef("dpz_pt_12m_juros_venc", "promo")));
        Assert.False(RateSheetValidator.Covers(body, new RateRef("dpz_pt_24m_juros_venc", "standard")));
    }
}
