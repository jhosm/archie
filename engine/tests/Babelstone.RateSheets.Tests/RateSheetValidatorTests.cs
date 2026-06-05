using Babelstone.RateSheets;
using Xunit;

namespace Babelstone.RateSheets.Tests;

/// <summary>
/// The deploy-time validator (ADR-PC-008 §P2, surface §2.5 self-contained invariants):
/// a sheet with a gap, overlap, non-exhaustive tail, or out-of-bound TAN is rejected
/// before it ever reaches the store.
/// </summary>
public sealed class RateSheetValidatorTests
{
    private readonly RateSheetValidator _validator = new();

    [Fact]
    public void Accepts_the_worked_example()
    {
        var result = _validator.Validate(RateSheetTestData.ValidBody(), RateSheetTestData.Bounds);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Rejects_an_empty_sheet()
    {
        var result = _validator.Validate(new RateSheetBody(), RateSheetTestData.Bounds);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Rejects_a_gap_between_bands()
    {
        var body = SingleRole(
            RateSheetTestData.Band(50_000, 5_000_000, 300),
            // gap: previous band ended at 5_000_000, this starts at 6_000_000
            RateSheetTestData.Band(6_000_000, null, 350));

        var result = _validator.Validate(body, RateSheetTestData.Bounds);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Contains("gap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rejects_overlapping_bands()
    {
        var body = SingleRole(
            RateSheetTestData.Band(50_000, 6_000_000, 300),
            // overlap: previous band ended at 6_000_000, this starts at 5_000_000
            RateSheetTestData.Band(5_000_000, null, 350));

        var result = _validator.Validate(body, RateSheetTestData.Bounds);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Contains("overlap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rejects_a_bounded_highest_band_as_non_exhaustive()
    {
        var body = SingleRole(
            RateSheetTestData.Band(50_000, 5_000_000, 300),
            // top band is NOT open-ended — principals above 5_000_000 are unpriced
            RateSheetTestData.Band(5_000_000, 25_000_000, 350));

        var result = _validator.Validate(body, RateSheetTestData.Bounds);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Contains("exhaustive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rejects_an_open_ended_band_that_is_not_the_highest()
    {
        var body = SingleRole(
            RateSheetTestData.Band(50_000, null, 300),
            RateSheetTestData.Band(5_000_000, null, 350));

        var result = _validator.Validate(body, RateSheetTestData.Bounds);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Contains("unreachable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rejects_a_tan_above_the_pack_bound()
    {
        var body = SingleRole(RateSheetTestData.Band(50_000, null, 2_001));

        var result = _validator.Validate(body, RateSheetTestData.Bounds);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Contains("bounds", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rejects_a_negative_tan()
    {
        var body = SingleRole(RateSheetTestData.Band(50_000, null, -1));

        var result = _validator.Validate(body, RateSheetTestData.Bounds);

        Assert.False(result.IsValid);
    }

    // Per-band SHAPE (range length, null/negative lower, upper-above-lower) is no longer a
    // validator concern: RateBand is correct-by-construction, so a malformed band is rejected
    // at deserialize / construction and can never reach the validator. Those cases now live in
    // RateBandTests; only the cross-band and pack-bound checks remain here.

    [Fact]
    public void RateBounds_rejects_an_inverted_bound()
    {
        Assert.Throws<ArgumentException>(() => new RateBounds(2_000, 0));
    }

    private static RateSheetBody SingleRole(params RateBand[] bands) => new()
    {
        Products = new()
        {
            ["dpz_pt_12m_juros_venc"] = new()
            {
                ["standard"] = new RoleRates { Bands = [.. bands] },
            },
        },
    };
}
