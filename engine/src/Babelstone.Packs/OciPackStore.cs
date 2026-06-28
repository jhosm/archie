using System.Collections.Concurrent;

namespace Babelstone.Packs;

/// <summary>
/// The in-engine pack loader/verifier (ADR-PC-007). On <see cref="GetAsync"/> it
/// resolves a pin to its digest, cosign-verifies, pulls by digest, structurally parses, and
/// caches — all out-of-process work happening once at LOAD time. <see cref="Resolve"/> is the
/// pure cache read a handler uses on the hot path. Every failure is a <see cref="PackLoadException"/>;
/// there is no silent fallback to a stale or bundled pack.
/// </summary>
public sealed class OciPackStore(
    IPackVersionRegistry registry,
    IPackVerifier verifier,
    IPackSource source) : IPackStore
{
    // Immutable once populated: a pin is content-addressed (digest) and never re-resolves
    // mid-life (ADR-PC-009), so an entry never legitimately changes. No as-of dimension.
    private readonly ConcurrentDictionary<string, VerifiedPack> _cache = new(StringComparer.Ordinal);

    // Per-pin gate so concurrent first-sight loads pull + verify exactly once.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _loadGates = new(StringComparer.Ordinal);

    public VerifiedPack Resolve(string packVersion)
        => _cache.TryGetValue(packVersion, out var pack)
            ? pack
            : throw new PackLoadException(packVersion, null,
                "pack version was not pre-loaded — resolve happens on the pure hot path and must never trigger a pull. " +
                "Call GetAsync at load time for every pack a live instance references.");

    public async Task<VerifiedPack> GetAsync(string packVersion, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packVersion);
        if (_cache.TryGetValue(packVersion, out var cached))
        {
            return cached;
        }

        var gate = _loadGates.GetOrAdd(packVersion, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(packVersion, out cached))
            {
                return cached; // another caller loaded it while we waited
            }

            // 1. Resolve the pin → digest (ADR-PC-007). A missing entry is fail-loud, never a skip.
            var packRef = await registry.ResolveAsync(packVersion, ct)
                ?? throw new PackLoadException(packVersion, null,
                    "no pack_versions registry entry — the pack version is unknown or unpinned.");

            // 2. cosign verify BEFORE trusting any content (ADR-PC-007). Throws on unsigned/wrong-signer.
            await verifier.VerifyAsync(packRef.OciRef, packRef.Digest, ct);

            // 3. Pull by digest (ADR-PC-007). Throws on pull failure.
            var files = await source.PullByDigestAsync(packRef.OciRef, packRef.Digest, ct);

            // 4. Structural parse + version-key cross-check (ADR-PC-007). Throws on any inconsistency.
            var pack = PackParser.Parse(files, packVersion);

            // 5. Cache immutably.
            _cache[packVersion] = pack;
            return pack;
        }
        finally
        {
            gate.Release();
        }
    }
}
