using System.Xml.Linq;
using Xunit;

namespace Babelstone.Lifecycle.Tests;

/// <summary>
/// LIFECYCLE_FAMILY_AGNOSTIC (ADR-PC-036; ADR-IC-019 family → core arrow): the
/// lifecycle-command driver CORE (<c>Babelstone.Lifecycle</c>) carries no <c>&lt;ProjectReference&gt;</c> to any
/// <c>families/**</c> project. The per-family rules + their Npgsql read-model stores live in the
/// <c>Babelstone.Families.*.Lifecycle</c> contributions the HOST discovers by assembly-scan and composes; the
/// core names only the family-agnostic ports (<c>IFamilyLifecycleModule</c> / <c>ILifecycleCommandRule</c>). A
/// stray families/** reference would re-introduce exactly the coupling this refactor removed — the core knowing
/// that a deposit "matures" or a loan owes an "installment" — so this is the gateable invariant, the
/// lifecycle-estate cousin of <c>NOTIFICATION_FAMILY_AGNOSTIC</c> (ADR-IC-019) and <c>ENGINE_FAMILY_AGNOSTIC</c>
/// (ADR-PC-021). It parses the core <c>.csproj</c> off disk — the same build-time dependency assertion shape
/// those gates use.
/// </summary>
/// <remarks>
/// Unlike the notification core (which reaches the engine ONLY over the ADR-PC-027 HTTP contract, so its gate
/// also forbids any engine reference), the lifecycle driver is downstream and MAY name the engine hosting seam
/// <c>Babelstone.Engine.Hosting</c> at compile time for the canonical <c>LifecycleCommandKey</c> (ADR-PC-036
///, LCD-1) — that is not a family and not a spine project. So this gate checks the one invariant
/// the refactor restores: NO <c>families/**</c> reference from the core.
/// </remarks>
public sealed class LifecycleFamilyAgnosticTests
{
    [Fact]
    public void Lifecycle_driver_core_references_no_family_project()
    {
        var repoRoot = RepoRoot();
        var familiesDir = Path.GetFullPath(Path.Combine(repoRoot, "families")) + Path.DirectorySeparatorChar;

        var csprojPath = Path.Combine(
            repoRoot, "lifecycle-driver", "src", "Babelstone.Lifecycle", "Babelstone.Lifecycle.csproj");
        Assert.True(File.Exists(csprojPath), $"lifecycle driver core project not found on disk: {csprojPath}");

        var projectDir = Path.GetDirectoryName(csprojPath)!;
        var violations = new List<string>();
        foreach (var include in ProjectReferenceIncludes(csprojPath))
        {
            // Resolve the (relative) Include against the .csproj's own directory, then normalise — a reference
            // lands "in families/**" iff its absolute path is under that tree, regardless of the ../ shape.
            var normalisedInclude = include.Replace('\\', Path.DirectorySeparatorChar);
            var resolved = Path.GetFullPath(Path.Combine(projectDir, normalisedInclude));

            if (resolved.StartsWith(familiesDir, StringComparison.Ordinal))
            {
                violations.Add($"Babelstone.Lifecycle → {include}  (families/**)");
            }
        }

        Assert.True(
            violations.Count == 0,
            "ADR-PC-036 + ADR-IC-019: the lifecycle-command driver core must reference no "
            + "families/** project — the family → core arrow is one-way. A family's lifecycle rule + read-model "
            + "store lives in its Babelstone.Families.*.Lifecycle contribution, discovered by the host's "
            + "assembly-scan, not wired into the core. Offending references:\n  "
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
    /// Walks up from the test assembly's base directory to the repo root, identified by the committed solution
    /// at <c>engine/Babelstone.slnx</c> (the same disk-marker pattern the engine/notification family-agnostic
    /// gates use — worktree-safe, no <c>.git</c> dependency).
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
