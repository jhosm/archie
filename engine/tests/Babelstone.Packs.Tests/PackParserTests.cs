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
    public void A_declared_but_engine_unimplemented_formula_ref_fails_loud()
        // A formula_ref the engine does not implement must throw, not silently default to a
        // wrong accrual basis. The bridge is PackDayCount.ToConvention(); resolve a synthetic
        // entry naming a formula_ref absent from the switch (the shipped pack carries no such
        // dead entry — see PackDeclarationsResolveTests, which fences that for every pack).
        => Assert.Throws<PackLoadException>(
            () => new PackDayCount("engine.day_count.not_implemented", []).ToConvention());

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
    public void Family_manifest_pins_the_term_deposit_family(/* bd babelstone-9w2k.3 */)
    {
        // The pinned family set the host cross-checks scanned modules against (ADR-PC-009 §P1). The
        // schema_version here must agree with the SAME family's schema_pins entry — one pin, two readers.
        var families = Pt2026().Families;
        Assert.Single(families);
        var termDeposit = families[0];
        Assert.Equal("term_deposit", termDeposit.FamilyName);
        Assert.Equal("term_deposit", termDeposit.AggregateType);
        Assert.Equal("term_deposit@2026.1", termDeposit.SchemaVersion);
        Assert.Equal("Babelstone.Families.TermDeposit.Application", termDeposit.PluginAssembly);
        Assert.Equal(Pt2026().Manifest.SchemaPins["term_deposit"], termDeposit.SchemaVersion);
    }

    [Fact]
    public void A_family_manifest_entry_missing_a_required_field_fails_loud()
    {
        // The structural parse null-checks every required field, so a family entry with no schema_version
        // fails loud (naming the file + field) rather than constructing a record with a default.
        var files = PackTestData.LoadPt2026();
        files["families.yaml"] = Encoding.UTF8.GetBytes(
            "families:\n  - family_name: term_deposit\n    aggregate_type: term_deposit\n"
            + "    plugin_assembly: Babelstone.Families.TermDeposit.Application\n");
        var ex = Assert.Throws<PackLoadException>(() => PackParser.Parse(files, "pt.2026.1"));
        Assert.Contains("schema_version", ex.Message);
    }

    [Fact]
    public void A_missing_family_manifest_file_fails_loud()
    {
        var files = PackTestData.LoadPt2026();
        files.Remove("families.yaml");
        var ex = Assert.Throws<PackLoadException>(() => PackParser.Parse(files, "pt.2026.1"));
        Assert.Contains("families.yaml", ex.Message);
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

    [Fact]
    public void A_non_iso_pack_effective_from_fails_loud()
    {
        // ParseDate is strict YYYY-MM-DD (DateTimeStyles.None, invariant culture): a date that does not
        // match the exact format must throw, not coerce or default to a wrong effective date. Pins the
        // TryParseExact failure branch (a relaxed parse would let a wrong effective date into the engine).
        var files = PackTestData.LoadPt2026();
        var manifest = Encoding.UTF8.GetString(files["pack.yaml"])
            .Replace("pack_effective_from: 2026-01-01", "pack_effective_from: 01/01/2026", StringComparison.Ordinal);
        files["pack.yaml"] = Encoding.UTF8.GetBytes(manifest);
        var ex = Assert.Throws<PackLoadException>(() => PackParser.Parse(files, "pt.2026.1"));
        Assert.Contains("pack_effective_from", ex.Message);
    }

    [Fact]
    public void An_empty_rate_sheet_ref_entry_fails_loud()
    {
        // A null/empty list entry in a rate-sheet-ref file must fail loud (naming the file), not be
        // silently skipped — pins the `r is null` guard so a hole in the refs list cannot pass unnoticed.
        var files = PackTestData.LoadPt2026();
        files["rate-sheet-refs/deposits-pt.yaml"] = Encoding.UTF8.GetBytes("refs:\n  - \n");
        var ex = Assert.Throws<PackLoadException>(() => PackParser.Parse(files, "pt.2026.1"));
        Assert.Contains("rate-sheet-refs/deposits-pt.yaml", ex.Message);
    }

    [Fact]
    public void A_rate_sheet_ref_missing_a_required_field_fails_loud()
    {
        // A ref entry present but missing rate_sheet_version_id fails loud on the required-field check,
        // rather than constructing a ref with an empty version id that would later resolve to nothing.
        var files = PackTestData.LoadPt2026();
        files["rate-sheet-refs/deposits-pt.yaml"] = Encoding.UTF8.GetBytes(
            "refs:\n  - product_family: term_deposit\n");
        var ex = Assert.Throws<PackLoadException>(() => PackParser.Parse(files, "pt.2026.1"));
        Assert.Contains("rate_sheet_version_id", ex.Message);
    }
}
