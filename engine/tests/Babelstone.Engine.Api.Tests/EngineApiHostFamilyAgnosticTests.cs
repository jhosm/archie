using System.Text.RegularExpressions;
using Xunit;

namespace Babelstone.Engine.Api.Tests;

/// <summary>
/// ENGINE_API_HOST_FAMILY_AGNOSTIC (bd babelstone-9w2k.5; honours ADR-PC-021 §P2/§D4 + §A2/§A14).
/// The capstone fitness gate for the family-count-invariant epic (babelstone-9w2k): the Engine API
/// host's composition CODE names no concrete family type.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this guards, and what it deliberately does not.</b> The host (<c>Babelstone.Engine.Api</c>) is
/// the §D4 composition root — the standing exemption (ADR-PC-021 §A2/§A14) that KEEPS its
/// <c>families/**</c> <c>ProjectReference</c> as the load anchor <c>HostModuleLoader</c> scans. So this is
/// NOT a <c>.csproj</c> dependency gate like <c>EngineFamilyAgnosticTests</c> (that would contradict §A14).
/// It is a SOURCE gate on the composition file: now that family host modules are discovered by
/// assembly-scan (bd babelstone-9w2k.2) and all per-family wiring lives in the family's own
/// <c>IFamilyHostModule</c> (bd babelstone-9w2k.1/.5), the host's <c>Program.cs</c> must name no concrete
/// family type IN CODE — neither a family aggregate state type (<c>DepositPosition</c>), nor a family
/// store/endpoint type (<c>PostgresDepositReadModelStore</c>, <c>DepositsEndpoints</c>), nor a
/// <c>Babelstone.Families.*</c> identifier. A family named only in an explanatory COMMENT is fine; a
/// family named in code is the regression this catches — exactly the surgical edit the epic removed.
/// </para>
/// <para>
/// <b>Why a source scan, not reflection.</b> The host assembly still REFERENCES the family assembly (the
/// scan anchor), so reflection over loaded types cannot distinguish "names a family in composition code"
/// from "transitively references the family for discovery". So the gate is two FAMILY-AGNOSTIC pattern
/// scans — no hand-maintained per-family token list (the shape the engine/orchestrator allowlist gates
/// deliberately avoid as high-churn): (1) the composition file <c>Program.cs</c> for a
/// <c>Babelstone.Families.*</c> identifier (comments and string literals stripped so a family named in
/// prose or a log message does not false-positive — catches a fully-qualified reference or a LOCAL
/// <c>using</c>, whose directive line itself carries the prefix); and (2) the host's GLOBAL-import surface
/// (the csproj <c>&lt;Using&gt;</c> items + any <c>global using</c> in another host file) for the same
/// pattern — the one vector that would let <c>Program.cs</c> name a family with no prefix at the use site,
/// which a <c>Program.cs</c>-only scan cannot see. Both key off the <c>Babelstone.Families.</c> namespace
/// prefix — the membership predicate the whole design uses — so adding a family never edits this test.
/// </para>
/// </remarks>
public sealed class EngineApiHostFamilyAgnosticTests
{
    [Fact]
    public void Host_Program_cs_names_no_concrete_family_type_in_code()
    {
        var programPath = Path.Combine(
            RepoRoot(), "engine", "src", "Babelstone.Engine.Api", "Program.cs");
        Assert.True(File.Exists(programPath), $"host Program.cs not found on disk: {programPath}");

        var code = StripCommentsAndStrings(File.ReadAllText(programPath));

        var violations = new List<string>();

        // The family-agnostic rule: NO `Babelstone.Families.*` identifier in code — catches any family,
        // present or future, with no per-family edit (the family → host arrow is the standing exemption;
        // the host composing a family by NAME is the regression). A fully-qualified reference matches
        // here directly; a LOCAL `using Babelstone.Families.*;` is caught because the directive line
        // itself carries the prefix. The remaining vector — a GLOBAL import that would leave a bare,
        // prefix-less family token at the use site — is covered by Host_imports_no_family_namespace_globally.
        foreach (Match m in Regex.Matches(code, @"\bBabelstone\.Families\.[A-Za-z0-9_.]+"))
        {
            violations.Add($"namespace reference: {m.Value}");
        }

        Assert.True(
            violations.Count == 0,
            "ENGINE_API_HOST_FAMILY_AGNOSTIC (bd babelstone-9w2k.5 / ADR-PC-021 §P2/§D4): the Engine API "
            + "host's Program.cs must name no concrete family type in code — all per-family wiring lives in "
            + "the family's IFamilyHostModule, discovered by assembly-scan. The host keeps its families/** "
            + "ProjectReference as the scan anchor (§A14), but composing a family by NAME is the regression "
            + "this catches. A family named in a COMMENT is fine; in code it is not. Offending references:\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>
    /// The host must not import a family namespace GLOBALLY. <see cref="Host_Program_cs_names_no_concrete_family_type_in_code"/>
    /// scans only the composition file, so it catches a family named via a fully-qualified reference or a
    /// LOCAL <c>using</c> (whose directive line carries the <c>Babelstone.Families.</c> prefix). The one
    /// remaining vector is a GLOBAL import — a csproj <c>&lt;Using Include="Babelstone.Families…"&gt;</c>
    /// item, or a hand-authored <c>global using …Babelstone.Families…;</c> in some OTHER host file — which
    /// would put a bare, prefix-less family token in <c>Program.cs</c>, invisible to a <c>Program.cs</c>-only
    /// scan. The sibling <c>.csproj</c> <c>ENGINE_FAMILY_AGNOSTIC</c> gate cannot backstop this: the host is
    /// the §A2/§A14 standing exemption it does not cover, and it checks <c>ProjectReference</c>, not
    /// <c>&lt;Using&gt;</c>. <c>&lt;ImplicitUsings&gt;</c> (engine/Directory.Build.props) only imports the
    /// SDK set, never a project namespace — so the csproj <c>&lt;Using&gt;</c> items and host
    /// <c>global using</c> directives are the whole global-import vector. This scans both for
    /// <c>Babelstone.Families.*</c> — a pattern, not a per-family token list.
    /// </summary>
    [Fact]
    public void Host_imports_no_family_namespace_globally()
    {
        var hostDir = Path.Combine(RepoRoot(), "engine", "src", "Babelstone.Engine.Api");
        var violations = new List<string>();

        // (a) csproj <Using Include="Babelstone.Families…"> items. Scan the raw XML — do NOT strip the
        // quoted attribute value here, the namespace lives inside the Include attribute.
        var csproj = Path.Combine(hostDir, "Babelstone.Engine.Api.csproj");
        if (File.Exists(csproj))
        {
            foreach (Match m in Regex.Matches(
                File.ReadAllText(csproj), @"<Using\b[^>]*Babelstone\.Families\.[A-Za-z0-9_.]+"))
            {
                violations.Add($"csproj <Using> import: {m.Value}");
            }
        }

        // (b) hand-authored `global using …Babelstone.Families…;` in any committed host .cs file (obj/bin
        // build output skipped — the generated GlobalUsings.g.cs only reflects the csproj <Using> items
        // already covered by (a) plus the family-free ImplicitUsings set). Comments + string literals are
        // stripped so a family named in a comment or a string near a global using does not false-positive;
        // the `[^;]*` keeps each match inside a single `global using …;` statement.
        foreach (var cs in Directory.EnumerateFiles(hostDir, "*.cs", SearchOption.AllDirectories))
        {
            if (cs.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || cs.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var code = StripCommentsAndStrings(File.ReadAllText(cs));
            foreach (Match m in Regex.Matches(
                code, @"global\s+using\b[^;]*\bBabelstone\.Families\.[A-Za-z0-9_.]+"))
            {
                violations.Add($"global using in {Path.GetFileName(cs)}: {Regex.Replace(m.Value, @"\s+", " ")}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "ENGINE_API_HOST_FAMILY_AGNOSTIC (bd babelstone-9w2k.5 / ADR-PC-021 §P2/§D4): the Engine API "
            + "host must not import a family namespace GLOBALLY (a csproj <Using> item or a `global using`), "
            + "which would let Program.cs name a family with no prefix at the use site. Per-family wiring "
            + "lives in the family's IFamilyHostModule, discovered by assembly-scan; the host names no "
            + "family. Offending global imports:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// Removes <c>// line</c> + <c>/* block */</c> comments and the contents of string/char literals
    /// (including verbatim <c>@"…"</c> and interpolated <c>$"…"</c> strings) so a family named only in
    /// prose, a log message, or a configuration-key string never trips the code scan. A single linear
    /// pass over the source — sufficient for the host's plain C# (no raw <c>"""</c> string literals).
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
                i++; // past the opening quote
                while (i < source.Length)
                {
                    if (source[i] == '\\') // an escape — skip the escaped char
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
    /// solution at <c>engine/Babelstone.slnx</c> (same disk-marker pattern as <c>EngineFamilyAgnosticTests</c>).
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
