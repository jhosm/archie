using System.Diagnostics;
using Babelstone.FinancialMath;
using Xunit;

namespace Babelstone.Packs.Tests;

/// <summary>
/// End-to-end pull-by-digest with the REAL oras (offline OCI layout — no registry, no network),
/// gated out of the default lane. Builds the pt.2026.1 artefact with packs/pack.sh (cue-vet +
/// oras push to an OCI layout), captures its digest, then exercises <see cref="OrasPackSource"/>
/// pulling by that digest and parsing the result. The cosign signature path needs a registry +
/// OIDC and rides the broader integration lane (E.6 / Q.5), not this offline test.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OciPackStoreIntegrationTests
{
    [Fact]
    public async Task Builds_then_pulls_pt2026_by_digest_from_an_oci_layout_and_parses()
    {
        var root = PackTestData.RepoRoot();
        var layout = Path.Combine(Path.GetTempPath(), $"babelstone-pt2026-layout-{Guid.NewGuid():N}");
        try
        {
            // pack.sh build: validate (cue vet) + oras push to an offline OCI layout; prints the digest.
            var build = await RunAsync(
                "bash",
                [Path.Combine(root, "packs", "pack.sh"), "build", Path.Combine(root, "packs", "pt.2026.1"), "--layout", layout],
                root);
            Assert.True(build.ExitCode == 0, $"pack.sh build failed (exit {build.ExitCode}):\n{build.StdErr}");

            var digest = build.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1].Trim();
            Assert.StartsWith("sha256:", digest);

            // Pull by that digest via the real oras, fully offline, and parse.
            var source = new OrasPackSource(useOciLayout: true);
            var files = await source.PullByDigestAsync(layout, digest);
            var pack = PackParser.Parse(files, "pt.2026.1");

            Assert.Equal("pt.2026.1", pack.VersionKey);
            Assert.Equal(DayCountConvention.Act360, pack.ResolveDayCount("act_360"));
            Assert.Equal(2800, pack.Withholdings["irs_juros"].RateBasisPoints);
        }
        finally
        {
            if (Directory.Exists(layout))
            {
                Directory.Delete(layout, recursive: true);
            }
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdOut, await stdErr);
    }
}
