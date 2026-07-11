using System.Collections.Concurrent;
using Babelstone.Packs;

namespace Babelstone.RateSheets.Api;

/// <summary>
/// A disk-backed <see cref="IPackStore"/> for the deploy host — the walking-skeleton stand-in for
/// the OCI loader (C.5, <see cref="OciPackStore"/>) on the dev boundary, mirroring the engine
/// host's <c>HostPack</c>. It structurally parses each configured pack version off the on-disk
/// <c>packs/</c> tree (the same <c>PackParser.Parse</c> the OCI loader runs after pulling by digest)
/// and caches it immutably, so the deploy's ADR-PC-008 bound comes from the pack's own
/// <c>parameters/constants.yaml</c> rather than a host config knob.
/// </summary>
/// <remarks>
/// The bound is the verified pack's: in production this seam is the cosign-verifying
/// <see cref="OciPackStore"/>; here it is disk-backed because the deploy host runs on the same
/// walking-skeleton boundary as the engine host (no live OCI registry in dev/CI). Both honour the
/// load-time/hot-path split — <see cref="GetAsync"/> parses, <see cref="Resolve"/> is the pure
/// cache read the deploy handler uses and throws <see cref="PackLoadException"/> for a version that
/// was never loaded, exactly like <see cref="OciPackStore"/>.
/// </remarks>
public sealed class HostPackStore : IPackStore
{
    private static readonly string[] DataFiles =
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

    private readonly string _packsDir;
    private readonly ConcurrentDictionary<string, VerifiedPack> _cache = new(StringComparer.Ordinal);

    public HostPackStore(string? packsDir)
        => _packsDir = packsDir ?? FindPacksDir();

    public Task<VerifiedPack> GetAsync(string packVersion, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packVersion);
        var pack = _cache.GetOrAdd(packVersion, Load);
        return Task.FromResult(pack);
    }

    public VerifiedPack Resolve(string packVersion)
        => _cache.TryGetValue(packVersion, out var pack)
            ? pack
            : throw new PackLoadException(packVersion, null,
                "pack version was not pre-loaded — resolve happens on the pure hot path and must never read disk. " +
                "Call GetAsync at startup for every pack version the deploy host may receive.");

    private VerifiedPack Load(string packVersion)
    {
        var packDir = Path.Combine(_packsDir, packVersion);
        if (!Directory.Exists(packDir))
        {
            throw new PackLoadException(packVersion, null,
                $"pack version '{packVersion}' has no directory under '{_packsDir}' — unknown or unpinned pack.");
        }

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var relativePath in DataFiles)
        {
            var diskPath = Path.Combine(packDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            files[relativePath] = File.ReadAllBytes(diskPath);
        }

        return PackParser.Parse(files, packVersion);
    }

    private static string FindPacksDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "packs")))
        {
            dir = dir.Parent;
        }

        return dir is not null
            ? Path.Combine(dir.FullName, "packs")
            : throw new InvalidOperationException(
                $"packs/ directory not found from {AppContext.BaseDirectory}; set RateSheets:PacksDir.");
    }
}
