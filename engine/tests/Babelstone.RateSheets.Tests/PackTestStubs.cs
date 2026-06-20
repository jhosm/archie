using Babelstone.Packs;

namespace Babelstone.RateSheets.Tests;

/// <summary>
/// Minimal <see cref="VerifiedPack"/> fixtures for the rate-sheet deploy tests: the deploy path
/// only reads <c>Parameters.MaxConsumerRateBps</c> for the §P2 bound, so the rest of the pack is
/// stubbed to empty. A registry-backed pack is exercised against the real on-disk tree by
/// <c>HostPackStore</c>; this stub keeps the bound deterministic without parsing YAML.
/// </summary>
internal static class PackTestStubs
{
    public static VerifiedPack WithMaxConsumerRateBps(string versionKey, int maxConsumerRateBps)
    {
        // versionKey is "<pack_id>.<pack_version>" (e.g. "pt.2026.1"); split off the last segment
        // so VersionKey round-trips, matching how the loader keys the cache.
        var lastDot = versionKey.LastIndexOf('.');
        var packId = lastDot < 0 ? versionKey : versionKey[..lastDot];
        var packVersion = lastDot < 0 ? versionKey : versionKey[(lastDot + 1)..];

        var manifest = new PackManifest(
            PackId: packId,
            PackVersion: packVersion,
            Namespace: "pt",
            ManifestSchemaVersion: 1,
            Publisher: "test",
            PackEffectiveFrom: new DateOnly(2026, 1, 1),
            BasedOnPackVersion: null,
            DeltaSummary: "stub",
            BreakingChanges: [],
            EngineCompatibleVersions: ">=0.0.0",
            SchemaPins: new Dictionary<string, string>(),
            RateSheetRefNames: [],
            TestCorpusRef: "stub");

        return new VerifiedPack(
            manifest,
            DayCounts: new Dictionary<string, PackDayCount>(),
            Withholdings: new Dictionary<string, PackWithholding>(),
            Fgds: new Dictionary<string, PackFgd>(),
            Reportings: new Dictionary<string, PackReporting>(),
            Parameters: new PackParameters(maxConsumerRateBps, AutoRenewalOptoutWindowDays: 14),
            RateSheetRefs: [],
            Families: []);
    }
}
