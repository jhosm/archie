namespace Babelstone.Packs;

/// <summary>
/// Pulls a pack's files by OCI digest (ADR-PC-007 — by digest, never by tag). The real
/// implementation (<see cref="OrasPackSource"/>) shells out to <c>oras</c> at LOAD time; tests
/// inject a fake. Returns the pack's data files keyed by their in-tar relative path
/// (e.g. <c>pack.yaml</c>, <c>primitives/day-count.yaml</c>).
/// </summary>
public interface IPackSource
{
    Task<IReadOnlyDictionary<string, byte[]>> PullByDigestAsync(string ociRef, string digest, CancellationToken ct = default);
}

/// <summary>
/// Verifies a pack's cosign signature (ADR-PC-007). The real implementation
/// (<see cref="CosignPackVerifier"/>) shells out to <c>cosign</c> at LOAD time and throws
/// <see cref="PackLoadException"/> on an unsigned, wrong-signer, or otherwise unverifiable
/// artefact. A verified signature IS the attestation that the pack's CUE depths 1–4 passed in
/// CI (ADR-PC-006), so the loader does not re-run <c>cue vet</c>.
/// </summary>
public interface IPackVerifier
{
    Task VerifyAsync(string ociRef, string digest, CancellationToken ct = default);
}

/// <summary>A resolved pack pin: the OCI reference, the image digest, and the signature digest (ADR-PC-007).</summary>
public sealed record PackRef(string OciRef, string Digest, string SignatureDigest);

/// <summary>
/// Resolves a pack version pin (<c>pt.YYYY.N</c>) to its immutable OCI digest + signature
/// digest — the <c>pack_versions</c> registry of ADR-PC-007. A missing row is a fail-loud
/// startup error (the store turns a null into a <see cref="PackLoadException"/>).
/// </summary>
/// <remarks>
/// <see cref="InMemoryPackVersionRegistry"/> is the configuration-backed implementation used
/// by the walking skeleton and tests.
/// </remarks>
public interface IPackVersionRegistry
{
    Task<PackRef?> ResolveAsync(string packVersion, CancellationToken ct = default);
}

/// <summary>
/// The in-engine pack loader/verifier (ADR-PC-007). <see cref="GetAsync"/> does the
/// out-of-process load-time work (resolve → cosign verify → pull-by-digest → parse → cache),
/// fail-loud throughout; <see cref="Resolve"/> is the pure in-memory cache read a handler uses
/// on the hot path — it can never trigger oras/cosign or any I/O.
/// </summary>
public interface IPackStore
{
    /// <summary>Loads and caches a pack version (load-time, impure). Throws <see cref="PackLoadException"/> on any failure — never returns a partial/wrong pack.</summary>
    Task<VerifiedPack> GetAsync(string packVersion, CancellationToken ct = default);

    /// <summary>Returns an already-loaded pack from the immutable cache (pure, hot path). Throws if it was not pre-loaded — a handler must never cause a pull.</summary>
    VerifiedPack Resolve(string packVersion);
}

/// <summary>
/// A configuration-backed <see cref="IPackVersionRegistry"/>: the engine is configured with the
/// pinned <c>pt.YYYY.N → (OCI ref, digest, signature digest)</c> mapping. Immutable once constructed.
/// </summary>
public sealed class InMemoryPackVersionRegistry(IReadOnlyDictionary<string, PackRef> refs) : IPackVersionRegistry
{
    public Task<PackRef?> ResolveAsync(string packVersion, CancellationToken ct = default)
        => Task.FromResult(refs.TryGetValue(packVersion, out var packRef) ? packRef : null);
}
