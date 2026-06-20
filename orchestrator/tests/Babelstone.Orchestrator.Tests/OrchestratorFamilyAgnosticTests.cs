using System.Text.RegularExpressions;
using System.Xml.Linq;
using Babelstone.Orchestrator.Dispatch;
using Babelstone.Orchestrator.Saga;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// The two orchestrator-substrate fitness gates (ADR-IC-018 Verifiable commitments) — the saga-side
/// cousins of the engine's <c>EngineFamilyAgnosticTests</c>:
/// <list type="bullet">
///   <item><b>ORCHESTRATOR_FAMILY_AGNOSTIC</b> (§D2/§D4/§P2): the substrate carries no
///   <c>&lt;ProjectReference&gt;</c> to any <c>families/**</c> project — the <em>family → substrate</em>
///   arrow is one-way; the host composition root is the standing exemption. A sibling test keeps the
///   substrate-project allowlist in lockstep with the §P1/§P2 enumeration parsed off the ADR.</item>
///   <item><b>ORCHESTRATOR_SUBSTRATE_NO_CONCRETE_SAGA</b> (§D1/§D3/§P3/§P6): the substrate assembly
///   defines NO concrete <see cref="ISagaStateMachine"/> / <see cref="IResultEventBridge"/> /
///   <see cref="ISagaCommandRouter"/> implementation — every concrete saga lives in a family
///   <c>.Orchestration</c> module. Catches a concrete saga typed INSIDE the substrate even when no
///   <c>.csproj</c> reference does (the §Residual-risk the ADR names).</item>
/// </list>
/// </summary>
public sealed class OrchestratorFamilyAgnosticTests
{
    /// <summary>
    /// The orchestrator substrate set, by ADR-IC-018 §P1/§P2's enumeration. This single project MUST
    /// stay family-agnostic; a reference from it into <c>families/**</c> fails the build. The host
    /// (<c>Babelstone.Orchestrator</c>) is deliberately EXCLUDED — it is the §D4 composition root that
    /// MAY reference a family. <c>SubstrateProjects_match_the_ADR_IC_018_P1_P2_list</c> keeps this
    /// allowlist in lockstep with the ADR off disk, so the gate cannot silently drift from the decision.
    /// </summary>
    private static readonly string[] SubstrateProjects =
    [
        "Babelstone.Orchestrator.Substrate",
    ];

    [Fact]
    public void No_substrate_project_references_a_families_project()
    {
        var repoRoot = RepoRoot();
        var srcDir = Path.Combine(repoRoot, "orchestrator", "src");
        var familiesDir = Path.GetFullPath(Path.Combine(repoRoot, "families")) + Path.DirectorySeparatorChar;

        var violations = new List<string>();

        foreach (var project in SubstrateProjects)
        {
            var csprojPath = Path.Combine(srcDir, project, project + ".csproj");
            Assert.True(File.Exists(csprojPath), $"substrate project not found on disk: {csprojPath}");

            var projectDir = Path.GetDirectoryName(csprojPath)!;
            foreach (var include in ProjectReferenceIncludes(csprojPath))
            {
                // Resolve the (relative) Include against the .csproj's own directory, then normalise —
                // a reference lands "in families/**" iff its absolute path is under the repo's
                // families/ tree, regardless of the ../ shape used to reach it.
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
            "ADR-IC-018 §D2/§D4/§P2: the orchestrator substrate must not reference any families/** project. "
            + "The family → substrate arrow is one-way; the host composition root is the standing exemption. "
            + "Offending references:\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>
    /// The <see cref="SubstrateProjects"/> allowlist must equal — exactly, as a set — the substrate
    /// project(s) ADR-IC-018 §P1/§P2 enumerate. Parsed off the ADR file the same way the sibling test
    /// parses <c>.csproj</c> off disk, so the gate cannot silently drift from the decision: add a
    /// substrate project to the ADR without adding it here (or vice versa) and this fails, naming the gap.
    /// </summary>
    [Fact]
    public void SubstrateProjects_match_the_ADR_IC_018_P1_P2_list()
    {
        var adrSubstrate = SubstrateProjectsFromAdr(RepoRoot());

        Assert.True(
            adrSubstrate.SetEquals(SubstrateProjects),
            "ADR-IC-018 §P1/§P2: the SubstrateProjects allowlist has drifted from the §P1/§P2 enumeration. "
            + $"Only in the ADR: [{string.Join(", ", adrSubstrate.Except(SubstrateProjects).Order())}]. "
            + $"Only in the test: [{string.Join(", ", SubstrateProjects.Except(adrSubstrate).Order())}]. "
            + "Reconcile the allowlist and the ADR in the same change (the §D5 explicit-drift rule).");
    }

    /// <summary>
    /// The substrate project names ADR-IC-018 names as the substrate set. §P1's project-topology code
    /// block places each substrate project on its own line as the path segment
    /// <c>Babelstone.Orchestrator.Substrate/</c> (the directory), and §P2 names it backtick-wrapped as
    /// "the §P1 `Babelstone.Orchestrator.Substrate` set". We pull the <c>Babelstone.Orchestrator.*</c>
    /// identifiers that §P1/§P2 mark as the substrate, EXCLUDING the bare host
    /// <c>Babelstone.Orchestrator</c> (the §D4 composition root, explicitly NOT a substrate project) —
    /// the substrate names all carry the <c>.Substrate</c> suffix. Robust to whitespace and member order.
    /// </summary>
    private static HashSet<string> SubstrateProjectsFromAdr(string repoRoot)
    {
        var adrPath = Path.Combine(
            repoRoot,
            "docs", "product-management", "integration_concepts", "adrs",
            "ADR-IC-018-family-owned-saga-modules.md");
        Assert.True(File.Exists(adrPath), $"ADR-IC-018 not found on disk: {adrPath}");

        var adr = File.ReadAllText(adrPath);

        // The substrate set is every Babelstone.Orchestrator.*Substrate* identifier the ADR names —
        // both §P1's `Babelstone.Orchestrator.Substrate/` topology line and §P2's backtick-wrapped
        // `Babelstone.Orchestrator.Substrate` reference resolve to the same name. We deliberately match
        // only the .Substrate-suffixed names so the bare host `Babelstone.Orchestrator` (the §D4
        // composition root, NOT a substrate project) never leaks into the substrate allowlist.
        var names = Regex.Matches(adr, @"\bBabelstone\.Orchestrator(?:\.[A-Za-z0-9]+)*\.Substrate\b")
            .Select(m => m.Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(names);
        return names;
    }

    /// <summary>
    /// ORCHESTRATOR_SUBSTRATE_NO_CONCRETE_SAGA (ADR-IC-018 §D1/§D3/§P3/§P6). The substrate assembly
    /// defines NO concrete (non-abstract, non-interface) type that implements
    /// <see cref="ISagaStateMachine"/>, <see cref="IResultEventBridge"/>, or
    /// <see cref="ISagaCommandRouter"/> — every concrete saga lives in a family <c>.Orchestration</c>
    /// module. This catches a concrete <c>TableStateMachine</c> subclass typed directly in the substrate
    /// even with no <c>.csproj</c> reference (the §Residual risk the ADR names). An in-process reflection
    /// check — no disk walk.
    /// </summary>
    [Fact]
    public void Substrate_assembly_defines_no_concrete_saga_implementation()
    {
        // ISagaStateMachine is defined in the substrate, so its assembly IS the substrate assembly.
        var substrateAssembly = typeof(ISagaStateMachine).Assembly;
        Assert.Equal("Babelstone.Orchestrator.Substrate", substrateAssembly.GetName().Name);

        var violations = substrateAssembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .Where(t => typeof(ISagaStateMachine).IsAssignableFrom(t)
                     || typeof(IResultEventBridge).IsAssignableFrom(t)
                     || typeof(ISagaCommandRouter).IsAssignableFrom(t))
            .Select(t => t.FullName!)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "ADR-IC-018 §D2/§P3: the substrate assembly defines no concrete saga implementation "
            + "(ISagaStateMachine / IResultEventBridge / ISagaCommandRouter). Every concrete saga lives "
            + "in a family .Orchestration module. Offending types:\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>
    /// ORCHESTRATOR_SUBSTRATE_NO_FAMILY_TOPIC_CONSTANT (bd babelstone-9w2k.5; honours ADR-IC-018 §D2/§P4 +
    /// ADR-IC-003 §S2). The substrate's saga subscription wiring (<c>Inbox/</c>) names NO per-family topic
    /// constant — the topics it subscribes to arrive EXCLUSIVELY from the family module's
    /// <c>ISagaModule.ConsumeTopics</c> via the <c>required</c> <see cref="SagaInboxConsumerOptions.Topics"/>
    /// (derived from the AsyncAPI catalogue, bd babelstone-9w2k.4). A hardcoded family topic literal here —
    /// e.g. a <c>"term_deposit"</c> constant or a <c>"deposits.process.events"</c> subscription string —
    /// would be the precise per-family edit the family-count-invariant epic removes: a missed/forgotten
    /// topic is a saga that silently never advances (no replay-safe recovery), so this is CI-gated. A
    /// source scan over the substrate's consume wiring with comments and string literals... wait, NO: a
    /// family topic WOULD be a string literal, so we scan the RAW source (literals included) but strip only
    /// COMMENTS — a family named in an explanatory comment is fine; a family topic in a literal is the
    /// regression. The complement of <c>Substrate_assembly_defines_no_concrete_saga_implementation</c>:
    /// that guards the saga TYPE level, this guards the topic-SUBSCRIPTION level.
    /// </summary>
    [Fact]
    public void Substrate_subscription_wiring_names_no_per_family_topic_constant()
    {
        var inboxDir = Path.Combine(
            RepoRoot(), "orchestrator", "src", "Babelstone.Orchestrator.Substrate", "Inbox");
        Assert.True(Directory.Exists(inboxDir), $"substrate Inbox/ wiring dir not found: {inboxDir}");

        // Family tokens that must never appear as a topic constant in the substrate's subscription wiring.
        // The load-bearing rule is the term-deposit family vocabulary (the only loaded family); a new
        // family adds its tokens in the same spirit the spine allowlists track their ADR enumeration.
        string[] familyTopicTokens =
        [
            "term_deposit",
            "deposits.process.events",
            "TermDeposit",
        ];

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(inboxDir, "*.cs", SearchOption.AllDirectories))
        {
            // Strip only COMMENTS (not string literals — a family TOPIC would be a literal, which is
            // exactly what we want to catch). A family named in a comment is allowed.
            var code = StripComments(File.ReadAllText(file));
            foreach (var token in familyTopicTokens)
            {
                if (code.Contains(token, StringComparison.Ordinal))
                {
                    violations.Add($"{Path.GetFileName(file)} names '{token}'");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "ADR-IC-018 §D2/§P4 / ADR-IC-003 §S2 (bd babelstone-9w2k.5): the orchestrator substrate's saga "
            + "subscription wiring must name no per-family topic constant — the consume topics arrive only "
            + "from the family module's ISagaModule.ConsumeTopics via the required Topics option. A "
            + "hardcoded family topic here is the per-family edit the family-count-invariant epic removes. "
            + "Offending references (in code, not comments):\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>Strips <c>//</c> and <c>/* */</c> comments, leaving string literals intact (a family topic IS a literal).</summary>
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

            // Skip OVER a string/char literal verbatim (keep its body — a family topic literal must be seen).
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
    /// solution at <c>engine/Babelstone.slnx</c> (same disk-marker pattern as
    /// <c>EngineFamilyAgnosticTests.RepoRoot</c>).
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
