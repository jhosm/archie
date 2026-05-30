using System.Globalization;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Babelstone.Packs;

/// <summary>
/// Structurally parses a pulled-and-verified pack's YAML data files into an immutable
/// <see cref="VerifiedPack"/> (ADR-PC-007 §P4). This is a STRUCTURAL parse, NOT a <c>cue vet</c>
/// re-run — the verified cosign signature already attests CUE depths 1–4 passed in CI
/// (§P2 / ADR-PC-006 §P3), which is what rejects duplicate keys and full-schema violations.
/// The structural parse fails loud on what it can see locally: a missing file, a YAML error, an
/// unknown key in a closed schema (the deserializer is strict — no <c>IgnoreUnmatchedProperties</c>),
/// an empty-bodied entry, a null/empty required field, an unmappable primitive, or a version-key
/// mismatch all raise <see cref="PackLoadException"/>. It does not re-derive the CUE-depth
/// guarantees (e.g. duplicate-key rejection); those ride on the verified signature.
/// </summary>
public static class PackParser
{
    // Strict: no .IgnoreUnmatchedProperties(), so an unknown YAML key (e.g. a misspelled
    // parameter in the closed parameters/constants.yaml) throws rather than binding to nothing.
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    /// <summary>
    /// Parses the pack files (keyed by in-tar relative path) and cross-checks the parsed
    /// <c>pack_id.pack_version</c> against the pin the engine requested.
    /// </summary>
    public static VerifiedPack Parse(IReadOnlyDictionary<string, byte[]> files, string expectedVersionKey)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrEmpty(expectedVersionKey);

        var manifest = ParseManifest(files, expectedVersionKey);

        // Version-key cross-check (ADR-PC-007 §P1): catches a registry mis-mapping (pin →
        // wrong digest → wrong content) before the wrong pack is ever cached.
        var actualVersionKey = $"{manifest.PackId}.{manifest.PackVersion}";
        if (!string.Equals(actualVersionKey, expectedVersionKey, StringComparison.Ordinal))
        {
            throw new PackLoadException(expectedVersionKey, null,
                $"version-key mismatch: the pin requested '{expectedVersionKey}' but the pulled pack is '{actualVersionKey}'.");
        }

        var dayCounts = ParseMap<DayCountDto, PackDayCount>(
            files, "primitives/day-count.yaml", expectedVersionKey,
            (id, dto) => new PackDayCount(Required(dto.FormulaRef, expectedVersionKey, $"day-count '{id}'.formula_ref")));

        var withholdings = ParseMap<WithholdingDto, PackWithholding>(
            files, "primitives/withholding.yaml", expectedVersionKey,
            (id, dto) => new PackWithholding(
                Required(dto.FormulaRef, expectedVersionKey, $"withholding '{id}'.formula_ref"),
                dto.RateBasisPoints,
                Required(dto.Basis, expectedVersionKey, $"withholding '{id}'.basis"),
                Required(dto.Timing, expectedVersionKey, $"withholding '{id}'.timing"),
                (dto.Exemptions ?? []).Select(e => new PackWithholdingExemption(
                    Required(e.Id, expectedVersionKey, $"withholding '{id}' exemption.id"),
                    Required(e.Evidence, expectedVersionKey, $"withholding '{id}' exemption.evidence"))).ToList(),
                (dto.Reporting ?? new()).ToDictionary(
                    kv => kv.Key,
                    kv => new PackReportingObligation(
                        kv.Value.Required,
                        Required(kv.Value.Frequency, expectedVersionKey, $"withholding '{id}' reporting '{kv.Key}'.frequency")))));

        var fgds = ParseMap<FgdDto, PackFgd>(
            files, "primitives/fgd.yaml", expectedVersionKey,
            (id, dto) => new PackFgd(dto.CoverageCeilingCents, Required(dto.Scheme, expectedVersionKey, $"fgd '{id}'.scheme")));

        var reportings = ParseMap<ReportingDto, PackReporting>(
            files, "primitives/reporting.yaml", expectedVersionKey,
            (id, dto) => new PackReporting(dto.Active,
                Required(dto.Frequency, expectedVersionKey, $"reporting '{id}'.frequency"),
                Required(dto.Regulator, expectedVersionKey, $"reporting '{id}'.regulator")));

        var paramsDto = Deserialize<ParametersDto>(files, "parameters/constants.yaml", expectedVersionKey);
        var parameters = new PackParameters(paramsDto.MaxConsumerRateBps, paramsDto.AutoRenewalOptoutWindowDays);

        // Rate-sheet-ref files are named by the manifest's rate_sheet_refs list (one file each).
        var rateSheetRefs = new List<PackRateSheetRef>();
        foreach (var refName in manifest.RateSheetRefNames)
        {
            var path = $"rate-sheet-refs/{refName}.yaml";
            var refsDto = Deserialize<RateSheetRefsFileDto>(files, path, expectedVersionKey);
            foreach (var r in refsDto.Refs ?? [])
            {
                if (r is null)
                {
                    throw new PackLoadException(expectedVersionKey, null, $"'{path}' has an empty ref entry.");
                }

                rateSheetRefs.Add(new PackRateSheetRef(
                    Required(r.ProductFamily, expectedVersionKey, $"{path} ref.product_family"),
                    Required(r.RateSheetVersionId, expectedVersionKey, $"{path} ref.rate_sheet_version_id")));
            }
        }

        return new VerifiedPack(manifest, dayCounts, withholdings, fgds, reportings, parameters, rateSheetRefs);
    }

    private static PackManifest ParseManifest(IReadOnlyDictionary<string, byte[]> files, string vk)
    {
        var dto = Deserialize<ManifestDto>(files, "pack.yaml", vk);
        return new PackManifest(
            PackId: Required(dto.PackId, vk, "pack.yaml pack_id"),
            PackVersion: Required(dto.PackVersion, vk, "pack.yaml pack_version"),
            Namespace: Required(dto.Namespace, vk, "pack.yaml namespace"),
            ManifestSchemaVersion: dto.ManifestSchemaVersion,
            Publisher: Required(dto.Publisher, vk, "pack.yaml publisher"),
            PackEffectiveFrom: ParseDate(dto.PackEffectiveFrom, vk, "pack.yaml pack_effective_from"),
            BasedOnPackVersion: dto.BasedOnPackVersion,
            DeltaSummary: Required(dto.DeltaSummary, vk, "pack.yaml delta_summary"),
            BreakingChanges: (dto.BreakingChanges ?? []).Select(b => new PackBreakingChange(
                Required(b.Id, vk, "breaking_change.id"), Required(b.Description, vk, "breaking_change.description"))).ToList(),
            EngineCompatibleVersions: Required(dto.Dependencies?.EngineCompatibleVersions, vk, "pack.yaml dependencies.engine_compatible_versions"),
            SchemaPins: dto.SchemaPins ?? new(),
            RateSheetRefNames: dto.RateSheetRefs ?? [],
            TestCorpusRef: Required(dto.TestCorpusRef, vk, "pack.yaml test_corpus_ref"));
    }

    private static IReadOnlyDictionary<string, TModel> ParseMap<TDto, TModel>(
        IReadOnlyDictionary<string, byte[]> files, string path, string vk, Func<string, TDto, TModel> map)
        where TDto : class
    {
        // Nullable value type: YamlDotNet binds an empty-bodied entry (e.g. "act_360:\n") to a
        // null value. Null-check before mapping so that fails loud as a PackLoadException
        // (naming the file + key) rather than escaping as an opaque NullReferenceException.
        var dtos = Deserialize<Dictionary<string, TDto?>>(files, path, vk);
        return dtos.ToDictionary(
            kv => kv.Key,
            kv => map(kv.Key, kv.Value ?? throw new PackLoadException(vk, null, $"'{path}' entry '{kv.Key}' has an empty body.")));
    }

    private static T Deserialize<T>(IReadOnlyDictionary<string, byte[]> files, string path, string vk)
    {
        if (!files.TryGetValue(path, out var bytes))
        {
            throw new PackLoadException(vk, null, $"pack is missing the required file '{path}'.");
        }

        try
        {
            var result = Deserializer.Deserialize<T>(Encoding.UTF8.GetString(bytes));
            return result ?? throw new PackLoadException(vk, null, $"file '{path}' deserialized to null (empty document?).");
        }
        catch (YamlException ex)
        {
            throw new PackLoadException(vk, null, $"failed to parse '{path}': {ex.Message}");
        }
    }

    private static string Required(string? value, string vk, string field) =>
        string.IsNullOrEmpty(value) ? throw new PackLoadException(vk, null, $"required field '{field}' is missing or empty.") : value;

    private static DateOnly ParseDate(string? value, string vk, string field)
    {
        var raw = Required(value, vk, field);
        return DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new PackLoadException(vk, null, $"field '{field}' value '{raw}' is not a YYYY-MM-DD date.");
    }

    // ---- DTOs: mutable shapes YamlDotNet binds to (underscored YAML → PascalCase). ----
    // Required fields are nullable and null-checked above, so a missing key fails loud rather
    // than silently constructing a record with a default.

    private sealed class ManifestDto
    {
        public string? PackId { get; set; }
        public string? PackVersion { get; set; }
        public string? Namespace { get; set; }
        public int ManifestSchemaVersion { get; set; }
        public string? Publisher { get; set; }
        public string? PackEffectiveFrom { get; set; }
        public string? BasedOnPackVersion { get; set; }
        public string? DeltaSummary { get; set; }
        public List<BreakingChangeDto>? BreakingChanges { get; set; }
        public DependenciesDto? Dependencies { get; set; }
        public Dictionary<string, string>? SchemaPins { get; set; }
        public List<string>? RateSheetRefs { get; set; }
        public string? TestCorpusRef { get; set; }
        public List<object>? PrimitiveOverlays { get; set; }
    }

    private sealed class DependenciesDto
    {
        public string? EngineCompatibleVersions { get; set; }
    }

    private sealed class BreakingChangeDto
    {
        public string? Id { get; set; }
        public string? Description { get; set; }
    }

    private sealed class DayCountDto
    {
        public string? FormulaRef { get; set; }
    }

    private sealed class WithholdingDto
    {
        public string? FormulaRef { get; set; }
        public int RateBasisPoints { get; set; }
        public string? Basis { get; set; }
        public string? Timing { get; set; }
        public List<ExemptionDto>? Exemptions { get; set; }
        public Dictionary<string, ReportingObligationDto>? Reporting { get; set; }
    }

    private sealed class ExemptionDto
    {
        public string? Id { get; set; }
        public string? Evidence { get; set; }
    }

    private sealed class ReportingObligationDto
    {
        public bool Required { get; set; }
        public string? Frequency { get; set; }
    }

    private sealed class FgdDto
    {
        public long CoverageCeilingCents { get; set; }
        public string? Scheme { get; set; }
    }

    private sealed class ReportingDto
    {
        public bool Active { get; set; }
        public string? Frequency { get; set; }
        public string? Regulator { get; set; }
    }

    private sealed class ParametersDto
    {
        public int MaxConsumerRateBps { get; set; }
        public int AutoRenewalOptoutWindowDays { get; set; }
    }

    private sealed class RateSheetRefsFileDto
    {
        public List<RateSheetRefDto?>? Refs { get; set; }
    }

    private sealed class RateSheetRefDto
    {
        public string? ProductFamily { get; set; }
        public string? RateSheetVersionId { get; set; }
    }
}
