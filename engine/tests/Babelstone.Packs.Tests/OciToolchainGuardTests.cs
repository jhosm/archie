using Xunit;

namespace Babelstone.Packs.Tests;

/// <summary>
/// Pins the fail-fast OCI-toolchain preflight (bd 4ow6, ADR-PC-007 §P2/§P4). OCI pack mode shells
/// out to oras + cosign at load time; the chiseled runtime image omits both. The guard must fire
/// with a clear, named error when either is absent, and be a silent no-op when both resolve — so
/// disk mode (which never invokes the guard, and never needs the tools) is untouched.
/// </summary>
public sealed class OciToolchainGuardTests
{
    /// <summary>A PATH resolver standing in for a chiseled image: nothing resolves.</summary>
    private static string? NoneResolve(string tool) => null;

    /// <summary>A PATH resolver standing in for a dev/CI image: both tools resolve.</summary>
    private static string? AllResolve(string tool) => $"/usr/local/bin/{tool}";

    [Fact]
    public void Both_tools_present_is_a_no_op()
    {
        // The dev/CI/prod-with-toolchain case: oras + cosign on PATH ⇒ guard returns silently.
        OciToolchainGuard.EnsureToolsAvailable(AllResolve);
    }

    [Fact]
    public void Missing_both_tools_fails_loud_naming_both()
    {
        // The chiseled-image case: neither tool present ⇒ a clear, actionable PackLoadException.
        var ex = Assert.Throws<PackLoadException>(() => OciToolchainGuard.EnsureToolsAvailable(NoneResolve));

        Assert.Contains("oras", ex.Message);
        Assert.Contains("cosign", ex.Message);
        Assert.Contains("OCI pack mode", ex.Message);
        // Names the runtime-image cause so an operator knows the fix without reading ProcessRunner.
        Assert.Contains("chiseled runtime image", ex.Message);
    }

    [Fact]
    public void Missing_only_oras_fails_loud_naming_oras_not_cosign_as_missing()
    {
        // cosign present, oras absent ⇒ the message lists ONLY oras as missing.
        var ex = Assert.Throws<PackLoadException>(() =>
            OciToolchainGuard.EnsureToolsAvailable(tool => tool == "cosign" ? "/usr/local/bin/cosign" : null));

        Assert.Contains("missing: oras", ex.Message);
        Assert.DoesNotContain("missing: oras, cosign", ex.Message);
    }

    [Fact]
    public void Missing_only_cosign_fails_loud_naming_cosign()
    {
        // oras present, cosign absent ⇒ the message lists ONLY cosign as missing.
        var ex = Assert.Throws<PackLoadException>(() =>
            OciToolchainGuard.EnsureToolsAvailable(tool => tool == "oras" ? "/usr/local/bin/oras" : null));

        Assert.Contains("missing: cosign", ex.Message);
    }

    [Fact]
    public void A_tool_that_resolves_to_empty_string_counts_as_missing()
    {
        // Defensive: an empty resolution is treated identically to null (not found).
        Assert.Throws<PackLoadException>(() => OciToolchainGuard.EnsureToolsAvailable(_ => string.Empty));
    }
}
