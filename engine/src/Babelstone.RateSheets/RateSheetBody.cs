using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Babelstone.RateSheets;

/// <summary>
/// The numerical body of a rate sheet (ADR-PC-008 §P1, surface §2.2): a map of
/// <c>product_id -&gt; role -&gt; ordered principal bands</c>. Stored as JSONB, 1:1 with
/// the deployed YAML. The envelope fields (version id, family, pack, effective_from,
/// approver) are columns on <see cref="RateSheet"/>, never part of the body — the
/// body is exactly the priceable data a treasury author edits.
/// </summary>
public sealed class RateSheetBody
{
    /// <summary>Product id (e.g. <c>dpz_pt_12m_juros_venc</c>) -&gt; role (e.g. <c>standard</c>, <c>new_money</c>) -&gt; bands.</summary>
    public Dictionary<string, Dictionary<string, RoleRates>> Products { get; init; } = [];
}

/// <summary>The ordered principal bands offered to one (product, role).</summary>
public sealed class RoleRates
{
    public List<RateBand> Bands { get; init; } = [];
}

/// <summary>
/// One principal band: a TAN (in basis points) that applies to principals within a
/// <c>[from, to)</c> cent range. <see cref="PrincipalCents"/> is the raw <c>[lower, upper]</c>
/// wire array (upper is <c>null</c> for the open-ended top band); <see cref="From"/> /
/// <see cref="To"/> interpret it after validation has guaranteed the shape.
/// </summary>
public sealed class RateBand
{
    /// <summary>The <c>[lower, upper]</c> principal range in cents; upper is <c>null</c> for the open-ended top band.</summary>
    public long?[] PrincipalCents { get; init; } = [];

    public int TanBasisPoints { get; init; }

    /// <summary>
    /// Inclusive lower bound in cents. Throws if the band is malformed (not exactly
    /// <c>[lower, upper]</c> with a non-null lower) rather than coercing to a plausible-but-wrong
    /// <c>0</c>: the validator accepts only well-shaped bands, and the stored JSONB is immutable,
    /// so reaching this accessor on a malformed band means a corrupt row — which must fail loud
    /// (a silent wrong rate is the worst failure here), not resolve every principal "from 0".
    /// </summary>
    [JsonIgnore]
    public long From => PrincipalCents is [{ } lower, _]
        ? lower
        : throw new InvalidOperationException(
            "RateBand.principal_cents must be [lower, upper] with a non-null lower bound (the band failed validation).");

    /// <summary>Exclusive upper bound in cents, or <c>null</c> for the open-ended top band. Throws on a malformed band (see <see cref="From"/>).</summary>
    [JsonIgnore]
    public long? To => PrincipalCents is [_, var upper]
        ? upper
        : throw new InvalidOperationException(
            "RateBand.principal_cents must have exactly two elements [lower, upper] (the band failed validation).");

    /// <summary>True if <paramref name="principalCents"/> falls in <c>[From, To)</c> (To null = unbounded above).</summary>
    public bool Covers(long principalCents) =>
        principalCents >= From && (To is null || principalCents < To.Value);
}

/// <summary>
/// The single <see cref="JsonSerializerOptions"/> for rate-sheet bodies. snake_case to
/// match the deployed YAML (<c>principal_cents</c>, <c>tan_basis_points</c>); dictionary
/// keys (product ids, role names) are data and stay verbatim. Used on both the JSONB
/// write path and the read-back, so the round-trip is symmetric.
/// </summary>
public static class RateSheetJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>
    /// A canonical string form of a body for idempotency comparison (ADR-PC-008 §P2:
    /// "identical body under an existing version id is 200, different is 409"). Object
    /// keys are sorted so the comparison is independent of author key order and of the
    /// reordering PostgreSQL applies when it normalises JSONB on read-back. Array order
    /// (the band sequence) is preserved and therefore significant.
    /// </summary>
    public static string Canonical(RateSheetBody body)
    {
        // SerializeToNode of a non-null body is always a JsonObject; CanonicalNode of a
        // JsonObject is non-null. The empty-object fallback keeps the compiler's
        // nullability analysis honest without a null-forgiving operator.
        var canonical = CanonicalNode(JsonSerializer.SerializeToNode(body, Options));
        return (canonical ?? new JsonObject()).ToJsonString();
    }

    private static JsonNode? CanonicalNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var sorted = new JsonObject();
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    sorted[pair.Key] = CanonicalNode(pair.Value);
                }

                return sorted;
            case JsonArray array:
                var copy = new JsonArray();
                foreach (var item in array)
                {
                    copy.Add(CanonicalNode(item));
                }

                return copy;
            default:
                return node?.DeepClone();
        }
    }
}
