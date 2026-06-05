using System.Text.Json;
using Babelstone.RateSheets;
using Xunit;

namespace Babelstone.RateSheets.Tests;

/// <summary>
/// <see cref="RateBand"/> is correct-by-construction (ADR-PC-008 §P1): the
/// <c>[lower, upper]</c> wire shape is validated by <see cref="RateBandJsonConverter"/> on
/// deserialize and by the constructor in code, so a malformed band cannot exist for a
/// resolver to read "from 0" by accident. These tests pin the per-band shape rules that
/// used to live in <see cref="RateSheetValidator"/>, plus the 1:1 JSONB round-trip.
/// </summary>
public sealed class RateBandTests
{
    [Fact]
    public void Round_trips_a_bounded_band_as_the_principal_cents_wire_array()
    {
        var band = new RateBand(50_000, 5_000_000, 300);

        var json = JsonSerializer.Serialize(band, RateSheetJson.Options);
        var back = JsonSerializer.Deserialize<RateBand>(json, RateSheetJson.Options);

        Assert.Equal("{\"principal_cents\":[50000,5000000],\"tan_basis_points\":300}", json);
        Assert.NotNull(back);
        Assert.Equal(50_000, back.From);
        Assert.Equal(5_000_000, back.To);
        Assert.Equal(300, back.TanBasisPoints);
    }

    [Fact]
    public void Round_trips_an_open_ended_top_band_with_a_null_upper()
    {
        var band = new RateBand(25_000_000, null, 350);

        var json = JsonSerializer.Serialize(band, RateSheetJson.Options);
        var back = JsonSerializer.Deserialize<RateBand>(json, RateSheetJson.Options);

        Assert.Equal("{\"principal_cents\":[25000000,null],\"tan_basis_points\":350}", json);
        Assert.NotNull(back);
        Assert.Equal(25_000_000, back.From);
        Assert.Null(back.To);
    }

    [Theory]
    [InlineData("{\"principal_cents\":[50000],\"tan_basis_points\":300}")]               // one element
    [InlineData("{\"principal_cents\":[50000,5000000,1],\"tan_basis_points\":300}")]     // three elements
    [InlineData("{\"principal_cents\":[],\"tan_basis_points\":300}")]                    // empty
    public void Rejects_a_principal_cents_array_that_is_not_length_2(string json)
    {
        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RateBand>(json, RateSheetJson.Options));

        Assert.Contains("exactly 2 elements", ex.Message);
    }

    [Fact]
    public void Rejects_a_null_lower_bound_at_deserialize()
    {
        const string json = "{\"principal_cents\":[null,5000000],\"tan_basis_points\":300}";

        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RateBand>(json, RateSheetJson.Options));

        Assert.Contains("lower bound", ex.Message);
    }

    [Fact]
    public void Rejects_a_negative_lower_bound_at_deserialize()
    {
        const string json = "{\"principal_cents\":[-1,5000000],\"tan_basis_points\":300}";

        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RateBand>(json, RateSheetJson.Options));

        Assert.Contains(">= 0", ex.Message);
    }

    [Fact]
    public void Rejects_an_upper_bound_not_above_the_lower_at_deserialize()
    {
        const string json = "{\"principal_cents\":[5000000,5000000],\"tan_basis_points\":300}";

        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RateBand>(json, RateSheetJson.Options));

        Assert.Contains("greater than lower bound", ex.Message);
    }

    [Fact]
    public void Rejects_a_missing_principal_cents_field()
    {
        const string json = "{\"tan_basis_points\":300}";

        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RateBand>(json, RateSheetJson.Options));

        Assert.Contains("principal_cents", ex.Message);
    }

    [Fact]
    public void The_constructor_rejects_a_negative_lower_bound()
    {
        Assert.Throws<ArgumentException>(() => new RateBand(-1, 5_000_000, 300));
    }

    [Fact]
    public void The_constructor_rejects_an_upper_bound_not_above_the_lower()
    {
        Assert.Throws<ArgumentException>(() => new RateBand(5_000_000, 5_000_000, 300));
    }
}
