using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace Babelstone.Composition.Tests;

/// <summary>
/// The BUILD-TIME layer of <c>FAMILY_TO_CORE_DEFAULT_DENY</c> (ADR-PC-040 candidate C):
/// <c>composition/msbuild/FamilyToCoreDefaultDeny.targets</c>, imported repo-wide by the root
/// <c>Directory.Build.targets</c>, fails a project's own <c>dotnet build</c> when a project with
/// no (or a <c>Core</c>) role references <c>families/**</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this adds and what it does not.</b> The guard adds failure locality on top of the
/// authoritative sibling CI gates (<see cref="FamilyAgnosticDefaultDenyTests"/> / catalogue row
/// XC-1 and <see cref="CompositionRootFamilyAgnosticTests"/> / row XC-2). If the layers ever
/// disagree, the gate wins — see the guard's own header for the full story.
/// </para>
/// <para>
/// <b>How it is proven.</b> Two ways, mirroring how the sibling gates prove theirs (parse the
/// artefact off disk) plus the one thing a disk parse cannot show — that the build actually
/// refuses: (1) a wiring test asserts the root <c>Directory.Build.targets</c> imports the guard
/// and that no subtree-level <c>Directory.Build.targets</c> shadows it (MSBuild stops at the
/// NEAREST one walking up, so a subtree adding its own would silently disconnect the guard);
/// (2) throwaway fixture projects in a temp directory — a fake <c>families/</c> tree pointed at
/// via the guard's <c>$(BabelstoneFamiliesDirectory)</c> test seam, so no real family is
/// referenced or restored — prove the negative (an unmarked / <c>Core</c>-marked project
/// referencing a family FAILS with <c>BBS0040</c>) and the positive (the same project marked
/// <c>CompositionRoot</c> builds green, so the deny really is role-keyed).
/// </para>
/// </remarks>
public sealed class FamilyToCoreBuildGuardTests
{
    private const string GuardRelativePath = "composition/msbuild/FamilyToCoreDefaultDeny.targets";
    private const string GuardErrorCode = "BBS0040";

    [Fact]
    public void Root_directory_build_targets_imports_the_guard_and_no_subtree_shadows_it()
    {
        var repoRoot = RepoRoot();
        var rootTargets = Path.Combine(repoRoot, "Directory.Build.targets");
        var guardPath = Path.Combine(repoRoot, GuardRelativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(
            File.Exists(guardPath),
            $"ADR-PC-040 candidate C: the shared build guard '{GuardRelativePath}' is missing.");
        Assert.True(
            File.Exists(rootTargets),
            "ADR-PC-040 candidate C: the repo-root Directory.Build.targets (the one-import shim that "
            + "hands the build guard to every project) is missing — without it the guard applies to "
            + "nothing.");

        Assert.True(
            ImportsGuard(rootTargets),
            $"ADR-PC-040 candidate C: the repo-root Directory.Build.targets does not import "
            + $"'{GuardRelativePath}' — the build-time family→core guard is disconnected.");

        // Shadowing drift: MSBuild uses the NEAREST Directory.Build.targets walking up from each
        // project, so a subtree-level file silently replaces the root one for that whole subtree.
        // Any nested Directory.Build.targets must therefore re-import the guard (directly or via
        // the root file) to keep the covenant's build layer total.
        var shadowing = new List<string>();
        foreach (var candidate in Directory.EnumerateFiles(repoRoot, "Directory.Build.targets", SearchOption.AllDirectories))
        {
            if (string.Equals(candidate, rootTargets, StringComparison.Ordinal)
                || HasSegment(candidate, "bin") || HasSegment(candidate, "obj")
                || HasSegment(candidate, ".git") || HasSegment(candidate, "node_modules"))
            {
                continue;
            }

            if (!ImportsGuard(candidate))
            {
                shadowing.Add(Path.GetRelativePath(repoRoot, candidate));
            }
        }

        Assert.True(
            shadowing.Count == 0,
            "ADR-PC-040 candidate C: subtree-level Directory.Build.targets file(s) shadow the repo-root "
            + "one WITHOUT importing the family→core build guard, silently disconnecting it for their "
            + $"subtree — each must <Import> '{GuardRelativePath}' (or the root Directory.Build.targets):\n  "
            + string.Join("\n  ", shadowing));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Core")]
    public void Project_with_no_or_core_role_referencing_a_family_fails_to_build(string? role)
    {
        var (exitCode, output) = BuildFixture(role);

        Assert.True(
            exitCode != 0,
            $"ADR-PC-040 candidate C: a fixture project with BabelstoneRole='{role ?? "(absent)"}' "
            + $"referencing a families/** project should FAIL to build, but the build succeeded.\n{output}");
        Assert.True(
            output.Contains(GuardErrorCode, StringComparison.Ordinal),
            $"ADR-PC-040 candidate C: the failing build should carry the guard's error code "
            + $"'{GuardErrorCode}' (so the failure is the covenant, not an accident).\n{output}");
    }

    [Fact]
    public void Composition_root_marked_project_referencing_a_family_builds()
    {
        var (exitCode, output) = BuildFixture("CompositionRoot");

        Assert.True(
            exitCode == 0,
            "ADR-PC-040 candidate C: the SAME fixture marked <BabelstoneRole>CompositionRoot</BabelstoneRole> "
            + "must build green (the explicit opt-out — and the control proving the deny is role-keyed, "
            + $"not an artefact of the fixture).\n{output}");
    }

    /// <summary>
    /// Builds a throwaway fixture: a fake family project under <c>families/</c> and a core project
    /// referencing it, importing the REAL guard file with <c>$(BabelstoneFamiliesDirectory)</c>
    /// pointed at the fixture's own families tree (the guard's documented test seam). Empty
    /// <c>Directory.Build.props/targets</c> at the fixture root keep MSBuild's upward search from
    /// leaking anything in — the fixture exercises exactly one input: the guard.
    /// </summary>
    private static (int ExitCode, string Output) BuildFixture(string? role)
    {
        var repoRoot = RepoRoot();
        var guardPath = Path.Combine(repoRoot, GuardRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var fixtureRoot = Directory.CreateTempSubdirectory("bbs-family-guard-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(fixtureRoot, "Directory.Build.props"), "<Project />\n");
            File.WriteAllText(Path.Combine(fixtureRoot, "Directory.Build.targets"), "<Project />\n");

            var familyDir = Directory.CreateDirectory(Path.Combine(fixtureRoot, "families", "Fake.Family")).FullName;
            File.WriteAllText(
                Path.Combine(familyDir, "Fake.Family.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            var roleProperty = role is null ? "" : $"<BabelstoneRole>{role}</BabelstoneRole>";
            var coreDir = Directory.CreateDirectory(Path.Combine(fixtureRoot, "core", "Fixture.Core")).FullName;
            File.WriteAllText(
                Path.Combine(coreDir, "Fixture.Core.csproj"),
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    {roleProperty}
                    <!-- The guard's test seam: classify against the FIXTURE's families tree, so the
                         violating reference below needs no real family (nothing to restore/build). -->
                    <BabelstoneFamiliesDirectory>$([MSBuild]::NormalizeDirectory('$(MSBuildProjectDirectory)', '..', '..', 'families'))</BabelstoneFamiliesDirectory>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../../families/Fake.Family/Fake.Family.csproj" />
                  </ItemGroup>
                  <Import Project="{guardPath}" />
                </Project>
                """);

            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                WorkingDirectory = coreDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("build");
            startInfo.ArgumentList.Add("Fixture.Core.csproj");
            startInfo.ArgumentList.Add("-nologo");
            startInfo.ArgumentList.Add("-v:m");
            startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            startInfo.Environment["DOTNET_NOLOGO"] = "1";

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("failed to start 'dotnet build' for the fixture");
            var output = new StringBuilder();
            output.Append(process.StandardOutput.ReadToEnd());
            output.Append(process.StandardError.ReadToEnd());
            if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException($"fixture 'dotnet build' timed out.\n{output}");
            }

            return (process.ExitCode, output.ToString());
        }
        finally
        {
            try
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup of the temp fixture; never fail the assertion on it.
            }
        }
    }

    /// <summary>True iff the given MSBuild file has an <c>&lt;Import&gt;</c> whose Project path
    /// names the shared guard file (directly, or via the root <c>Directory.Build.targets</c> that
    /// imports it).</summary>
    private static bool ImportsGuard(string msbuildFilePath)
    {
        var doc = XDocument.Load(msbuildFilePath);
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "Import")
            .Select(e => (string?)e.Attribute("Project"))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Replace('\\', '/'))
            .Any(p => p.EndsWith(GuardRelativePath, StringComparison.Ordinal)
                      || p.EndsWith("/Directory.Build.targets", StringComparison.Ordinal));
    }

    private static bool HasSegment(string path, string segment)
    {
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains(segment, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Walks up from the test assembly's base directory to the repo root, identified by
    /// the committed solution at <c>engine/Babelstone.slnx</c> (the same disk-marker pattern the
    /// sibling gates use — worktree-safe, no <c>.git</c> dependency).</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "engine", "Babelstone.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"repo root (containing engine/Babelstone.slnx) not found from {AppContext.BaseDirectory}");
    }
}
