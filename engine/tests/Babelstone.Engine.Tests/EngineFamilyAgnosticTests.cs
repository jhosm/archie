using System.Xml.Linq;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// ENGINE_FAMILY_AGNOSTIC (ADR-PC-021 §P2 / §D2): the generic engine spine carries no
/// <c>&lt;ProjectReference&gt;</c> to any <c>families/**</c> project — the <em>family → engine</em>
/// arrow is one-way. A stray reference from the spine to a family would silently erode
/// family-agnosticism, so this is the gateable invariant the ADR's Verifiable-commitments
/// row names. The check is a build-time / dependency assertion: it parses the spine
/// projects' <c>.csproj</c> off disk and fails if any references a family project.
/// </summary>
public sealed class EngineFamilyAgnosticTests
{
    /// <summary>
    /// The generic engine spine, by ADR-PC-021 §P2's enumeration. These six projects MUST
    /// stay family-agnostic; a reference from any of them into <c>families/**</c> fails the build.
    /// </summary>
    private static readonly string[] SpineProjects =
    [
        "Babelstone.Engine",
        "Babelstone.EventStore",
        "Babelstone.RateSheets",
        "Babelstone.Packs",
        "Babelstone.FinancialMath",
        "Babelstone.FinancialTypes",
    ];

    [Fact]
    public void No_spine_project_references_a_families_project()
    {
        var repoRoot = RepoRoot();
        var srcDir = Path.Combine(repoRoot, "engine", "src");
        var familiesDir = Path.GetFullPath(Path.Combine(repoRoot, "families")) + Path.DirectorySeparatorChar;

        var violations = new List<string>();

        foreach (var project in SpineProjects)
        {
            var csprojPath = Path.Combine(srcDir, project, project + ".csproj");
            Assert.True(File.Exists(csprojPath), $"spine project not found on disk: {csprojPath}");

            var projectDir = Path.GetDirectoryName(csprojPath)!;
            foreach (var include in ProjectReferenceIncludes(csprojPath))
            {
                // Resolve the (relative) Include against the .csproj's own directory, then
                // normalise — a reference lands "in families/**" iff its absolute path is
                // under the repo's families/ tree, regardless of the ../ shape used to reach it.
                var normalisedInclude = include.Replace('\\', Path.DirectorySeparatorChar);
                var resolved = Path.GetFullPath(Path.Combine(projectDir, normalisedInclude));
                if (resolved.StartsWith(familiesDir, StringComparison.Ordinal))
                {
                    violations.Add($"{project} → {include}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "ADR-PC-021 §P2/§D2: the generic engine spine must not reference any families/** project. "
            + "The family → engine arrow is one-way. Offending references:\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>Every <c>ProjectReference Include="…"</c> in a <c>.csproj</c>, namespace-agnostic.</summary>
    private static IEnumerable<string> ProjectReferenceIncludes(string csprojPath)
    {
        var doc = XDocument.Load(csprojPath);
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => include!);
    }

    /// <summary>
    /// Walks up from the test assembly's base directory to the repo root, identified by the
    /// committed solution at <c>engine/Babelstone.slnx</c> (same disk-marker pattern as
    /// <c>PackTestData.RepoRoot</c>).
    /// </summary>
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
