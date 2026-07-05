using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Babelstone.RateSheets;

/// <summary>
/// In plain English: a single row of the product-config deploy registry — one published,
/// immutable generation of a product's configuration, with a registry-issued version id an auditor
/// (and a replay) can point at. This is the v2 registry ADR-PC-009 §A2 named as later work: until it
/// existed, "which product-config generation was this deposit opened under?" was answered by hashing
/// the YAML bytes (the interim content-hash pin, bd babelstone-fk7m.9). Now it is a versioned deploy
/// timeline, exactly like rate sheets.
/// </summary>
/// <remarks>
/// The shape mirrors <see cref="RateSheet"/> (ADR-PC-008): a registry-issued
/// <see cref="ProductConfigVersionId"/>, the product it defines, the pack it was authored against, an
/// <see cref="EffectiveFrom"/> deploy anchor, the config body as JSONB, and the treasury/product
/// approval trail. Once published it is never edited; a correction ships as a new
/// <see cref="ProductConfigVersionId"/> with a later <see cref="EffectiveFrom"/> (ADR-PC-008 §P5
/// forward-only immutability). The body carries NO PII (ADR-PC-004): it is structural configuration
/// (term / interest variant / renewal policy / partial-withdrawal gates), never a depositor fact.
/// </remarks>
/// <param name="ProductConfigVersionId">Registry-issued unique id of this config version — the value
/// a later work item pins on <c>DepositConstituted</c> in place of the interim content hash (ADR-PC-009 §A2).</param>
/// <param name="ProductId">The product code this config version defines (e.g. <c>dpz_pt_12m_juros_venc</c>).</param>
/// <param name="PackVersion">The regulatory pack version the config was authored against.</param>
/// <param name="EffectiveFrom">Instant from which this version is the candidate active config; a correction ships as a new version with a later value.</param>
/// <param name="Body">The structural config body (1:1 with the deployed YAML), stored as JSONB.</param>
/// <param name="ContentHash">The <c>sha256:&lt;hex&gt;</c> of the canonical body — bridges the interim
/// content-hash pin (bd babelstone-fk7m.9) so a registry version id still resolves to the exact bytes.</param>
/// <param name="ApprovedBy">Who approved the config before publication.</param>
/// <param name="ApprovalRef">Reference to the approval record (audit trail).</param>
/// <param name="PublishedBy">Who published the config (the gateway-authenticated deploy actor).</param>
/// <param name="PublishedAt">Set by the database default (<c>clock_timestamp()</c>) at insert; null on a
/// not-yet-stored version and populated on read-back.</param>
public sealed record ProductConfigVersion(
    string ProductConfigVersionId,
    string ProductId,
    string PackVersion,
    DateTimeOffset EffectiveFrom,
    JsonObject Body,
    string ContentHash,
    string ApprovedBy,
    string ApprovalRef,
    string PublishedBy,
    DateTimeOffset? PublishedAt = null);

/// <summary>
/// The config version resolved as active at a constitution instant (mirrors
/// <see cref="RateSheetResolution"/>, ADR-PC-008). Carries the <see cref="ProductConfigVersionId"/> to
/// pin on <c>DepositConstituted</c> (the ADR-PC-009 §A2 registry-issued pin) and the
/// <see cref="ContentHash"/> that bridges to the interim content-hash pin, so a replay can prove which
/// product-config generation a deposit was constituted under from the registry-issued id alone.
/// </summary>
public sealed record ProductConfigVersionResolution(
    string ProductConfigVersionId,
    string ContentHash,
    JsonObject Body);

/// <summary>
/// Raised when an insert collides with an existing config version — either the
/// <c>product_config_version_id</c> primary key or the <c>(product_id, effective_from)</c> unique key.
/// The deploy endpoint re-reads and applies the ADR-PC-008 rule: an identical body under the same
/// version id is idempotent success; anything else is a conflict (mirrors
/// <see cref="DuplicateRateSheetVersionException"/>).
/// </summary>
public sealed class DuplicateProductConfigVersionException : Exception
{
    public DuplicateProductConfigVersionException(string productConfigVersionId, Exception? inner = null)
        : base($"A product config conflicting with version id '{productConfigVersionId}' already exists.", inner)
        => ProductConfigVersionId = productConfigVersionId;

    public string ProductConfigVersionId { get; }
}

/// <summary>
/// JSON handling for the product-config registry body (mirrors <see cref="RateSheetJson"/>): the
/// snake_case options the stored JSONB round-trips through, plus a canonical string form for the
/// ADR-PC-008 idempotency comparison and the content-hash the registry stamps (ADR-PC-009 §A2 bridge).
/// </summary>
public static class ProductConfigJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>
    /// A canonical string form of a config body for idempotency comparison (ADR-PC-008: "identical
    /// body under an existing version id is 200, different is 409"). Object keys are sorted so the
    /// comparison is independent of author key order and of the reordering PostgreSQL applies when it
    /// normalises JSONB on read-back. Array order is preserved and therefore significant.
    /// </summary>
    public static string Canonical(JsonObject body)
    {
        var canonical = CanonicalNode(body.DeepClone());
        return (canonical ?? new JsonObject()).ToJsonString();
    }

    /// <summary>
    /// The <c>sha256:&lt;hex&gt;</c> content hash of the canonical body — the same self-describing
    /// form <c>YamlProductConfigStore</c> mints from the raw YAML bytes (bd babelstone-fk7m.9). Here it
    /// is computed from the CANONICAL JSON so it is stable across key-order and JSONB normalisation,
    /// letting the registry version id resolve back to a deterministic content identity.
    /// </summary>
    public static string ContentHash(JsonObject body) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(body))));

    private static JsonNode? CanonicalNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var sorted = new JsonObject();
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    sorted[pair.Key] = CanonicalNode(pair.Value?.DeepClone());
                }

                return sorted;
            case JsonArray array:
                var copy = new JsonArray();
                foreach (var item in array)
                {
                    copy.Add(CanonicalNode(item?.DeepClone()));
                }

                return copy;
            default:
                return node?.DeepClone();
        }
    }
}
