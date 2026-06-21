using System.Xml.Linq;
using Xunit;

namespace Babelstone.Notification.Tests;

/// <summary>
/// NOTIFICATION_FAMILY_AGNOSTIC (ADR-IC-019 §D2/§P2): the notification core carries no
/// <c>&lt;ProjectReference&gt;</c> to any <c>families/**</c> project, and none to an engine-spine
/// project — the <em>family → core</em> arrow is one-way, and the core reaches the engine only over
/// the storage-opaque ADR-PC-027 read contract (GET /v1/deposits/{id}), never a compile-time kernel
/// reference. A stray reference would silently erode family-agnosticism (and re-couple the storage
/// tier ADR-PC-027 hides), so this is the gateable invariant ADR-IC-019's Verifiable-commitments row
/// (catalogue NOTIF-1) names. The notification-estate cousin of <c>ENGINE_FAMILY_AGNOSTIC</c>
/// (ADR-PC-021) and <c>ORCHESTRATOR_FAMILY_AGNOSTIC</c> (ADR-IC-018). It parses the notification
/// core <c>.csproj</c> off disk — the same build-time dependency assertion shape those gates use.
/// </summary>
public sealed class NotificationFamilyAgnosticTests
{
    /// <summary>
    /// The notification core projects (the §P1 core set) that MUST stay family-agnostic. The host
    /// composition root would be the standing ADR-PC-021 §A2 exemption if it were a separate project;
    /// today the single core project IS family-agnostic by construction (it reads over the contract),
    /// so it is checked directly.
    /// </summary>
    private static readonly string[] CoreProjects =
    [
        "Babelstone.Notification",
    ];

    /// <summary>
    /// The ADR-PC-021 §P2 engine spine — the notification core must reference none of these (it reads
    /// the engine only over the ADR-PC-027 HTTP contract, never a compile-time kernel reference).
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
    public void Notification_core_references_no_family_and_no_engine_spine_project()
    {
        var repoRoot = RepoRoot();
        var familiesDir = Path.GetFullPath(Path.Combine(repoRoot, "families")) + Path.DirectorySeparatorChar;
        var spineDirs = SpineProjects.ToDictionary(
            p => p,
            p => Path.GetFullPath(Path.Combine(repoRoot, "engine", "src", p)) + Path.DirectorySeparatorChar,
            StringComparer.Ordinal);

        var violations = new List<string>();

        foreach (var project in CoreProjects)
        {
            var csprojPath = Path.Combine(repoRoot, "notification", "src", project, project + ".csproj");
            Assert.True(File.Exists(csprojPath), $"notification core project not found on disk: {csprojPath}");

            var projectDir = Path.GetDirectoryName(csprojPath)!;
            foreach (var include in ProjectReferenceIncludes(csprojPath))
            {
                // Resolve the (relative) Include against the .csproj's own directory, then normalise —
                // a reference lands "in families/**" or "in the spine" iff its absolute path is under
                // that tree, regardless of the ../ shape used to reach it.
                var normalisedInclude = include.Replace('\\', Path.DirectorySeparatorChar);
                var resolved = Path.GetFullPath(Path.Combine(projectDir, normalisedInclude));

                if (resolved.StartsWith(familiesDir, StringComparison.Ordinal))
                {
                    violations.Add($"{project} → {include}  (families/**)");
                }

                foreach (var (spine, dir) in spineDirs)
                {
                    if (resolved.StartsWith(dir, StringComparison.Ordinal))
                    {
                        violations.Add($"{project} → {include}  (engine spine: {spine})");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "ADR-IC-019 §D2/§P2: the notification core must reference no families/** project and no "
            + "engine-spine project — it reads the engine only over the ADR-PC-027 contract. The "
            + "family → core arrow is one-way. Offending references:\n  "
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
    /// Walks up from the test assembly's base directory to the repo root, identified by the committed
    /// solution at <c>engine/Babelstone.slnx</c> (the same disk-marker pattern <c>EngineFamilyAgnosticTests</c>
    /// uses — worktree-safe, no <c>.git</c> dependency).
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
