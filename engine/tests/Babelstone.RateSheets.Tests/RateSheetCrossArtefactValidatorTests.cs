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
    public void Rejects_a_config_whose_rate_ref_names_a_different_product()
    {
        // bd babelstone-ktfx: a config's rate_ref must reference its OWN product_id (surface §2.2:
        // the rate_ref lives inside the product config and resolves that product's role). A config
        // for dpz_pt_12m_juros_venc whose rate_ref points at a different product is malformed —
        // caught at deploy rather than silently mispricing at constitution. The sheet itself covers
        // both pairs, so the only failure here is the product-id mismatch (not a coverage gap).
        var crossWiredBody = new RateSheetBody
        {
            Products = new()
            {
                ["dpz_pt_12m_juros_venc"] = new()
                {
                    ["standard"] = new RoleRates { Bands = [RateSheetTestData.Band(50_000, null, 300)] },
                },
                ["dpz_pt_24m_juros_venc"] = new()
                {
                    ["standard"] = new RoleRates { Bands = [RateSheetTestData.Band(50_000, null, 320)] },
                },
            },
        };
        var activeConfigs = new[]
        {
            new ActiveProductConfig(
                "dpz_pt_12m_juros_venc",
                // The ref names dpz_pt_24m_juros_venc, not the config's own dpz_pt_12m_juros_venc.
                new[] { new RateRef("dpz_pt_24m_juros_venc", "standard") }),
        };

        var result = _validator.Validate(crossWiredBody, RateSheetTestData.Bounds, activeConfigs);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            d => d.Contains("dpz_pt_24m_juros_venc", StringComparison.Ordinal)
                 && d.Contains("its own product_id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_covered_pair_prices_the_whole_principal_range_so_no_per_principal_pinning()
    {
        // bd babelstone-ktfx: the §2.5 decision is whole-range principal coverage — a covered
        // (product, role) pair's bands are exhaustive over [min, ∞), so the ref needs no principal.
        // Resolve proves it: the same (product, role) the active config's rate_ref names prices the
        // smallest in-range principal AND an arbitrarily large one, with no gap in between.
        var sheet = new RateSheetResolution("v1", RateSheetTestData.ValidBody());

        // standard's lowest band starts at 50_000; the open-ended top band runs to +∞.
        Assert.Equal(300, sheet.ResolveTanBasisPoints("dpz_pt_12m_juros_venc", "standard", 50_000));
        Assert.Equal(350, sheet.ResolveTanBasisPoints("dpz_pt_12m_juros_venc", "standard", long.MaxValue));

        // The ref the active config carries covers the pair; coverage is whole-range, not pinned.
        Assert.True(RateSheetValidator.Covers(
            RateSheetTestData.ValidBody(), new RateRef("dpz_pt_12m_juros_venc", "standard")));
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
