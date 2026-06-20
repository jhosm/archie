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
/// from "transitively references the family for discovery". The composition code is one file
/// (<c>Program.cs</c>); scanning its source — comments and string literals stripped so a family named in
/// prose or a log message does not false-positive — is the precise check. The same family-token
/// vocabulary the engine/orchestrator gates use keys this scan off the decision, not a fragile literal.
/// </para>
/// </remarks>
public sealed class EngineApiHostFamilyAgnosticTests
{
    /// <summary>
    /// Concrete family identifiers that must never appear in the host's composition CODE. Drawn from the
    /// term-deposit family's public surface (the only loaded family). A new family adds its tokens here in
    /// the same spirit the spine allowlist tracks §P2 — but the load-bearing rule is the
    /// <c>Babelstone.Families.</c> namespace prefix below, which catches ANY family with no per-token edit.
    /// </summary>
    private static readonly string[] FamilyTypeTokens =
    [
        "DepositPosition",            // the family aggregate STATE type — the issue's "aggregate type reference"
        "PostgresDepositReadModelStore",
        "IDepositReadModelStore",
        "DepositReadModelRow",
        "DepositsEndpoints",
        "TermDepositHostModule",
        "TermDepositFamilyModule",
        "TermDepositConstitutionService",
    ];

    [Fact]
    public void Host_Program_cs_names_no_concrete_family_type_in_code()
    {
        var programPath = Path.Combine(
            RepoRoot(), "engine", "src", "Babelstone.Engine.Api", "Program.cs");
        Assert.True(File.Exists(programPath), $"host Program.cs not found on disk: {programPath}");

        var code = StripCommentsAndStrings(File.ReadAllText(programPath));

        var violations = new List<string>();

        // The load-bearing rule: NO `Babelstone.Families.*` identifier in code — catches any family,
        // present or future, with no per-family edit (the family → host arrow is the standing exemption;
        // the host composing a family by NAME is the regression).
        foreach (Match m in Regex.Matches(code, @"\bBabelstone\.Families\.[A-Za-z0-9_.]+"))
        {
            violations.Add($"namespace reference: {m.Value}");
        }

        // Plus the concrete family TYPE tokens (a family type reached via a `using` would have no
        // namespace prefix at the use site), each as a whole-word match.
        foreach (var token in FamilyTypeTokens)
        {
            if (Regex.IsMatch(code, $@"\b{Regex.Escape(token)}\b"))
            {
                violations.Add($"family type: {token}");
            }
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
