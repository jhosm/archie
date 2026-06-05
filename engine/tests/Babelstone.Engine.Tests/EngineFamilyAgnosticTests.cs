using System.Text.RegularExpressions;
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
    /// The generic engine spine, by ADR-PC-021 §P2's enumeration. These eight projects MUST
    /// stay family-agnostic; a reference from any of them into <c>families/**</c> fails the build.
    /// <c>SpineProjects_match_the_ADR_PC_021_P2_list</c> keeps this allowlist in lockstep with
    /// the ADR off disk, so the gate cannot silently drift from the decision it enforces.
    /// </summary>
    private static readonly string[] SpineProjects =
    [
        "Babelstone.Engine",
        "Babelstone.EventStore",
        "Babelstone.RateSheets",
        "Babelstone.Packs",
        "Babelstone.FinancialMath",
        "Babelstone.FinancialTypes",
        "Babelstone.Engine.Avro",
        "Babelstone.OutboxPublisher",
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

    /// <summary>
    /// The <see cref="SpineProjects"/> allowlist must equal — exactly, as a set — the spine
    /// enumerated by ADR-PC-021 §P2 in prose. Parsed off the ADR file the same way the sibling
    /// test parses <c>.csproj</c> off disk, so the gate cannot silently drift from the decision:
    /// add a project to §P2 without adding it here (or vice versa) and this fails, naming the gap.
    /// </summary>
    [Fact]
    public void SpineProjects_match_the_ADR_PC_021_P2_list()
    {
        var adrSpine = SpineProjectsFromAdrP2(RepoRoot());

        Assert.True(
            adrSpine.SetEquals(SpineProjects),
            "ADR-PC-021 §P2: the SpineProjects allowlist has drifted from the §P2 prose enumeration. "
            + $"Only in the ADR: [{string.Join(", ", adrSpine.Except(SpineProjects).Order())}]. "
            + $"Only in the test: [{string.Join(", ", SpineProjects.Except(adrSpine).Order())}]. "
            + "Reconcile the allowlist and the ADR in the same change (the §D5 explicit-drift rule).");
    }

    /// <summary>
    /// The spine project names ADR-PC-021 §P2 enumerates, read off the ADR markdown. §P2 names the
    /// spine as the first backtick-wrapped list inside the parenthetical right after the phrase
    /// "generic engine spine"; we pull the <c>`Babelstone.*`</c> identifiers from that span. Robust
    /// to whitespace, line wrapping, and member order — it keys off the prose, not the layout.
    /// </summary>
    private static HashSet<string> SpineProjectsFromAdrP2(string repoRoot)
    {
        var adrPath = Path.Combine(
            repoRoot,
            "docs", "product-management", "product_concepts", "adrs",
            "ADR-PC-021-application-layer-family-owned-deciders.md");
        Assert.True(File.Exists(adrPath), $"ADR-PC-021 not found on disk: {adrPath}");

        var adr = File.ReadAllText(adrPath);

        // Isolate the parenthetical span that follows "generic engine spine" — the §P2
        // enumeration — so a `Babelstone.*` named elsewhere in the ADR can't leak in.
        var span = Regex.Match(adr, @"generic engine spine\s*\(([^)]*)\)", RegexOptions.Singleline);
        Assert.True(span.Success, $"ADR-PC-021 §P2 spine enumeration not found in {adrPath}");

        var names = Regex.Matches(span.Groups[1].Value, @"`(Babelstone\.[A-Za-z0-9.]+)`")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(names);
        return names;
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
