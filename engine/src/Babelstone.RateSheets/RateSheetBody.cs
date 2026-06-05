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
    private readonly IReadOnlyList<RateBand> _bands = [];

    /// <summary>
    /// The ordered bands. Read-only to match the documented immutability of a published
    /// sheet (ADR-PC-008 §P1: once published, never edited); the band sequence is set at
    /// construction (deserialize or author code) and never mutated thereafter.
    /// </summary>
    public IReadOnlyList<RateBand> Bands
    {
        get => _bands;
        init => _bands = value ?? [];
    }
}

/// <summary>
/// One principal band: a TAN (in basis points) that applies to principals within a
/// <c>[From, To)</c> cent range. The wire form is the <c>[lower, upper]</c>
/// <c>principal_cents</c> array (upper is <c>null</c> for the open-ended top band),
/// preserved 1:1 by <see cref="RateBandJsonConverter"/>. The type is correct-by-construction:
/// a malformed range (not exactly <c>[lower, upper]</c>, a null/negative lower, or an upper
/// not above the lower) is rejected at deserialize and cannot be expressed in code, so
/// <see cref="From"/> / <see cref="To"/> are always well-shaped — there is no malformed
/// value for a resolver to read "from 0" by accident (a silent wrong rate is the worst
/// failure here). Cross-band invariants (contiguity, exhaustiveness) still live in
/// <see cref="RateSheetValidator"/>.
/// </summary>
[JsonConverter(typeof(RateBandJsonConverter))]
public sealed class RateBand
{
    /// <summary>
    /// Constructs a well-shaped band. <paramref name="from"/> is the inclusive lower bound in
    /// cents (must be non-negative); <paramref name="to"/> is the exclusive upper bound, or
    /// <c>null</c> for the open-ended top band, and when present must be strictly above
    /// <paramref name="from"/>. Throws <see cref="ArgumentException"/> otherwise — the same shape
    /// rules the wire converter enforces, so an in-code band can never be malformed either.
    /// </summary>
    public RateBand(long from, long? to, int tanBasisPoints)
    {
        if (from < 0)
        {
            throw new ArgumentException(
                $"RateBand lower bound {from} must be >= 0.", nameof(from));
        }

        if (to is { } upper && upper <= from)
        {
            throw new ArgumentException(
                $"RateBand upper bound {upper} must be greater than lower bound {from}.", nameof(to));
        }

        From = from;
        To = to;
        TanBasisPoints = tanBasisPoints;
    }

    /// <summary>Inclusive lower bound in cents.</summary>
    public long From { get; }

    /// <summary>Exclusive upper bound in cents, or <c>null</c> for the open-ended top band.</summary>
    public long? To { get; }

    public int TanBasisPoints { get; }

    /// <summary>True if <paramref name="principalCents"/> falls in <c>[From, To)</c> (To null = unbounded above).</summary>
    public bool Covers(long principalCents) =>
        principalCents >= From && (To is null || principalCents < To.Value);
}

/// <summary>
/// Reads / writes a <see cref="RateBand"/> against its wire shape — a snake_case object
/// <c>{ "principal_cents": [lower, upper], "tan_basis_points": n }</c> — preserving the
/// JSONB round-trip 1:1 with the deployed YAML (ADR-PC-008 §P1, §S3). Shape validation
/// happens here, so a malformed band is rejected at deserialize rather than surfacing as a
/// wrong rate at resolution: <c>principal_cents</c> must be exactly <c>[lower, upper]</c>
/// with a non-null, non-negative lower and an upper that is either <c>null</c> (the
/// open-ended top band) or strictly above the lower. Cross-band invariants (contiguity,
/// exhaustiveness, pack bounds) remain the validator's job — see <see cref="RateSheetValidator"/>.
/// </summary>
public sealed class RateBandJsonConverter : JsonConverter<RateBand>
{
    // Wire property names are fixed (snake_case), not derived from the options' naming
    // policy: this converter owns the band's shape and must read/write the same names the
    // deployed YAML uses regardless of how the enclosing options are configured.
    private const string PrincipalCentsName = "principal_cents";
    private const string TanBasisPointsName = "tan_basis_points";

    public override RateBand Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("RateBand must be a JSON object.");
        }

        long?[]? principalCents = null;
        int? tanBasisPoints = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Unexpected token in RateBand object.");
            }

            var property = reader.GetString();
            reader.Read();
            switch (property)
            {
                case PrincipalCentsName:
                    principalCents = ReadPrincipalCents(ref reader);
                    break;
                case TanBasisPointsName:
                    tanBasisPoints = reader.GetInt32();
                    break;
                default:
                    reader.Skip(); // tolerate (and drop) any unknown field, as object deserialization does
                    break;
            }
        }

        if (principalCents is null)
        {
            throw new JsonException($"RateBand is missing '{PrincipalCentsName}'.");
        }

        if (tanBasisPoints is null)
        {
            throw new JsonException($"RateBand is missing '{TanBasisPointsName}'.");
        }

        // The [lower, upper] shape is validated here so a malformed band never reaches a
        // resolver. The diagnostics mirror the per-band rules the validator used to own.
        if (principalCents.Length != 2)
        {
            throw new JsonException(
                $"{PrincipalCentsName} must have exactly 2 elements [lower, upper], got {principalCents.Length}.");
        }

        if (principalCents[0] is not { } lower)
        {
            throw new JsonException($"{PrincipalCentsName} lower bound (element 0) must not be null.");
        }

        if (lower < 0)
        {
            throw new JsonException($"{PrincipalCentsName} lower bound {lower} must be >= 0.");
        }

        if (principalCents[1] is { } upper && upper <= lower)
        {
            throw new JsonException(
                $"{PrincipalCentsName} upper bound {upper} must be greater than lower bound {lower}.");
        }

        return new RateBand(lower, principalCents[1], tanBasisPoints.Value);
    }

    public override void Write(Utf8JsonWriter writer, RateBand value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName(PrincipalCentsName);
        writer.WriteStartArray();
        writer.WriteNumberValue(value.From);
        if (value.To is { } upper)
        {
            writer.WriteNumberValue(upper);
        }
        else
        {
            writer.WriteNullValue();
        }

        writer.WriteEndArray();
        writer.WriteNumber(TanBasisPointsName, value.TanBasisPoints);
        writer.WriteEndObject();
    }

    private static long?[] ReadPrincipalCents(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"{PrincipalCentsName} must be a JSON array.");
        }

        var elements = new List<long?>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            elements.Add(reader.TokenType switch
            {
                JsonTokenType.Null => (long?)null,
                JsonTokenType.Number => reader.GetInt64(),
                _ => throw new JsonException($"{PrincipalCentsName} elements must be a number or null."),
            });
        }

        return [.. elements];
    }
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
