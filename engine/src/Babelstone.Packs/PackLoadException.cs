namespace Babelstone.Packs;

/// <summary>
/// A pack load failed and the engine must NOT proceed (ADR-PC-007 §P4 fail-loud). Covers a
/// missing pin, a failed/absent cosign signature, a failed oras pull, a YAML parse error, an
/// unknown closed-schema key, an unmappable primitive, or a version-key mismatch. There is no
/// silent fallback to a stale or bundled pack — a handler resolving against a half-loaded or
/// wrong pack would emit wrong money, so loading either fully succeeds or throws this.
/// </summary>
public sealed class PackLoadException(string? packVersion, string? digest, string message, Exception? inner = null)
    : Exception($"Pack load failed for '{packVersion ?? "?"}'{(digest is null ? "" : $"@{digest}")}: {message}", inner)
{
    /// <summary>The pack version pin (<c>pt.YYYY.N</c>) being loaded, when known.</summary>
    public string? PackVersion { get; } = packVersion;

    /// <summary>The OCI digest being pulled/verified, when known.</summary>
    public string? Digest { get; } = digest;
}
