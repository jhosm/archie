using System.Text.Json;
using Xunit;

namespace Babelstone.Notification.Tests;

/// <summary>
/// The CONSUMER half of the NotificationDue emit contract (ADR-PC-025 / ADR-IC-009). In plain English:
/// the customer-communications system that renders and delivers a notification reads a fixed set of
/// fields off the <c>NotificationDue</c> message; this test pins EXACTLY those fields and verifies the
/// governed Avro schema (<c>contracts/avro/operations/NotificationDue.avsc</c>) honours them. A
/// provider-side break — dropping a field the renderer needs, renaming it, removing the SCHEDULED
/// trigger leg, or smuggling a PII field onto the payload — fails THIS build, which is what a
/// consumer-driven contract is for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a hand-rolled Pact-style CDC, not the PactNet FFI.</b> The repo deliberately uses Pact-STYLE
/// consumer-driven contracts pinned in code (see <c>EngineCommandPactConsumerTests</c>) rather than the
/// PactNet native FFI + broker, which is a larger, CI-fragile dependency. So this is the same
/// consumer-drives-the-contract discipline against the single governed artefact both sides share — the
/// <c>.avsc</c>. The provider (the notification scheduler's SCHEDULED emission, ADR-IC-019, and the
/// engine's EVENT_DRIVEN emission, both DEF-2 deferred) verifies the SAME contract object when wired.
/// </para>
/// <para>
/// The verification is against the schema, not a live broker, because the wire shape IS the contract
/// (ADR-IC-002 governs its BACKWARD evolution; the AsyncAPI gate governs no-PII / discoverability). The
/// contract is therefore the field set + types the renderer binds to, declared once in
/// <see cref="ConsumerRequiredFields"/> so a future change is a deliberate, reviewed edit.
/// </para>
/// </remarks>
public sealed class NotificationDuePactConsumerTests
{
    // The fields the communications-system renderer (the consumer) binds to, with the Avro shape it
    // expects (ADR-PC-025 Decision 1 — payload shape). This is the consumer-driven contract: every
    // entry MUST be present in the governed schema with this shape, or rendering would break.
    private static readonly IReadOnlyList<(string Field, AvroShape Shape)> ConsumerRequiredFields =
    [
        ("notification_id", AvroShape.Uuid),         // idempotency / dedupe key (slot 4)
        ("instance_id", AvroShape.Uuid),             // the instance the notice is about
        ("customer_id", AvroShape.Uuid),             // recipient REFERENCE — PII resolved at render time, never on the bus
        ("template_ref", AvroShape.String),          // which pack template to render
        ("template_pack_version", AvroShape.String), // the pinned pack version (ADR-PC-007)
        ("trigger_kind", AvroShape.Enum),            // EVENT_DRIVEN | SCHEDULED | PRE_CONTRACTUAL
        ("causation_id", AvroShape.NullableUuid),    // the causing domain event (null for SCHEDULED)
        ("data", AvroShape.MapOfString),             // structural interpolation values only — no PII
        ("due_at", AvroShape.Date),                  // = valid_time
    ];

    // The trigger_kind symbols the contract requires — notably SCHEDULED, the leg the notification
    // scheduler produces (ADR-PC-025 §6 retains it for the downstream producer; ADR-PC-023).
    private static readonly string[] RequiredTriggerKinds = ["EVENT_DRIVEN", "SCHEDULED", "PRE_CONTRACTUAL"];

    // Identity-attribute name fragments that must NEVER appear as a payload field (the repo
    // never-PII-on-the-durable-bus rule, ADR-PC-004 §P2 / ADR-PC-025 Decision 1). The renderer resolves
    // PII at render time by customer_id reference; an opaque *_id / *_ref reference is allowed.
    private static readonly string[] PiiFieldFragments =
        ["nif", "iban", "name", "email", "phone", "address", "tax_id"];

    private enum AvroShape { String, Uuid, NullableUuid, Date, Enum, MapOfString }

    [Fact]
    public void Governed_schema_honours_the_consumer_contract()
    {
        var fields = SchemaFields();

        var violations = new List<string>();
        foreach (var (field, shape) in ConsumerRequiredFields)
        {
            if (!fields.TryGetValue(field, out var type))
            {
                violations.Add($"missing required field '{field}'");
                continue;
            }

            if (!ShapeMatches(type, shape))
            {
                violations.Add($"field '{field}' is not the expected {shape} shape (got {type.GetRawText()})");
            }
        }

        Assert.True(
            violations.Count == 0,
            "ADR-PC-025 / ADR-IC-009 consumer contract: the governed NotificationDue schema must honour "
            + "every field the communications-system renderer binds to. Offending:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void Trigger_kind_retains_the_scheduled_leg_the_scheduler_produces()
    {
        var triggerType = SchemaFields()["trigger_kind"];
        var symbols = triggerType.GetProperty("symbols").EnumerateArray().Select(s => s.GetString()).ToHashSet();

        foreach (var required in RequiredTriggerKinds)
        {
            Assert.True(
                symbols.Contains(required),
                $"ADR-PC-025 §6: trigger_kind must retain '{required}'. SCHEDULED is the leg the "
                + "notification scheduler produces (ADR-PC-023 / ADR-IC-019); dropping it breaks the "
                + "downstream producer. Symbols: " + string.Join(", ", symbols));
        }
    }

    [Fact]
    public void No_payload_field_carries_pii()
    {
        // The contract resolves the subject's PII (name/NIF/contact) at render time by customer_id
        // reference — never on the bus (ADR-PC-004 §P2). An opaque *_id / *_ref reference is allowed.
        var violations = new List<string>();
        foreach (var field in SchemaFields().Keys)
        {
            var lowered = field.ToLowerInvariant();
            if (lowered.EndsWith("_id", StringComparison.Ordinal) || lowered.EndsWith("_ref", StringComparison.Ordinal))
            {
                continue;
            }

            var fragment = PiiFieldFragments.FirstOrDefault(f => lowered.Contains(f, StringComparison.Ordinal));
            if (fragment is not null)
            {
                violations.Add($"{field} (PII fragment '{fragment}')");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Never-PII-on-the-durable-bus (ADR-PC-004 §P2 / ADR-PC-025 Decision 1): no NotificationDue "
            + "payload field may carry PII — the renderer resolves it by customer_id reference. Offending "
            + "fields:\n  " + string.Join("\n  ", violations));
    }

    // ---- Schema access -----------------------------------------------------------------------------

    // field name -> its Avro `type` JSON node (a string, an object, or a [null, T] union array).
    private static IReadOnlyDictionary<string, JsonElement> SchemaFields()
    {
        var schemaPath = Path.Combine(RepoRoot(), "contracts", "avro", "operations", "NotificationDue.avsc");
        Assert.True(File.Exists(schemaPath), $"governed NotificationDue schema not found on disk: {schemaPath}");

        using var doc = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in doc.RootElement.GetProperty("fields").EnumerateArray())
        {
            // Clone so the element stays valid after the JsonDocument is disposed.
            fields[field.GetProperty("name").GetString()!] = field.GetProperty("type").Clone();
        }

        return fields;
    }

    private static bool ShapeMatches(JsonElement type, AvroShape shape) => shape switch
    {
        AvroShape.String => IsScalar(type, "string") && !HasLogicalType(type),
        AvroShape.Uuid => IsLogical(type, "string", "uuid"),
        AvroShape.NullableUuid => IsNullableUnion(type, out var inner) && IsLogical(inner, "string", "uuid"),
        AvroShape.Date => IsLogical(type, "int", "date"),
        AvroShape.Enum => type.ValueKind == JsonValueKind.Object
                          && type.TryGetProperty("type", out var et) && et.GetString() == "enum",
        AvroShape.MapOfString => type.ValueKind == JsonValueKind.Object
                                 && type.TryGetProperty("type", out var mt) && mt.GetString() == "map"
                                 && type.TryGetProperty("values", out var mv) && mv.GetString() == "string",
        _ => false,
    };

    private static bool IsScalar(JsonElement type, string name)
        => (type.ValueKind == JsonValueKind.String && type.GetString() == name)
           || (type.ValueKind == JsonValueKind.Object && type.TryGetProperty("type", out var t)
               && t.GetString() == name);

    private static bool HasLogicalType(JsonElement type)
        => type.ValueKind == JsonValueKind.Object && type.TryGetProperty("logicalType", out _);

    private static bool IsLogical(JsonElement type, string baseType, string logical)
        => type.ValueKind == JsonValueKind.Object
           && type.TryGetProperty("type", out var t) && t.GetString() == baseType
           && type.TryGetProperty("logicalType", out var l) && l.GetString() == logical;

    // A nullable Avro union is the array [ "null", T ] — "null" FIRST (ADR-IC-002 §P2). Yields T.
    private static bool IsNullableUnion(JsonElement type, out JsonElement inner)
    {
        inner = default;
        if (type.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var branches = type.EnumerateArray().ToList();
        if (branches.Count != 2 || branches[0].ValueKind != JsonValueKind.String || branches[0].GetString() != "null")
        {
            return false;
        }

        inner = branches[1];
        return true;
    }

    // Walk up to the repo root (the directory holding the governed contracts tree).
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !File.Exists(Path.Combine(dir.FullName, "contracts", "avro", "operations", "NotificationDue.avsc")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"repo root (containing contracts/avro/operations/NotificationDue.avsc) not found from {AppContext.BaseDirectory}");
    }
}
