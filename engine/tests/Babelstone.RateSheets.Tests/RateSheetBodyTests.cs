using System.Text.Json;
using Babelstone.RateSheets;
using Xunit;

namespace Babelstone.RateSheets.Tests;

/// <summary>
/// Body JSON round-trip, the order-insensitive canonical form used for §P2 idempotency,
/// and the §P3 <c>(product, role, principal) -&gt; tan_basis_points</c> resolution.
/// </summary>
public sealed class RateSheetBodyTests
{
    [Fact]
    public void Body_round_trips_through_snake_case_json()
    {
        var body = RateSheetTestData.ValidBody();

        var json = JsonSerializer.Serialize(body, RateSheetJson.Options);
        var back = JsonSerializer.Deserialize<RateSheetBody>(json, RateSheetJson.Options);

        Assert.Contains("principal_cents", json);
        Assert.Contains("tan_basis_points", json);
        Assert.DoesNotContain("\"from\"", json); // From/To are [JsonIgnore]d computed views
        Assert.NotNull(back);
        Assert.Equal(
            RateSheetJson.Canonical(body),
            RateSheetJson.Canonical(back));
    }

    [Fact]
    public void Canonical_is_insensitive_to_object_key_order()
    {
        var a = new RateSheetBody
        {
            Products = new()
            {
                ["p1"] = new() { ["standard"] = new RoleRates { Bands = [RateSheetTestData.Band(0, null, 100)] } },
                ["p2"] = new() { ["standard"] = new RoleRates { Bands = [RateSheetTestData.Band(0, null, 200)] } },
            },
        };
        var b = new RateSheetBody
        {
            Products = new()
            {
                // same content, reversed insertion order
                ["p2"] = new() { ["standard"] = new RoleRates { Bands = [RateSheetTestData.Band(0, null, 200)] } },
                ["p1"] = new() { ["standard"] = new RoleRates { Bands = [RateSheetTestData.Band(0, null, 100)] } },
            },
        };

        Assert.Equal(RateSheetJson.Canonical(a), RateSheetJson.Canonical(b));
    }

    [Theory]
    [InlineData("standard", 100_000, 300)]      // first band [50_000, 5_000_000)
    [InlineData("standard", 5_000_000, 325)]    // lower bound is inclusive -> second band
    [InlineData("standard", 60_000_000, 350)]   // open-ended top band
    [InlineData("new_money", 100_000, 400)]
    public void Resolves_the_band_covering_a_principal(string role, long principalCents, int expectedBps)
    {
        var resolution = new RateSheetResolution("pt-deposits-2026.1", RateSheetTestData.ValidBody());

        var bps = resolution.ResolveTanBasisPoints("dpz_pt_12m_juros_venc", role, principalCents);

        Assert.Equal(expectedBps, bps);
    }

    [Fact]
    public void Resolves_to_null_below_the_floor()
    {
        var resolution = new RateSheetResolution("pt-deposits-2026.1", RateSheetTestData.ValidBody());

        Assert.Null(resolution.ResolveTanBasisPoints("dpz_pt_12m_juros_venc", "standard", 49_999));
    }

    [Fact]
    public void Resolves_to_null_for_an_unknown_product_or_role()
    {
        var resolution = new RateSheetResolution("pt-deposits-2026.1", RateSheetTestData.ValidBody());

        Assert.Null(resolution.ResolveTanBasisPoints("nope", "standard", 100_000));
        Assert.Null(resolution.ResolveTanBasisPoints("dpz_pt_12m_juros_venc", "nope", 100_000));
    }
}
