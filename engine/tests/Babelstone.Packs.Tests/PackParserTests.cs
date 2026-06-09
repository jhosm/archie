using System.Text;
using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Packs.Tests;

/// <summary>
/// Offline structural-parse tests against the committed pt.2026.1 pack (no oras/cosign,
/// default CI lane). Pins the data model E.2/E.3 resolve against and the fail-loud branches.
/// </summary>
public sealed class PackParserTests
{
    private static VerifiedPack Pt2026() => PackParser.Parse(PackTestData.LoadPt2026(), "pt.2026.1");

    [Fact]
    public void Parses_manifest_identity_and_pins()
    {
        var pack = Pt2026();
        Assert.Equal("pt", pack.Manifest.PackId);
        Assert.Equal("2026.1", pack.Manifest.PackVersion);
        Assert.Equal("pt.2026.1", pack.VersionKey);
        Assert.Equal(new DateOnly(2026, 1, 1), pack.Manifest.PackEffectiveFrom);
        Assert.Equal("term_deposit@2026.1", pack.Manifest.SchemaPins["term_deposit"]);
    }

    [Fact]
    public void Resolves_act_360_to_the_engine_convention()
        => Assert.Equal(DayCountConvention.Act360, Pt2026().ResolveDayCount("act_360"));

    [Fact]
    public void Resolves_30_360_european()
        => Assert.Equal(DayCountConvention.Thirty360European, Pt2026().ResolveDayCount("30_360_european"));

    [Fact]
    public void Day_count_carries_the_pack_declared_permitted_for_set()
    {
        // The engine models permitted_for (babelstone-fk7m.2): act_360 is permitted for term_deposit,
        // the others for nothing yet. The strict parser must accept this governed field, not reject it.
        var dayCounts = Pt2026().DayCounts;
        Assert.Equal(["term_deposit"], dayCounts["act_360"].PermittedFor);
        Assert.Empty(dayCounts["act_365"].PermittedFor);
    }

    [Fact]
    public void A_day_count_formula_ref_with_no_engine_convention_fails_loud()
        // ToConvention refuses to silently default when a formula_ref names a primitive the
        // engine doesn't implement (e.g. the dropped act_act_isda, fk7m.8) — the pack no longer
        // carries such an entry, so exercise the defensive arm directly.
        => Assert.Throws<PackLoadException>(
            () => new PackDayCount("engine.day_count.actual_actual_isda", []).ToConvention());

    [Fact]
    public void An_undeclared_day_count_id_fails_loud()
        => Assert.Throws<PackLoadException>(() => Pt2026().ResolveDayCount("does_not_exist"));

    [Fact]
    public void Withholding_irs_juros_is_28pct_gross_at_credit()
    {
        var withholding = Pt2026().Withholdings["irs_juros"];
        Assert.Equal(2800, withholding.RateBasisPoints);
        Assert.Equal("gross_interest", withholding.Basis);
        Assert.Equal("at_credit", withholding.Timing);
        Assert.Equal(3, withholding.Exemptions.Count);
        Assert.True(withholding.Reporting["modelo_39"].Required);
        Assert.Equal("annual", withholding.Reporting["modelo_39"].Frequency);
    }

    [Fact]
    public void Parameters_are_parsed()
    {
        var parameters = Pt2026().Parameters;
        Assert.Equal(2000, parameters.MaxConsumerRateBps);
        Assert.Equal(14, parameters.AutoRenewalOptoutWindowDays);
    }

    [Fact]
    public void Fgd_coverage_ceiling_is_100k_eur()
    {
        var fgd = Pt2026().Fgds["deposit_guarantee"];
        Assert.Equal(10_000_000L, fgd.CoverageCeilingCents);
        Assert.Equal(new Money(10_000_000), fgd.CoverageCeiling);
        Assert.Equal("fgd_pt", fgd.Scheme);
    }

    [Fact]
    public void Reporting_activates_only_the_retail_rate_statistics()
    {
        var reportings = Pt2026().Reportings;
        Assert.True(reportings["bdp_estatisticas_taxas_juro"].Active);
        Assert.False(reportings["ifrs9_staging"].Active);
    }

    [Fact]
    public void Rate_sheet_refs_point_at_the_term_deposit_sheet()
    {
        var refs = Pt2026().RateSheetRefs;
        Assert.Single(refs);
        Assert.Equal("term_deposit", refs[0].ProductFamily);
        Assert.Equal("pt-deposits-2026.1", refs[0].RateSheetVersionId);
    }

    [Fact]
    public void A_version_key_mismatch_fails_loud()
    {
        var ex = Assert.Throws<PackLoadException>(() => PackParser.Parse(PackTestData.LoadPt2026(), "pt.2026.2"));
        Assert.Contains("version-key mismatch", ex.Message);
    }

    [Fact]
    public void An_unknown_key_in_the_closed_parameters_file_fails_loud()
    {
        var files = PackTestData.LoadPt2026();
        files["parameters/constants.yaml"] = Encoding.UTF8.GetBytes(
            "max_consumer_rate_bps: 2000\nauto_renewal_optout_window_days: 14\nunexpected_key: 7\n");
        Assert.Throws<PackLoadException>(() => PackParser.Parse(files, "pt.2026.1"));
    }

    [Fact]
    public void An_empty_bodied_primitive_entry_fails_loud()
    {
        var files = PackTestData.LoadPt2026();
        files["primitives/day-count.yaml"] = Encoding.UTF8.GetBytes("act_360:\n"); // key with no formula_ref body
        var ex = Assert.Throws<PackLoadException>(() => PackParser.Parse(files, "pt.2026.1"));
        Assert.Contains("empty body", ex.Message);
    }

    [Fact]
    public void A_missing_required_file_fails_loud()
    {
        var files = PackTestData.LoadPt2026();
        files.Remove("primitives/day-count.yaml");
        Assert.Throws<PackLoadException>(() => PackParser.Parse(files, "pt.2026.1"));
    }
}
