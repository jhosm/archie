namespace Babelstone.EventStore.Tests;

/// <summary>
/// Loads the committed <c>pt.2026.1</c> pack source files off disk — the data files a structural
/// parse needs (the same list <c>HostPack</c> / pack.sh use, minus the sealed test-corpus the
/// loader does not parse). Used to drive <c>OciPackStore</c>'s load flow over the durable
/// <c>PostgresPackVersionRegistry</c> in the §P4 fail-loud tests without invoking oras/cosign.
/// </summary>
internal static class PackFixtures
{
    private static readonly string[] RelativePaths =
    [
        "pack.yaml",
        "primitives/day-count.yaml",
        "primitives/withholding.yaml",
        "primitives/fgd.yaml",
        "primitives/reporting.yaml",
        "parameters/constants.yaml",
        "families.yaml",
        "rate-sheet-refs/deposits-pt.yaml",
        "rate-sheet-refs/current-account-pt.yaml",
        "rate-sheet-refs/loans-pt.yaml",
    ];

    public static Dictionary<string, byte[]> LoadPt2026()
    {
        var packDir = Path.Combine(RepoRoot(), "packs", "pt.2026.1");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var relativePath in RelativePaths)
        {
            var diskPath = Path.Combine(packDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            files[relativePath] = File.ReadAllBytes(diskPath);
        }

        return files;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "packs", "pt.2026.1", "pack.yaml")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"repo root (containing packs/pt.2026.1/pack.yaml) not found from {AppContext.BaseDirectory}");
    }
}
