using System.Text.RegularExpressions;

namespace Babelstone.OutboxPublisher.Tests;

/// <summary>
/// ADR-IC-017 (2026-06-26 amendment) PRODUCER SCOPING. In plain English: the engine embeds EVERY
/// governed <c>contracts/avro/**/*.avsc</c> into its <see cref="Babelstone.Engine.Avro.AvroSchemaCatalog"/>,
/// but a handful of those schemas are produced by a DIFFERENT service, not the engine — the first is
/// the maturity-notice signal <c>NotificationDue(SCHEDULED)</c>, raised by the notification scheduler
/// (ADR-IC-019 / ADR-PC-025), not the engine. Such a schema is engine-OWNED but not engine-EMITTED, so
/// it has no relay-capable engine <c>DomainEvent</c>, no fold handler, and no engine codec round-trip.
/// The §P3 reverse-orphan FAMILY of build-time checks (relay-capable biconditional, handler
/// completeness, codec sweep) anchors on the engine's runtime event set and so must EXEMPT these
/// downstream-producer schemas — exactly as the shell gate <c>scripts/asyncapi-catalog-validate.sh</c>
/// producer-scopes its §P3 leg to <c>x-producer: engine</c>.
///
/// The <c>x-producer</c> marker lives on the AsyncAPI catalogue entry (<c>info.x-producer</c>, default
/// <c>engine</c>), NOT on the <c>.avsc</c>, so this helper reads it off disk — the same hermetic
/// on-disk idiom <c>EmitContractFitnessTests</c> uses — and exposes the set of DOWNSTREAM-producer Avro
/// record names the tests filter out. Every OTHER obligation on a downstream schema (no-PII, BACKWARD,
/// subject well-formedness) is still enforced by the shell gate; only the engine-CLR legs are scoped.
/// </summary>
internal static class DownstreamProducerSchemas
{
    // info.x-producer on the AsyncAPI doc (a single per-file info field). Absent => engine. Declared
    // BEFORE RecordNames so it is initialised when RecordNames's initializer runs (static field
    // initializers execute in textual order).
    private static readonly Regex ProducerRe =
        new(@"^\s*x-producer:\s*['""]?(?<p>[A-Za-z0-9_-]+)", RegexOptions.Multiline | RegexOptions.Compiled);

    // The payload $ref to the governed .avsc — its basename (minus extension) is the Avro record name.
    private static readonly Regex AvscRefRe =
        new(@"\$ref:\s*['""]?[^'""\n]*?/(?<name>[A-Za-z0-9_]+)\.avsc", RegexOptions.Compiled);

    /// <summary>
    /// The Avro record names (== the AsyncAPI message / <c>.avsc</c> <c>name</c>) whose catalogue entry
    /// declares a NON-engine <c>x-producer</c>. Empty unless a downstream-producer event is catalogued.
    /// </summary>
    public static IReadOnlySet<string> RecordNames { get; } = LoadDownstreamRecordNames();

    private static IReadOnlySet<string> LoadDownstreamRecordNames()
    {
        var catalogDir = Path.Combine(RepoRoot(), "contracts", "catalog", "events");
        var downstream = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(catalogDir))
        {
            return downstream;
        }

        foreach (var file in Directory.EnumerateFiles(catalogDir, "*.asyncapi.yaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var producer = ProducerRe.Match(text) is { Success: true } pm ? pm.Groups["p"].Value : "engine";
            if (string.Equals(producer, "engine", StringComparison.Ordinal))
            {
                continue;
            }

            // A non-engine producer: record the .avsc record name(s) this catalogue file promotes.
            foreach (Match m in AvscRefRe.Matches(text))
            {
                downstream.Add(m.Groups["name"].Value);
            }
        }

        return downstream;
    }

    // Walk up to the repo root, identified by the committed solution at engine/Babelstone.slnx — the
    // same disk-marker pattern EmitContractFitnessTests / ShapeLockSnapshotTests use.
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
