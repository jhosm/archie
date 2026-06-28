using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;

namespace Babelstone.Packs;

/// <summary>
/// A regulatory pack that has been pulled by digest, cosign-verified, and structurally parsed
/// (ADR-PC-007) — the immutable in-memory model the engine resolves primitives and
/// parameters against. Held version-keyed in <see cref="OciPackStore"/>'s cache; a handler
/// reads it purely (no I/O). The data shape is the pack.sh DATA_FILES layout one-for-one.
/// </summary>
public sealed record VerifiedPack(
    PackManifest Manifest,
    IReadOnlyDictionary<string, PackDayCount> DayCounts,
    IReadOnlyDictionary<string, PackWithholding> Withholdings,
    IReadOnlyDictionary<string, PackFgd> Fgds,
    IReadOnlyDictionary<string, PackReporting> Reportings,
    PackParameters Parameters,
    IReadOnlyList<PackRateSheetRef> RateSheetRefs,
    IReadOnlyList<PackFamily> Families)
{
    /// <summary>The immutable composite version key <c>&lt;pack_id&gt;.&lt;pack_version&gt;</c> (e.g. <c>pt.2026.1</c>).</summary>
    public string VersionKey => $"{Manifest.PackId}.{Manifest.PackVersion}";

    /// <summary>
    /// Maps a pack-declared day-count id (e.g. <c>act_360</c>) to the engine convention.
    /// Throws (never defaults) if the id is undeclared or its formula_ref has no engine
    /// convention — a variant naming an unsupported day-count must fail loud, not silently
    /// accrue on the wrong basis.
    /// </summary>
    public DayCountConvention ResolveDayCount(string id) =>
        DayCounts.TryGetValue(id, out var dayCount)
            ? dayCount.ToConvention()
            : throw new PackLoadException(VersionKey, null, $"day-count id '{id}' is not declared in the pack.");
}

/// <summary>The pack manifest (pack.yaml): identity, metadata, and version pins (ADR-PC-007).</summary>
/// <param name="TemplateRefNames">The names of the pack-shipped disclosure-template files
/// (<c>templates/&lt;name&gt;.yaml</c>) this pack declares (ADR-PC-025; e.g. <c>notices</c>) — the same
/// per-pack-set shape as <see cref="RateSheetRefNames"/>. Surfaced so a downstream disclosure producer
/// can require its template-set is declared by the instance-pinned pack and fail loud if a deployment's
/// pack omits it — every deposit discloses under the pack it was opened with (ADR-PC-025). The parser
/// surfaces the manifest list, but the template bodies are not rendered here.</param>
public sealed record PackManifest(
    string PackId,
    string PackVersion,
    string Namespace,
    int ManifestSchemaVersion,
    string Publisher,
    DateOnly PackEffectiveFrom,
    string? BasedOnPackVersion,
    string DeltaSummary,
    IReadOnlyList<PackBreakingChange> BreakingChanges,
    string EngineCompatibleVersions,
    IReadOnlyDictionary<string, string> SchemaPins,
    IReadOnlyList<string> RateSheetRefNames,
    IReadOnlyList<string> TemplateRefNames,
    string TestCorpusRef);

public sealed record PackBreakingChange(string Id, string Description);

/// <summary>
/// A day-count primitive binding (primitives/day-count.yaml): an engine formula reference plus the
/// pack-declared <c>permitted_for</c> families (the depth-4 regulatory permitted-set).
/// The permitted-set is enforced by <c>pack-validate</c> at config-deploy time; the engine carries it
/// for audit visibility and does not re-enforce it at resolution.
/// </summary>
public sealed record PackDayCount(string FormulaRef, IReadOnlyList<string> PermittedFor)
{
    /// <summary>Bridges <c>formula_ref</c> to the engine <see cref="DayCountConvention"/>, or throws if none.</summary>
    public DayCountConvention ToConvention() => FormulaRef switch
    {
        "engine.day_count.actual_360" => DayCountConvention.Act360,
        "engine.day_count.actual_365" => DayCountConvention.Act365,
        "engine.day_count.thirty_360_european" => DayCountConvention.Thirty360European,
        _ => throw new PackLoadException(null, null,
            $"day-count formula_ref '{FormulaRef}' has no engine convention; refusing to default silently."),
    };
}

/// <summary>A withholding primitive (primitives/withholding.yaml): e.g. <c>irs_juros</c> at 2800 bps, flow-by-flow.</summary>
public sealed record PackWithholding(
    string FormulaRef,
    int RateBasisPoints,
    string Basis,
    string Timing,
    IReadOnlyList<PackWithholdingExemption> Exemptions,
    IReadOnlyDictionary<string, PackReportingObligation> Reporting);

public sealed record PackWithholdingExemption(string Id, string Evidence);

public sealed record PackReportingObligation(bool Required, string Frequency);

/// <summary>Deposit-guarantee-fund coverage (primitives/fgd.yaml).</summary>
public sealed record PackFgd(long CoverageCeilingCents, string Scheme)
{
    /// <summary>The per-depositor coverage ceiling as <see cref="Money"/> (cents-native).</summary>
    public Money CoverageCeiling => new(CoverageCeilingCents);
}

/// <summary>A regulator reporting hook (primitives/reporting.yaml).</summary>
public sealed record PackReporting(bool Active, string Frequency, string Regulator);

/// <summary>Closed pack-level scalar parameters (parameters/constants.yaml). An unknown key fails the parse.</summary>
public sealed record PackParameters(int MaxConsumerRateBps, int AutoRenewalOptoutWindowDays);

/// <summary>A version-pinned rate-sheet reference (rate-sheet-refs/*.yaml).</summary>
public sealed record PackRateSheetRef(string ProductFamily, string RateSheetVersionId);

/// <summary>
/// One entry of the pack's family-manifest (families.yaml; ADR-PC-007):
/// the FAMILY SET this deployment is pinned to run. The host's <c>HostModuleLoader</c> cross-checks
/// each scanned family host module against these entries and fails closed on a family/schema-version
/// skew or a pinned family with no loadable module (ADR-PC-009 — the pinned pack is the
/// authoritative per-deployment family set; every module stamps <see cref="SchemaVersion"/> onto every
/// EventEnvelope, so a newer-than-pinned module is an audit/replay hazard).
/// </summary>
/// <param name="FamilyName">The family id (e.g. <c>term_deposit</c>) — matches the module's <c>FamilyName</c>.</param>
/// <param name="AggregateType">The event-envelope aggregate_type / bus topic the family writes under (== <see cref="FamilyName"/>, the documented convention; ADR-IC-004).</param>
/// <param name="SchemaVersion">The pinned family schema version (e.g. <c>term_deposit@2026.1</c>) — matches <c>IFamilyModule.SchemaVersion</c> and the same family's <c>schema_pins</c> entry.</param>
/// <param name="PluginAssembly">The .NET assembly carrying the family's <c>IFamilyHostModule</c>, so a skew message can name the offending box.</param>
public sealed record PackFamily(
    string FamilyName, string AggregateType, string SchemaVersion, string PluginAssembly);
