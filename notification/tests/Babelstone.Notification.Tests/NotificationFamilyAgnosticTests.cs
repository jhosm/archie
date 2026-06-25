using System.Text.RegularExpressions;
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
    /// composition root <c>Babelstone.Notification.Host</c> is the standing ADR-PC-021 §A2 exemption — it
    /// MAY <c>ProjectReference</c> families/** to compose them, so it is deliberately NOT in this set. The
    /// core library <c>Babelstone.Notification</c> (the poll loop, the schedule pass, the dedupe ledger, the
    /// read client, and the <c>IFamilyNotificationModule</c> contribution port) stays family-agnostic and is
    /// checked directly — both for a <c>&lt;ProjectReference&gt;</c> into families/**/the engine spine AND
    /// for a family literal embedded in its source (ADR-IC-019 §D2/§P2 + Amendment 2026-06-24).
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

    /// <summary>
    /// NOTIF-1 hardening (ADR-IC-019 §D1 + Amendment 2026-06-24): the core carries no family knowledge
    /// embedded as a LITERAL, not just no <c>&lt;ProjectReference&gt;</c>. The project-reference gate above is
    /// blind to a family rule smuggled in as string/int literals — the exact gap PR #317 exposed
    /// (<c>OptOutWindowDays = 14</c> + <c>"pt.notice.maturity"</c> in the generic core: green gate, real §D1
    /// violation). This scans the core source for a family namespace/type, a family discriminator, or a pack
    /// template-ref, stripping COMMENTS first (the core's docs legitimately explain what it does NOT do, e.g.
    /// "e.g. <c>pt.notice.maturity</c>") — the same strip-comments-keep-literals technique the orchestrator's
    /// substrate topic gate uses. A bare int like <c>14</c> is uncatchable by any scan; the load-bearing fix
    /// is the structural relocation of the family rule into <c>families/**/.Notification</c> (which makes the
    /// project-reference gate above meaningful again), and this is the defense-in-depth tripwire over the
    /// now-clean core.
    /// </summary>
    [Fact]
    public void Notification_core_source_names_no_embedded_family_literal()
    {
        var repoRoot = RepoRoot();

        // Family vocabulary that must never appear as a literal in the family-agnostic core (ADR-IC-019 §D1).
        string[] forbiddenTokens = ["Babelstone.Families", "term_deposit", "TermDeposit"];

        // Pack disclosure/notice template-ref namespaces (ADR-PC-025 slot 1 / surface §3.3–§3.4) — a
        // family-owned template literal in the core is a §D1 leak.
        var templateRefGrammar = new Regex(@"\bpt\.(?:notice|disclosure|fine|fipre|secci)\.", RegexOptions.Compiled);

        var violations = new List<string>();

        foreach (var project in CoreProjects)
        {
            var projectDir = Path.Combine(repoRoot, "notification", "src", project);
            Assert.True(Directory.Exists(projectDir), $"notification core project dir not found: {projectDir}");

            foreach (var file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
            {
                // Skip generated output (obj/, bin/) — only the hand-authored core source is the contract.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                // Strip COMMENTS (keep string literals — a family template-ref IS a literal). A family named
                // in a doc comment is allowed; a family literal in code is the regression.
                var code = StripComments(File.ReadAllText(file));
                var name = Path.GetFileName(file);

                foreach (var token in forbiddenTokens)
                {
                    if (code.Contains(token, StringComparison.Ordinal))
                    {
                        violations.Add($"{project}/{name} names family token '{token}'");
                    }
                }

                var match = templateRefGrammar.Match(code);
                if (match.Success)
                {
                    violations.Add($"{project}/{name} names pack template-ref '{match.Value}…'");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "ADR-IC-019 §D1 + Amendment 2026-06-24: the notification core must embed no family knowledge as a "
            + "literal — a family scheduling rule (a window width, a template ref, a 'due now' decision) lives "
            + "in a families/**/.Notification contribution, not the generic core. A family named in a comment "
            + "is fine; a family literal in code is the §D1 violation PR #317 introduced. Offending references "
            + "(in code, not comments):\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>Strips <c>//</c> and <c>/* */</c> comments, leaving string/char literals intact (a family
    /// template-ref IS a literal, which is exactly what the scan must see). Mirrors the orchestrator
    /// substrate topic gate's <c>StripComments</c>.</summary>
    private static string StripComments(string source)
    {
        var output = new System.Text.StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && next == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    i++;
                }

                i += 2;
                output.Append(' ');
                continue;
            }

            // Skip OVER a string/char literal verbatim (keep its body — a family template-ref literal must be seen).
            if (c is '"' or '\'')
            {
                var quote = c;
                output.Append(c);
                i++;
                while (i < source.Length)
                {
                    output.Append(source[i]);
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        output.Append(source[i + 1]);
                        i += 2;
                        continue;
                    }

                    if (source[i] == quote)
                    {
                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            output.Append(c);
            i++;
        }

        return output.ToString();
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
