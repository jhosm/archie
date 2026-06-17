namespace Babelstone.Packs;

/// <summary>
/// A fail-fast preflight for OCI pack mode (ADR-PC-007 §P2/§P4). OCI mode shells out to the
/// <c>oras</c> and <c>cosign</c> CLIs at LOAD time (<see cref="OrasPackSource"/> /
/// <see cref="CosignPackVerifier"/> via <see cref="OciPackStore"/>). The chiseled
/// (distroless-style) runtime image the engine ships in (<c>aspnet:10.0-noble-chiseled-extra</c>,
/// engine/Dockerfile) deliberately omits both binaries — and even a shell — to shrink the base-OS
/// CVE surface the image-build grype scan flags (ADR-IC-014 §S2).
/// <para>
/// Without this guard, a process configured for OCI mode inside that image would only discover the
/// missing tool deep in the eager-load loop, surfacing as an opaque "could not start 'oras'"
/// <see cref="PackLoadException"/> from <c>ProcessRunner</c> with no hint that the runtime image is
/// the cause. This check runs ONCE, up front, when OCI mode is selected, and throws a clear,
/// actionable error naming the missing tool before any pack work begins.
/// </para>
/// <para>
/// Disk mode — the default for dev/CI/prod — never invokes oras/cosign and so must never call this
/// guard; it is completely unaffected.
/// </para>
/// </summary>
public static class OciToolchainGuard
{
    /// <summary>The CLIs OCI pack mode requires on PATH, in the order they are used (resolve → verify → pull).</summary>
    private static readonly string[] RequiredTools = ["oras", "cosign"];

    /// <summary>
    /// Throws a <see cref="PackLoadException"/> if either <c>oras</c> or <c>cosign</c> is absent from
    /// PATH. A no-op when both resolve. Call this exactly once, at the point OCI pack mode is selected,
    /// BEFORE constructing the OCI loader — never on the pure hot path.
    /// </summary>
    /// <param name="resolveOnPath">
    /// How a tool name is resolved to an executable path; injectable for tests. Defaults to a real
    /// PATH lookup. Returns the resolved path, or <c>null</c> if the tool is not found.
    /// </param>
    public static void EnsureToolsAvailable(Func<string, string?>? resolveOnPath = null)
    {
        var resolve = resolveOnPath ?? ResolveOnPath;

        var missing = new List<string>();
        foreach (var tool in RequiredTools)
        {
            if (string.IsNullOrEmpty(resolve(tool)))
            {
                missing.Add(tool);
            }
        }

        if (missing.Count == 0)
        {
            return;
        }

        // Name the offending tool(s) and the runtime-image cause so an operator knows the fix without
        // spelunking ProcessRunner. PackLoadException keeps the failure on the established fail-loud
        // channel (ADR-PC-007 §P4) the host already aborts non-zero on.
        throw new PackLoadException(null, null,
            $"OCI pack mode (Engine:PackRegistry=oci) requires the {string.Join(" and ", RequiredTools)} CLI(s) on PATH, " +
            $"but the following are missing: {string.Join(", ", missing)}. " +
            "The chiseled runtime image (aspnet:10.0-noble-chiseled-extra, engine/Dockerfile) deliberately omits both " +
            "to shrink the base-OS CVE surface (ADR-IC-014 §S2). Run OCI mode only in an image that bundles oras + cosign, " +
            "or use the default disk mode (Engine:PackRegistry=disk), which needs neither (ADR-PC-007 §P2/§P4).");
    }

    /// <summary>
    /// Resolves a bare tool name against PATH (and, on Windows, PATHEXT) the way the OS would when
    /// <see cref="System.Diagnostics.Process"/> launches it. Returns the full path, or <c>null</c> if
    /// no executable is found — the same outcome that would otherwise make <c>Process.Start</c> throw.
    /// </summary>
    private static string? ResolveOnPath(string tool)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathValue))
        {
            return null;
        }

        // On Windows a CLI is typically `tool.exe`; on Unix it is the bare name. Probe PATHEXT
        // candidates on Windows, and the bare name everywhere.
        var candidates = new List<string> { tool };
        if (OperatingSystem.IsWindows())
        {
            var pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM";
            foreach (var ext in pathExt.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                candidates.Add(tool + ext);
            }
        }

        foreach (var dir in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in candidates)
            {
                var full = Path.Combine(dir, candidate);
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }
}
