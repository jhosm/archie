using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Babelstone.Composition.Tests;

/// <summary>
/// <c>FAMILY_ORIGINATED_MOVEMENTS_SETTLEMENT_WIRED</c> (catalogue row XC-3; ADR-PC-032 / ADR-IC-018, with
/// ADR-PC-040 as the shared covenant posture). In plain English: if a product family's events can MOVE
/// MONEY (they carry `Originated` Movements), then that family MUST be wired into the settlement machinery
/// — otherwise its cash legs are silently never driven. The personal-loan family shipped exactly that gap:
/// Originated Movement-bearing events with no Orchestration module, so nothing joined its topic to the
/// settlement saga's consume set, and only human review on PR #442 noticed (bd `babelstone-9z9w`). The
/// ADR-PC-040 covenant gates the SHAPE of composition (arrows, discovery), not the EXISTENCE of a family's
/// settlement wiring; this gate closes that class mechanically.
/// </summary>
/// <remarks>
/// <para>
/// <b>Expected side — derived from the Avro catalogue, never a hand-list.</b> A family "moves money" iff
/// any of its event schemas (<c>contracts/avro/**/*.avsc</c>) embeds the governed Movement carrier shape
/// (<c>contracts/avro/_shared/Movement.avsc.json</c>, inlined verbatim per its <c>_usage</c> contract):
/// structurally, a record named <c>Movement</c> whose <c>origin</c> field is the <c>MovementOrigin</c> enum
/// with <c>Originated</c> among its symbols. Keying on the embedded SHAPE — not event names — means a new
/// Movement-bearing event or a whole new family is covered at birth with zero gate edit. An
/// <b>Observed-mode</b> family (the future conta à ordem / credit_card shapes, ADR-PC-039) does NOT trip
/// it: its movements arrive already cleared, so it narrows its inlined carrier's <c>origin</c> enum to
/// <c>["Observed"]</c> — making Observed-only structural — and the gate skips it by construction. The
/// family's topic is the schema's namespace tail (<c>loans.personal_loan</c> → <c>personal_loan</c>), the
/// same topic == channel == aggregate_type convention the relay and gen-saga-topics rely on.
/// </para>
/// <para>
/// <b>Actual side — the three disk-parseable links that make the topic reach the settlement saga.</b>
/// (1) a family Orchestration project's checked-in, catalogue-generated <c>FamilyIntegrationTopics.g.cs</c>
/// lists the topic (<c>gen-saga-topics-check</c> keeps that file honest against the AsyncAPI catalogue);
/// (2) that project defines an <c>ISagaModule</c> (the declarer the host discovers — genuine discovery +
/// declaration is proven live by <c>SagaModuleLoaderTests</c>, which this complements from the
/// contracts side); (3) the orchestrator host's <c>.csproj</c> carries the scan-anchor
/// <c>ProjectReference</c> to that project — without it the module's dll never lands beside the host and
/// discovery misses it SILENTLY (exactly the 9z9w failure mode). Why here and not inside
/// <c>gen-saga-topics.py</c>: that script's job is topic-manifest fidelity per DECLARED family; this
/// gate's job is that a money-moving family IS declared at all — it needs the Avro-shape derivation and
/// the csproj walk, both of which live naturally beside the covenant gates' repo-walking machinery
/// (RepoRoot + XML parsing), with the script's derivation reused via the checked-in manifests rather
/// than duplicated.
/// </para>
/// </remarks>
public sealed class FamilyOriginatedMovementsSettlementWiredTests
{
    [Fact]
    public void Every_family_with_Originated_movement_bearing_events_is_wired_into_the_settlement_consume_set()
    {
        var repoRoot = RepoRoot();

        // ---- EXPECTED: the topics whose events carry Originated Movements, derived from the Avro tree.
        var originatedTopics = OriginatedMovementTopics(repoRoot);

        // No vacuous pass: both shipped families carry Originated Movements today, so an empty derivation
        // means the detector broke (e.g. the carrier shape was renamed), not that nothing moves money.
        Assert.NotEmpty(originatedTopics);

        // ---- ACTUAL: the per-topic wiring links.
        var manifests = OrchestrationTopicManifests(repoRoot);
        var hostReferences = OrchestratorHostProjectReferenceDirs(repoRoot);

        var failures = new List<string>();
        foreach (var (topic, schemas) in originatedTopics.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            // (1) Some family Orchestration project's generated manifest lists the topic.
            var owning = manifests.Where(m => m.Topics.Contains(topic)).ToList();
            if (owning.Count == 0)
            {
                failures.Add(
                    $"topic '{topic}' (Originated Movement-bearing events: {string.Join(", ", schemas)}) appears in NO "
                    + "families/**/FamilyIntegrationTopics.g.cs — the family ships no Orchestration declaration, so its "
                    + "cash legs would silently never reach the settlement saga. Add the family to scripts/gen-saga-topics.py "
                    + "(regenerate with `make gen-saga-topics`) and ship its ISagaModule (see PersonalLoanSagaModule).");
                continue;
            }

            foreach (var manifest in owning)
            {
                // (2) The declaring project defines the ISagaModule the host discovers.
                if (!Directory.EnumerateFiles(manifest.ProjectDir, "*.cs", SearchOption.AllDirectories)
                        .Where(f => !f.EndsWith(".g.cs", StringComparison.Ordinal))
                        .Any(f => File.ReadAllText(f).Contains("ISagaModule", StringComparison.Ordinal)))
                {
                    failures.Add(
                        $"topic '{topic}': '{Path.GetRelativePath(repoRoot, manifest.ProjectDir)}' carries the generated "
                        + "topic manifest but defines no ISagaModule — the manifest alone is never discovered "
                        + "(ADR-IC-018 §D6: the module IS the declarer).");
                }

                // (3) The orchestrator host references the project (the scan anchor). Discovery probes the
                // host's OUTPUT directory for Babelstone.Families.*.dll, so a missing ProjectReference is a
                // module that exists yet is silently never found.
                if (!hostReferences.Contains(Path.GetFullPath(manifest.ProjectDir)))
                {
                    failures.Add(
                        $"topic '{topic}': the orchestrator host (orchestrator/src/Babelstone.Orchestrator) has no "
                        + $"ProjectReference to '{Path.GetRelativePath(repoRoot, manifest.ProjectDir)}' — the scan-anchor "
                        + "reference that lands the module's dll beside the host for SagaModuleLoader discovery "
                        + "(ADR-IC-018 Revised 2026-07-02). Without it the settlement saga never subscribes this topic.");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "FAMILY_ORIGINATED_MOVEMENTS_SETTLEMENT_WIRED (ADR-PC-032 / ADR-IC-018): every family whose Avro catalogue "
            + "carries Originated Movement-bearing events must be wired into the settlement saga's consume set. "
            + "Violations:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void The_shape_detector_keys_on_Originated_so_an_observed_only_carrier_does_not_trip_the_gate()
    {
        // The Observed-mode seam (ADR-PC-039's future families): a family whose movements arrive already
        // cleared narrows its inlined carrier's origin enum to ["Observed"] — structural, so the gate skips
        // it. Pinned against synthetic schema JSON so the seam cannot silently regress when the detector
        // evolves.
        const string observedOnly = """
            {
              "type": "record", "namespace": "accounts.conta_ordem", "name": "SweepObserved",
              "fields": [
                { "name": "movements", "type": { "type": "array", "items": {
                  "type": "record", "name": "Movement",
                  "fields": [
                    { "name": "origin", "type": { "type": "enum", "name": "MovementOrigin", "symbols": ["Observed"] } }
                  ] } } }
              ]
            }
            """;
        const string originated = """
            {
              "type": "record", "namespace": "loans.personal_loan", "name": "Synthetic",
              "fields": [
                { "name": "movements", "type": { "type": "array", "items": {
                  "type": "record", "name": "Movement",
                  "fields": [
                    { "name": "origin", "type": { "type": "enum", "name": "MovementOrigin", "symbols": ["Originated", "Observed"] } }
                  ] } } }
              ]
            }
            """;

        using var observedDoc = JsonDocument.Parse(observedOnly);
        using var originatedDoc = JsonDocument.Parse(originated);

        Assert.False(EmbedsOriginatedMovementCarrier(observedDoc.RootElement));
        Assert.True(EmbedsOriginatedMovementCarrier(originatedDoc.RootElement));
    }

    // ---- expected-side derivation (contracts/avro) --------------------------------------------------

    /// <summary>Topic → the schema file names carrying Originated Movements, derived from every
    /// <c>*.avsc</c> under <c>contracts/avro</c> (the governed carrier shape itself is <c>.avsc.json</c>,
    /// deliberately outside the <c>*.avsc</c> glob — the same exclusion the engine embed and compat gate
    /// use).</summary>
    private static Dictionary<string, List<string>> OriginatedMovementTopics(string repoRoot)
    {
        var avroRoot = Path.Combine(repoRoot, "contracts", "avro");
        Assert.True(Directory.Exists(avroRoot), $"Avro contracts tree not found: {avroRoot}");

        var topics = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(avroRoot, "*.avsc", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".avsc", StringComparison.Ordinal)))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!EmbedsOriginatedMovementCarrier(doc.RootElement))
            {
                continue;
            }

            // The family's topic is the schema namespace's tail segment — topic == channel ==
            // aggregate_type, the relay's documented convention (OutboxDrainer.PublishAsync) the
            // gen-saga-topics manifest derivation also rests on. A Movement-bearing event with no
            // namespace cannot be topic-addressed at all — fail loud, never skip.
            Assert.True(
                doc.RootElement.TryGetProperty("namespace", out var ns) && ns.ValueKind == JsonValueKind.String,
                $"Originated Movement-bearing schema '{Path.GetRelativePath(repoRoot, file)}' declares no namespace — "
                + "its topic (namespace tail == aggregate_type) cannot be derived.");
            var topic = ns.GetString()!.Split('.')[^1];

            if (!topics.TryGetValue(topic, out var files))
            {
                topics[topic] = files = [];
            }

            files.Add(Path.GetFileName(file));
        }

        return topics;
    }

    /// <summary>Whether this schema JSON embeds the governed Movement carrier with <c>Originated</c>
    /// reachable: a record named <c>Movement</c> whose <c>origin</c> field's <c>MovementOrigin</c> enum
    /// includes the <c>Originated</c> symbol (the shape <c>_shared/Movement.avsc.json</c> mandates
    /// inlining verbatim), anywhere in the document. Also treats a NAMED-TYPE REFERENCE to
    /// <c>Movement</c> (an <c>"items": "…Movement"</c> string) as Originated-capable — a referencing
    /// style cannot narrow the symbols, so it fails toward wiring, never toward silence.</summary>
    private static bool EmbedsOriginatedMovementCarrier(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (IsMovementRecord(element, out var originatedReachable))
                {
                    if (originatedReachable)
                    {
                        return true;
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    // The named-type-reference fallback: { "items": "Movement" } / "_shared.Movement".
                    if (property.NameEquals("items")
                        && property.Value.ValueKind == JsonValueKind.String
                        && (property.Value.GetString() ?? string.Empty)
                            .Split('.')[^1].Equals("Movement", StringComparison.Ordinal))
                    {
                        return true;
                    }

                    if (EmbedsOriginatedMovementCarrier(property.Value))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (EmbedsOriginatedMovementCarrier(item))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    /// <summary>Is this JSON object a record named <c>Movement</c>? If so, does its <c>origin</c> field's
    /// enum include <c>Originated</c>?</summary>
    private static bool IsMovementRecord(JsonElement element, out bool originatedReachable)
    {
        originatedReachable = false;
        if (!element.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
            || type.GetString() != "record"
            || !element.TryGetProperty("name", out var name) || name.GetString() != "Movement"
            || !element.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var field in fields.EnumerateArray())
        {
            if (!field.TryGetProperty("name", out var fieldName) || fieldName.GetString() != "origin"
                || !field.TryGetProperty("type", out var fieldType) || fieldType.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (fieldType.TryGetProperty("name", out var enumName) && enumName.GetString() == "MovementOrigin"
                && fieldType.TryGetProperty("symbols", out var symbols) && symbols.ValueKind == JsonValueKind.Array)
            {
                originatedReachable = symbols.EnumerateArray()
                    .Any(s => s.ValueKind == JsonValueKind.String && s.GetString() == "Originated");
            }
        }

        return true;
    }

    // ---- actual-side parsing (families/** manifests + the host csproj) ------------------------------

    private sealed record TopicManifest(string ProjectDir, IReadOnlySet<string> Topics);

    /// <summary>Every checked-in, catalogue-generated <c>FamilyIntegrationTopics.g.cs</c> under
    /// <c>families/**/src</c>, with its declared topic strings (the quoted entries of the generated
    /// <c>All</c> list) and its owning project directory.</summary>
    private static List<TopicManifest> OrchestrationTopicManifests(string repoRoot)
    {
        var familiesRoot = Path.Combine(repoRoot, "families");
        Assert.True(Directory.Exists(familiesRoot), $"families tree not found: {familiesRoot}");

        var manifests = new List<TopicManifest>();
        foreach (var file in Directory.EnumerateFiles(
                     familiesRoot, "FamilyIntegrationTopics.g.cs", SearchOption.AllDirectories))
        {
            var topics = File.ReadLines(file)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith('"') && line.EndsWith("\","))
                .Select(line => line[1..^2])
                .ToHashSet(StringComparer.Ordinal);

            manifests.Add(new TopicManifest(Path.GetDirectoryName(file)!, topics));
        }

        return manifests;
    }

    /// <summary>The absolute directories of every <c>ProjectReference</c> the orchestrator host's
    /// <c>.csproj</c> carries — the scan anchors that land family module dlls beside the host.</summary>
    private static HashSet<string> OrchestratorHostProjectReferenceDirs(string repoRoot)
    {
        var hostCsproj = Path.Combine(
            repoRoot, "orchestrator", "src", "Babelstone.Orchestrator", "Babelstone.Orchestrator.csproj");
        Assert.True(File.Exists(hostCsproj), $"orchestrator host project not found: {hostCsproj}");

        var hostDir = Path.GetDirectoryName(hostCsproj)!;
        return XDocument.Load(hostCsproj)
            .Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFullPath(Path.GetDirectoryName(
                Path.Combine(hostDir, include!.Replace('\\', Path.DirectorySeparatorChar)))!))
            .ToHashSet(StringComparer.Ordinal);
    }

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
