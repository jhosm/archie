using System.Xml.Linq;
using Xunit;

namespace Babelstone.Composition.Tests;

/// <summary>
/// FAMILY_TO_CORE_DEFAULT_DENY (ADR-PC-040 §D1/§D2; catalogue row XC-1): the <em>family → core</em>
/// arrow is one-way <b>repo-wide, by default</b>. Every <c>.csproj</c> in the repository that is not
/// (a) a family contribution under <c>families/**</c>, (b) a test project, or (c) explicitly marked
/// with a <c>&lt;BabelstoneRole&gt;</c> opt-out MUST carry no <c>ProjectReference</c> resolving into
/// <c>families/**</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why default-deny, not an allowlist.</b> The per-estate gates (<c>EngineFamilyAgnosticTests</c>,
/// <c>OrchestratorFamilyAgnosticTests</c>, <c>NotificationFamilyAgnosticTests</c>,
/// <c>LifecycleFamilyAgnosticTests</c>) each guard a hand-enumerated core set, kept in lockstep with
/// their governing ADR — the decision-linked layer, which stays. But an allowlist protects only what
/// it lists: a NEW core/substrate project was gated by nothing until someone wrote a test for it —
/// exactly how the lifecycle driver shipped with the arrow reversed until PR #404. This gate inverts
/// the default: a project is presumed to be a family-agnostic core unless its own <c>.csproj</c>
/// declares otherwise, so a new project is covered at birth with zero gate edit.
/// </para>
/// <para>
/// <b>The classification signal (ADR-PC-040 §D2)</b> is a single MSBuild property in the project
/// file, read off disk here: ABSENT or <c>Core</c> → gated (the default); <c>CompositionRoot</c> →
/// the one explicit, visible opt-out that MAY reference <c>families/**</c> (the ADR-PC-021 §A2
/// standing-exemption pattern — the engine API, orchestrator, notification, and lifecycle hosts),
/// itself gated by the sibling <c>COMPOSITION_ROOT_NAMES_NO_FAMILY</c> source scan;
/// <c>TestRig</c> → declared non-shipping test tooling (the ADR-PC-011 load harness), treated like a
/// test project; anything else → fail-closed (vocabulary drift is an error, not a silent skip).
/// Family contributions are recognised by their <c>families/**</c> path; test projects by
/// <c>&lt;IsTestProject&gt;true&lt;/IsTestProject&gt;</c> or a <c>tests</c> path segment (which also
/// covers the committed test fixture projects).
/// </para>
/// </remarks>
public sealed class FamilyAgnosticDefaultDenyTests
{
    /// <summary>The recognised <c>&lt;BabelstoneRole&gt;</c> vocabulary (ADR-PC-040 §D2). Extending it
    /// requires amending the ADR — the gate fails an unknown value by design.</summary>
    private const string RoleCore = "Core";
    private const string RoleCompositionRoot = "CompositionRoot";
    private const string RoleTestRig = "TestRig";

    [Fact]
    public void No_unmarked_project_references_a_families_project()
    {
        var repoRoot = RepoRoot();
        var familiesDir = Path.GetFullPath(Path.Combine(repoRoot, "families")) + Path.DirectorySeparatorChar;

        var violations = new List<string>();
        var compositionRoots = new List<string>();
        var gatedProjects = 0;

        foreach (var csprojPath in AllProjectFiles(repoRoot))
        {
            var relative = Path.GetRelativePath(repoRoot, csprojPath).Replace(Path.DirectorySeparatorChar, '/');

            // (a) A family contribution IS the family — the arrow's tail, never its head.
            if (csprojPath.StartsWith(familiesDir, StringComparison.Ordinal))
            {
                continue;
            }

            var doc = XDocument.Load(csprojPath);

            // (b) Test projects (and the committed fixtures under a tests/ dir) may reference families
            // by design — they exercise them.
            if (IsTestProject(doc, relative))
            {
                continue;
            }

            // (c) The single machine-readable role signal (ADR-PC-040 §D2).
            var role = PropertyValue(doc, "BabelstoneRole");
            switch (role)
            {
                case RoleCompositionRoot:
                    // The explicit opt-out: MAY reference families/** (the scan anchor). Its own gate
                    // is the COMPOSITION_ROOT_NAMES_NO_FAMILY source scan (CompositionRootFamilyAgnosticTests).
                    compositionRoots.Add(relative);
                    continue;
                case RoleTestRig:
                    // Declared test tooling (the ADR-PC-011 load harness) — the test-project posture,
                    // made explicit and greppable in the project file.
                    continue;
                case null:
                case "":
                case RoleCore:
                    break; // the default: a family-agnostic core, gated below.
                default:
                    violations.Add(
                        $"{relative}: unknown <BabelstoneRole> value '{role}' — the recognised vocabulary is "
                        + $"'{RoleCore}' (or absent), '{RoleCompositionRoot}', '{RoleTestRig}' (ADR-PC-040 §D2); "
                        + "extending it requires amending the ADR.");
                    continue;
            }

            gatedProjects++;

            var projectDir = Path.GetDirectoryName(csprojPath)!;
            foreach (var include in ProjectReferenceIncludes(doc))
            {
                // Resolve the (relative) Include against the .csproj's own directory, then normalise —
                // a reference lands "in families/**" iff its absolute path is under the repo's
                // families/ tree, regardless of the ../ shape used to reach it (the same resolution
                // the per-estate gates use).
                var normalisedInclude = include.Replace('\\', Path.DirectorySeparatorChar);
                var resolved = Path.GetFullPath(Path.Combine(projectDir, normalisedInclude));
                if (resolved.StartsWith(familiesDir, StringComparison.Ordinal))
                {
                    violations.Add($"{relative} → {include}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "FAMILY_TO_CORE_DEFAULT_DENY (ADR-PC-040 §D1/§D2): a project with no (or a 'Core') "
            + "<BabelstoneRole> is a family-agnostic core BY DEFAULT and must not reference any "
            + "families/** project — the family → core arrow is one-way. If the project genuinely "
            + "composes families, declare <BabelstoneRole>CompositionRoot</BabelstoneRole> in its "
            + ".csproj (one visible line; it then comes under the COMPOSITION_ROOT_NAMES_NO_FAMILY "
            + "source gate). Offending projects/references:\n  "
            + string.Join("\n  ", violations));

        // Sanity floors: a marker sweep must not make the gate vacuous. At least one composition
        // root exists (the engine API host at minimum), and the gated remainder is non-trivial.
        Assert.True(
            compositionRoots.Count > 0,
            "FAMILY_TO_CORE_DEFAULT_DENY: no <BabelstoneRole>CompositionRoot</BabelstoneRole> project "
            + "found anywhere in the repo — the hosts must declare their role (ADR-PC-040 §D2).");
        Assert.True(
            gatedProjects > 0,
            "FAMILY_TO_CORE_DEFAULT_DENY: no gated (unmarked) project found — the repo walk is broken.");
    }

    /// <summary>
    /// Every <c>.csproj</c> committed in the repo, excluding build output (<c>bin/</c>, <c>obj/</c>)
    /// and VCS internals. Deliberately a raw disk walk — the gate must see a project the moment it
    /// exists, whether or not any solution references it yet.
    /// </summary>
    private static IEnumerable<string> AllProjectFiles(string repoRoot)
    {
        foreach (var path in Directory.EnumerateFiles(repoRoot, "*.csproj", SearchOption.AllDirectories))
        {
            if (HasSegment(path, "bin") || HasSegment(path, "obj") || HasSegment(path, ".git")
                || HasSegment(path, "node_modules"))
            {
                continue;
            }

            yield return path;
        }
    }

    /// <summary>A test project by either signal: the MSBuild <c>IsTestProject</c> property, or a
    /// <c>tests</c> path segment (which also covers the committed fixture projects that ship no
    /// tests themselves, e.g. <c>engine/tests/fixtures/**</c>).</summary>
    private static bool IsTestProject(XDocument doc, string relativePath)
    {
        if (string.Equals(PropertyValue(doc, "IsTestProject"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return relativePath.Split('/').Contains("tests", StringComparer.Ordinal);
    }

    /// <summary>The trimmed value of the first MSBuild property with the given local name, or null.</summary>
    private static string? PropertyValue(XDocument doc, string name)
    {
        return doc.Descendants()
            .Where(e => e.Name.LocalName == name && e.Parent?.Name.LocalName == "PropertyGroup")
            .Select(e => e.Value.Trim())
            .FirstOrDefault();
    }

    /// <summary>Every <c>ProjectReference Include="…"</c> in a loaded <c>.csproj</c>, namespace-agnostic.</summary>
    private static IEnumerable<string> ProjectReferenceIncludes(XDocument doc)
    {
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => include!);
    }

    private static bool HasSegment(string path, string segment)
    {
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains(segment, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Walks up from the test assembly's base directory to the repo root, identified by the committed
    /// solution at <c>engine/Babelstone.slnx</c> (the same disk-marker pattern every family-agnostic
    /// gate uses — worktree-safe, no <c>.git</c> dependency).
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
