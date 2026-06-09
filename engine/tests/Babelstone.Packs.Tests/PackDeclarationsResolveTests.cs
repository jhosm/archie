using Xunit;

namespace Babelstone.Packs.Tests;

/// <summary>
/// Fences a whole bug class: a pack must never declare a primitive <c>formula_ref</c> the engine
/// cannot resolve. Such a dead entry passes <c>make pack-validate</c> (CUE checks the string shape,
/// not engine implementation — ADR-PC-006) yet throws only at engine load (ADR-PC-010,
/// VerifiedPack/PackDayCount.ToConvention() is the resolver). This test discovers every shipped
/// pack under <c>packs/</c> and asserts every declared day-count formula_ref resolves, so a future
/// dead entry fails CI here instead of lurking until load.
/// </summary>
public sealed class PackDeclarationsResolveTests
{
    /// <summary>Every <c>packs/&lt;id&gt;/</c> dir that carries a <c>pack.yaml</c> (a shipped pack source).</summary>
    public static TheoryData<string> ShippedPacks()
    {
        var packsRoot = Path.Combine(PackTestData.RepoRoot(), "packs");
        var data = new TheoryData<string>();
        foreach (var dir in Directory.EnumerateDirectories(packsRoot))
        {
            if (File.Exists(Path.Combine(dir, "pack.yaml")))
            {
                data.Add(Path.GetFileName(dir));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ShippedPacks))]
    public void Every_declared_day_count_formula_ref_resolves_in_the_engine(string packDirName)
    {
        var pack = ParsePackFromDisk(packDirName);

        Assert.NotEmpty(pack.DayCounts); // a pack with no day-counts would vacuously pass — guard that too.
        foreach (var (id, dayCount) in pack.DayCounts)
        {
            // ToConvention() throws PackLoadException for a formula_ref the engine does not implement.
            // If this throws, the pack declares a dead primitive that would fail at engine load.
            var ex = Record.Exception(() => dayCount.ToConvention());
            Assert.True(
                ex is null,
                $"pack '{pack.VersionKey}' day-count '{id}' has unresolvable formula_ref " +
                $"'{dayCount.FormulaRef}' — the engine does not implement it ({ex?.Message}).");
        }
    }

    /// <summary>
    /// Parses a shipped pack from its on-disk source files (the DATA_FILES a structural parse needs —
    /// same set <see cref="PackTestData"/> uses for pt.2026.1), keyed by the directory's version key.
    /// </summary>
    private static VerifiedPack ParsePackFromDisk(string packDirName)
    {
        var packDir = Path.Combine(PackTestData.RepoRoot(), "packs", packDirName);
        string[] fixedPaths =
        [
            "pack.yaml",
            "primitives/day-count.yaml",
            "primitives/withholding.yaml",
            "primitives/fgd.yaml",
            "primitives/reporting.yaml",
            "parameters/constants.yaml",
        ];

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var relativePath in fixedPaths)
        {
            var diskPath = Path.Combine(packDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            files[relativePath] = File.ReadAllBytes(diskPath);
        }

        // Rate-sheet-ref filenames are pack-specific (e.g. deposits-pt.yaml) — discover them.
        var rateSheetRefDir = Path.Combine(packDir, "rate-sheet-refs");
        foreach (var diskPath in Directory.EnumerateFiles(rateSheetRefDir, "*.yaml"))
        {
            files[$"rate-sheet-refs/{Path.GetFileName(diskPath)}"] = File.ReadAllBytes(diskPath);
        }

        return PackParser.Parse(files, packDirName);
    }
}
