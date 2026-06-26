using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Babelstone.OutboxPublisher.Tests;

/// <summary>
/// DAY-ONE SHAPE-LOCK as a fast unit test (the C# half of the
/// <c>scripts/avro-compat-check.sh</c> step-4 gate). For every emitted-event Avro
/// schema (<c>contracts/avro/**/*.avsc</c>) there must be a committed golden snapshot
/// under <c>contracts/avro/.shape-lock/{subject}.json</c> whose structural fingerprint
/// (namespace + record name + ordered fields, each as name + normalised type +
/// optional/default presence) MATCHES the live schema.
/// </summary>
/// <remarks>
/// Why this exists in addition to the §P3 registry compatibility gate: the registry
/// check is a NO-OP for a brand-new subject — "no previously-published version means
/// nothing to break" — so a day-one field-TYPE mistake on a new family's schema (e.g.
/// a <c>loans.*</c> amount typed <c>int</c> instead of <c>long</c>, or a date authored
/// as a bare <c>int</c> with no <c>logicalType</c>) would sail through §P3 and reach the
/// wire with only review standing between it and a wrong type. The shape-lock pins the
/// FIRST shape the moment the schema lands, so any later drift — and the absence of a
/// snapshot for a new subject — is caught by a gate, not only review.
///
/// The fingerprint here is the SAME normalisation the shell gate's jq filter produces
/// (the committed snapshots are authored by <c>./scripts/avro-compat-check.sh
/// --update-shape-lock</c>); this test re-derives it from the live <c>.avsc</c> and
/// compares against the committed snapshot, so the two halves agree exactly. It is
/// doc-INSENSITIVE — an explanatory <c>doc</c> edit never trips it — and Docker-free,
/// so it runs in the default CI lane. Re-lock an intentional schema change in the same
/// PR with <c>./scripts/avro-compat-check.sh --update-shape-lock</c>.
/// </remarks>
public sealed class ShapeLockSnapshotTests
{
    private static readonly JsonSerializerOptions Canonical = new() { WriteIndented = false };

    public static IEnumerable<object[]> SchemaFiles()
    {
        var avroDir = AvroDir();
        foreach (var schema in Directory.EnumerateFiles(avroDir, "*.avsc", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            yield return new object[] { Path.GetRelativePath(RepoRoot(), schema) };
        }
    }

    [Fact]
    public void At_least_one_schema_is_swept()
    {
        // Guard the data-driven theory: an empty source would make every [Theory] silently vacuous,
        // so a glob that finds nothing must fail loud rather than pass by saying nothing.
        Assert.NotEmpty(SchemaFiles());
    }

    [Theory]
    [MemberData(nameof(SchemaFiles))]
    public void Every_schema_has_a_matching_day_one_shape_lock(string relativeSchemaPath)
    {
        var repoRoot = RepoRoot();
        var schemaPath = Path.Combine(repoRoot, relativeSchemaPath);
        using var doc = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var root = doc.RootElement;

        var fingerprint = ShapeFingerprint(root);
        var subject = fingerprint.GetProperty("subject").GetString()!;

        var lockPath = Path.Combine(repoRoot, "contracts", "avro", ".shape-lock", subject + ".json");
        Assert.True(
            File.Exists(lockPath),
            $"No day-one shape-lock for {subject} ({relativeSchemaPath}). A new subject must pin its "
            + "shape in the SAME change — run: ./scripts/avro-compat-check.sh --update-shape-lock "
            + $"(expected {lockPath}).");

        // Compare the live-derived fingerprint against the committed snapshot, both canonicalised
        // (object keys recursively sorted, whitespace stripped) so emit-order / formatting differences
        // never produce a false mismatch — only a genuine STRUCTURAL difference does.
        var committed = Canonicalise(File.ReadAllText(lockPath));
        var live = Canonicalise(fingerprint.GetRawText());

        Assert.True(
            committed == live,
            $"Shape-lock DRIFT for {subject} ({relativeSchemaPath}). If this structural change is "
            + "intentional, re-lock it in this PR: ./scripts/avro-compat-check.sh --update-shape-lock "
            + "(and confirm §P3 compatibility at the subject's effective level).\n"
            + $"  committed: {committed}\n  live:      {live}");
    }

    // ---- The canonical fingerprint — a faithful C# port of the shell gate's jq filter. -------------

    /// <summary>
    /// Build the doc-insensitive structural fingerprint of a record schema: namespace, name, subject,
    /// and the ordered fields (name + normalised type + optional flag + default-presence). Mirrors
    /// <c>shape_fingerprint()</c> in <c>scripts/avro-compat-check.sh</c> so the gate and this test agree.
    /// </summary>
    private static JsonElement ShapeFingerprint(JsonElement record)
    {
        var ns = record.GetProperty("namespace").GetString()!;
        var name = record.GetProperty("name").GetString()!;

        var fields = new List<object>();
        foreach (var field in record.GetProperty("fields").EnumerateArray())
        {
            var type = field.GetProperty("type");
            fields.Add(new
            {
                name = field.GetProperty("name").GetString(),
                type = TypeFingerprint(type),
                optional = type.ValueKind == JsonValueKind.Array
                           && type[0].ValueKind == JsonValueKind.String
                           && type[0].GetString() == "null",
                has_default = field.TryGetProperty("default", out _),
            });
        }

        var fingerprint = new
        {
            subject = $"{ns}.{name}-value",
            @namespace = ns,   // emits the JSON key "namespace" (the snapshot's key)
            name,
            fields,
        };

        var json = JsonSerializer.Serialize(fingerprint, Canonical);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>
    /// Reduce an Avro type node to the stable shape string the snapshot records: a scalar name; a
    /// union as <c>[a,b]</c>; an object as its inner type + <c>@logicalType</c> + an
    /// <c>&lt;items:…&gt;</c> / <c>&lt;values:…&gt;</c> / <c>&lt;rec:…&gt;</c> suffix for
    /// array / map / nested-record. Mirrors the jq <c>typefp</c> recursion exactly.
    /// </summary>
    private static string TypeFingerprint(JsonElement type)
    {
        switch (type.ValueKind)
        {
            case JsonValueKind.String:
                return type.GetString()!;

            case JsonValueKind.Array: // a union
                return "[" + string.Join(",", type.EnumerateArray().Select(TypeFingerprint)) + "]";

            case JsonValueKind.Object:
                var inner = TypeFingerprint(type.GetProperty("type"));
                var logical = type.TryGetProperty("logicalType", out var lt) ? "@" + lt.GetString() : "";
                var complex = type.GetProperty("type").ValueKind == JsonValueKind.String
                    ? type.GetProperty("type").GetString()
                    : null;

                var suffix = "";
                if (complex == "array")
                {
                    suffix = "<items:" + TypeFingerprint(type.GetProperty("items")) + ">";
                }
                else if (complex == "map")
                {
                    suffix = "<values:" + TypeFingerprint(type.GetProperty("values")) + ">";
                }
                else if (complex == "record")
                {
                    var recName = type.TryGetProperty("name", out var rn) ? rn.GetString() : "";
                    var recFields = type.GetProperty("fields").EnumerateArray()
                        .Select(f => f.GetProperty("name").GetString() + ":" + TypeFingerprint(f.GetProperty("type")));
                    suffix = "<rec:" + recName + ":" + string.Join(",", recFields) + ">";
                }

                return inner + logical + suffix;

            default:
                return type.GetRawText();
        }
    }

    // Canonicalise a JSON document for value comparison: recursively sort object keys, drop whitespace.
    // This makes the comparison insensitive to emit order (the jq snapshot sorts keys; the C# anonymous
    // type does not) — only a genuine structural difference survives.
    private static string Canonicalise(string json)
    {
        var node = JsonNode.Parse(json)!;
        return Sort(node).ToJsonString();
    }

    private static JsonNode Sort(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                var sorted = new JsonObject();
                foreach (var kv in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    sorted[kv.Key] = kv.Value is null ? null : Sort(kv.Value.DeepClone());
                }
                return sorted;
            case JsonArray arr:
                var outArr = new JsonArray();
                foreach (var item in arr)
                {
                    outArr.Add(item is null ? null : Sort(item.DeepClone()));
                }
                return outArr;
            default:
                return node.DeepClone();
        }
    }

    private static string AvroDir() => Path.Combine(RepoRoot(), "contracts", "avro");

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
