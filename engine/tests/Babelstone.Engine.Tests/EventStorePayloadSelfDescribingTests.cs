using System.Xml.Linq;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// EVENT_STORE_PAYLOAD_SELF_DESCRIBING (ADR-PC-028): the event-store / replay decode path must be able to
/// read the book of record with <b>no Schema Registry</b>. This is the structural half — the assemblies
/// that decode <c>events.payload</c> on the REPLAY / projection-rebuild path (<c>Babelstone.Engine</c>, which
/// hosts AggregateRuntime / ProjectionRunner / ProjectionReconciler / SimulationRuntime / ReadModelRunner,
/// and <c>Babelstone.EventStore</c>, the store + envelope) carry <b>no reference</b> to a Schema-Registry
/// client (<c>Confluent.SchemaRegistry</c>) or to the Avro/SR codec assembly (<c>Babelstone.Engine.Avro</c>).
/// So the book of record cannot acquire a registry dependency to decode its own history — the property
/// that made JSON (not Avro) the right store format (ADR-PC-028 §Decision). The behavioural half — the JSON
/// store codec round-trips with no registry — lives in <c>Babelstone.Engine.Api.Tests</c>.
/// <para>
/// Avro + the Schema Registry remain the BUS concern (<c>Babelstone.Engine.Avro</c>,
/// <c>Babelstone.InboxConsumer</c>, <c>Babelstone.OutboxPublisher</c>, ADR-IC-002) — deliberately NOT in the
/// set below; this gate is about the store, not the bus.
/// </para>
/// </summary>
public sealed class EventStorePayloadSelfDescribingTests
{
    /// <summary>
    /// The assemblies that decode <c>events.payload</c> on the replay / projection-rebuild path. These must
    /// stay registry-free (ADR-PC-028). The bus assemblies are intentionally absent.
    /// </summary>
    private static readonly string[] ReplayDecodeSpine =
    [
        "Babelstone.Engine",
        "Babelstone.EventStore",
    ];

    /// <summary>A reference is registry-coupled if it pulls in a Schema-Registry client or the Avro/SR codec.</summary>
    private static readonly string[] ForbiddenReferenceFragments =
    [
        "Confluent.SchemaRegistry",
        "Babelstone.Engine.Avro",
    ];

    [Fact]
    public void Replay_decode_spine_takes_no_schema_registry_dependency()
    {
        var srcDir = Path.Combine(RepoRoot(), "engine", "src");
        var violations = new List<string>();

        foreach (var project in ReplayDecodeSpine)
        {
            var csprojPath = Path.Combine(srcDir, project, project + ".csproj");
            Assert.True(File.Exists(csprojPath), $"replay-decode-spine project not found on disk: {csprojPath}");

            foreach (var include in ReferenceIncludes(csprojPath))
            {
                foreach (var forbidden in ForbiddenReferenceFragments)
                {
                    if (include.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add($"{project} → {include}");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "EVENT_STORE_PAYLOAD_SELF_DESCRIBING (ADR-PC-028): the event-store / replay decode spine must decode "
            + "the book of record with NO Schema Registry — it may not reference a registry client "
            + "(Confluent.SchemaRegistry) or the Avro/SR codec (Babelstone.Engine.Avro). Avro + SR are the bus's "
            + "concern only (ADR-IC-002). Offending references:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>Every <c>ProjectReference</c>/<c>PackageReference</c> <c>Include="…"</c> in a <c>.csproj</c>.</summary>
    private static IEnumerable<string> ReferenceIncludes(string csprojPath)
    {
        var doc = XDocument.Load(csprojPath);
        return doc.Descendants()
            .Where(e => e.Name.LocalName is "ProjectReference" or "PackageReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => include!);
    }

    /// <summary>
    /// Walks up from the test assembly's base directory to the repo root, identified by the committed
    /// solution at <c>engine/Babelstone.slnx</c> (same disk-marker pattern as the family-agnostic gate).
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
