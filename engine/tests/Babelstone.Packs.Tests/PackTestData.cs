namespace Babelstone.Packs.Tests;

/// <summary>
/// Loads the committed pt.2026.1 pack source files off disk (the parser reads exactly these —
/// schemas/ is added only in the built artefact, not needed for a structural parse), plus the
/// fakes that drive <see cref="OciPackStore"/>'s load flow without invoking oras/cosign.
/// </summary>
internal static class PackTestData
{
    // The DATA_FILES a structural parse needs (= pack.sh's list minus the sealed test-corpus,
    // which the loader does not parse).
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

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "packs", "pt.2026.1", "pack.yaml")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException($"repo root (containing packs/pt.2026.1/pack.yaml) not found from {AppContext.BaseDirectory}");
    }

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
}

/// <summary>An <see cref="IPackSource"/> that returns fixed files and counts pulls.</summary>
internal sealed class FakePackSource(IReadOnlyDictionary<string, byte[]> files) : IPackSource
{
    public int CallCount { get; private set; }

    public Task<IReadOnlyDictionary<string, byte[]>> PullByDigestAsync(string ociRef, string digest, CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(files);
    }
}

/// <summary>An <see cref="IPackSource"/> that simulates an oras pull failure.</summary>
internal sealed class ThrowingPackSource : IPackSource
{
    public Task<IReadOnlyDictionary<string, byte[]>> PullByDigestAsync(string ociRef, string digest, CancellationToken ct = default)
        => throw new PackLoadException(null, digest, "simulated oras pull failure");
}

/// <summary>An <see cref="IPackVerifier"/> that accepts and counts verifications.</summary>
internal sealed class FakePackVerifier : IPackVerifier
{
    public int CallCount { get; private set; }

    public Task VerifyAsync(string ociRef, string digest, CancellationToken ct = default)
    {
        CallCount++;
        return Task.CompletedTask;
    }
}

/// <summary>An <see cref="IPackVerifier"/> that simulates an unsigned / wrong-signer rejection.</summary>
internal sealed class ThrowingPackVerifier : IPackVerifier
{
    public Task VerifyAsync(string ociRef, string digest, CancellationToken ct = default)
        => throw new PackLoadException(null, digest, "simulated cosign verify failure (unsigned)");
}
