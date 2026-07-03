using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Babelstone.Composition.Tests;

/// <summary>
/// COMPOSITION_ROOT_NAMES_NO_FAMILY (ADR-PC-040 §D3; catalogue row XC-2): EVERY composition root —
/// every project marked <c>&lt;BabelstoneRole&gt;CompositionRoot&lt;/BabelstoneRole&gt;</c> — composes
/// families by discovery and names no <c>Babelstone.Families.*</c> identifier in its composition
/// surface. The row-12b gate (<c>EngineApiHostFamilyAgnosticTests</c>, ADR-PC-021 §A17–§A18)
/// generalised from the engine host to ALL roots: the same two family-agnostic pattern scans, applied
/// to whichever projects carry the marker — so a new host is covered by adding its one marker line,
/// with zero edit here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope (deliberately the 12b scope).</b> (1) The composition file <c>Program.cs</c>, comments and
/// string literals stripped, must contain no <c>Babelstone.Families.*</c> identifier — catches a
/// fully-qualified reference or a LOCAL <c>using</c> (whose directive line carries the prefix); (2) the
/// project's GLOBAL-import surface — csproj <c>&lt;Using&gt;</c> items and any <c>global using</c> in a
/// committed project file — must import no family namespace, the one vector that would put a bare,
/// prefix-less family token in <c>Program.cs</c>. A host-local family-specific edge adapter FILE with a
/// LOCAL import stays legal (ADR-PC-040 §D3 — an API-surface concern the covenant does not decide);
/// what can never happen is <c>Program.cs</c> naming a family, because that is precisely the
/// compose-by-hand edit discovery exists to remove.
/// </para>
/// <para>
/// <b>Why keyed on the marker.</b> The roots are discovered off the same single machine-readable
/// <c>&lt;BabelstoneRole&gt;</c> signal the default-deny dependency gate
/// (<c>FamilyAgnosticDefaultDenyTests</c>, XC-1) reads — no hand-maintained host list. Dropping a
/// marker does not escape gating: an unmarked host still referencing <c>families/**</c> then fails
/// XC-1 instead.
/// </para>
/// </remarks>
public sealed class CompositionRootFamilyAgnosticTests
{
    private static readonly Regex FamilyIdentifier =
        new(@"\bBabelstone\.Families\.[A-Za-z0-9_.]+", RegexOptions.Compiled);

    [Fact]
    public void Every_composition_root_Program_cs_names_no_family_in_code()
    {
        var violations = new List<string>();
        var roots = CompositionRootProjects();

        Assert.NotEmpty(roots);

        foreach (var csprojPath in roots)
        {
            var projectDir = Path.GetDirectoryName(csprojPath)!;
            var programPath = Path.Combine(projectDir, "Program.cs");
            Assert.True(
                File.Exists(programPath),
                $"composition root has no Program.cs to gate: {csprojPath} — a CompositionRoot-marked "
                + "project is a runnable host whose composition file this gate scans (ADR-PC-040 §D3).");

            var code = StripCommentsAndStrings(File.ReadAllText(programPath));
            foreach (Match m in FamilyIdentifier.Matches(code))
            {
                violations.Add($"{Relative(programPath)}: {m.Value}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "COMPOSITION_ROOT_NAMES_NO_FAMILY (ADR-PC-040 §D3): a composition root's Program.cs must "
            + "name no concrete family in code — per-family wiring lives in the family's own module, "
            + "discovered by assembly-scan (FamilyModuleScanner). A family named in a COMMENT is fine; "
            + "in code it is the compose-by-hand edit discovery removes. Offending references:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void No_composition_root_imports_a_family_namespace_globally()
    {
        var violations = new List<string>();

        foreach (var csprojPath in CompositionRootProjects())
        {
            var projectDir = Path.GetDirectoryName(csprojPath)!;

            // (a) csproj <Using Include="Babelstone.Families…"> items. Scan the raw XML — the
            // namespace lives inside the Include attribute.
            foreach (Match m in Regex.Matches(
                File.ReadAllText(csprojPath), @"<Using\b[^>]*Babelstone\.Families\.[A-Za-z0-9_.]+"))
            {
                violations.Add($"{Relative(csprojPath)} <Using> import: {m.Value}");
            }

            // (b) a hand-authored `global using …Babelstone.Families…;` in any committed project .cs
            // (obj/bin build output skipped — generated GlobalUsings.g.cs only reflects (a) plus the
            // family-free SDK ImplicitUsings set). Comments + string literals stripped so a family
            // named in prose does not false-positive; `[^;]*` keeps each match inside one statement.
            foreach (var cs in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
            {
                if (HasSegment(cs, "obj") || HasSegment(cs, "bin"))
                {
                    continue;
                }

                var code = StripCommentsAndStrings(File.ReadAllText(cs));
                foreach (Match m in Regex.Matches(
                    code, @"global\s+using\b[^;]*\bBabelstone\.Families\.[A-Za-z0-9_.]+"))
                {
                    violations.Add(
                        $"global using in {Relative(cs)}: {Regex.Replace(m.Value, @"\s+", " ")}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "COMPOSITION_ROOT_NAMES_NO_FAMILY (ADR-PC-040 §D3): a composition root must not import a "
            + "family namespace GLOBALLY (a csproj <Using> item or a `global using`), which would let "
            + "Program.cs name a family with no prefix at the use site — invisible to the Program.cs "
            + "scan. Offending global imports:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// Every <c>&lt;BabelstoneRole&gt;CompositionRoot&lt;/BabelstoneRole&gt;</c>-marked project file —
    /// the same single classification signal <c>FamilyAgnosticDefaultDenyTests</c> (XC-1) reads, so
    /// the two universal gates can never disagree about which projects are roots.
    /// </summary>
    private static IReadOnlyList<string> CompositionRootProjects()
    {
        var repoRoot = RepoRoot();
        var roots = new List<string>();

        foreach (var csprojPath in Directory.EnumerateFiles(repoRoot, "*.csproj", SearchOption.AllDirectories))
        {
            if (HasSegment(csprojPath, "bin") || HasSegment(csprojPath, "obj")
                || HasSegment(csprojPath, ".git") || HasSegment(csprojPath, "node_modules"))
            {
                continue;
            }

            var doc = XDocument.Load(csprojPath);
            var role = doc.Descendants()
                .Where(e => e.Name.LocalName == "BabelstoneRole" && e.Parent?.Name.LocalName == "PropertyGroup")
                .Select(e => e.Value.Trim())
                .FirstOrDefault();

            if (role == "CompositionRoot")
            {
                roots.Add(csprojPath);
            }
        }

        return roots;
    }

    private static string Relative(string path)
    {
        return Path.GetRelativePath(RepoRoot(), path).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static bool HasSegment(string path, string segment)
    {
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains(segment, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes <c>// line</c> + <c>/* block */</c> comments and the contents of string/char literals
    /// (regular, char, AND verbatim <c>@"…"</c> / interpolated <c>$"…"</c> / <c>$@"…"</c> strings) so a
    /// family named only in prose, a log message, or a configuration-key string never trips the code
    /// scan. Mirrors <c>EngineApiHostFamilyAgnosticTests.StripCommentsAndStrings</c> (whose
    /// verbatim-string handling is pinned by its own regression test): a non-verbatim literal escapes
    /// with <c>\</c>; a verbatim literal treats <c>\</c> as literal and escapes a quote by doubling it,
    /// so <c>@"C:\dir\"</c> closes at the right quote and the code after it is not swallowed.
    /// </summary>
    private static string StripCommentsAndStrings(string source)
    {
        var output = new System.Text.StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            // Line comment.
            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            // Block comment.
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

            // String / char literal — skip its body (preserving the rest of the code).
            if (c is '"' or '\'')
            {
                var quote = c;

                // A verbatim string (@"…", $@"…", @$"…") escapes a quote by DOUBLING it and treats
                // backslash as a LITERAL character. Detect it by the `@` sigil immediately before the
                // opening quote (possibly behind a `$`).
                var prev = i > 0 ? source[i - 1] : '\0';
                var prevPrev = i > 1 ? source[i - 2] : '\0';
                var verbatim = quote == '"' && (prev == '@' || (prev == '$' && prevPrev == '@'));

                i++; // past the opening quote
                while (i < source.Length)
                {
                    if (verbatim)
                    {
                        if (source[i] == '"')
                        {
                            if (i + 1 < source.Length && source[i + 1] == '"')
                            {
                                i += 2; // a doubled "" — an escaped quote inside the verbatim string
                                continue;
                            }

                            i++; // past the closing quote
                            break;
                        }

                        i++; // backslash and everything else is literal in a verbatim string
                        continue;
                    }

                    if (source[i] == '\\') // a non-verbatim escape — skip the escaped char
                    {
                        i += 2;
                        continue;
                    }

                    if (source[i] == quote)
                    {
                        i++; // past the closing quote
                        break;
                    }

                    i++;
                }

                output.Append(' ');
                continue;
            }

            output.Append(c);
            i++;
        }

        return output.ToString();
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
