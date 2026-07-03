using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Babelstone.ContractDrift.Tests;

/// <summary>
/// The wire-level shape of a C# DTO, derived by reflection HONOURING System.Text.Json
/// semantics exactly as the hosts configure them (bd babelstone-ax0b.4 Layer 1):
/// <list type="bullet">
/// <item>property NAME = <c>[JsonPropertyName]</c> when present, else the hosts'
/// <c>JsonNamingPolicy.SnakeCaseLower</c> (Babelstone.Engine.Api / RateSheets.Api /
/// orchestrator EdgeServices all pin that policy);</item>
/// <item>NULLABILITY via <see cref="NullabilityInfoContext"/> (NRT annotations for reference
/// types, <c>Nullable&lt;T&gt;</c> for value types);</item>
/// <item>REQUIREDNESS is the repo's boundary-contract convention (ADR-PC-021 §D5), not STJ
/// strictness (STJ never throws on a missing positional record parameter): a RESPONSE
/// property is required iff non-nullable (the serializer always writes it); a REQUEST
/// property is required iff non-nullable AND without a primary-constructor default (a
/// defaulted parameter is an optional field the handler backfills).</item>
/// </list>
/// </summary>
public sealed record WireShape(
    IReadOnlyDictionary<string, WireProperty> Properties,
    IReadOnlySet<string> Required)
{
    public static WireShape OfResponse(Type dto) => Of(dto, isRequest: false);

    public static WireShape OfRequest(Type dto) => Of(dto, isRequest: true);

    private static WireShape Of(Type dto, bool isRequest)
    {
        var nullability = new NullabilityInfoContext();
        var constructorParameters = dto.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault()?.GetParameters() ?? [];

        var properties = new Dictionary<string, WireProperty>(StringComparer.Ordinal);
        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in dto.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always })
            {
                continue;
            }

            var wireName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name);

            var isNullable = IsNullable(property, nullability);
            properties[wireName] = new WireProperty(wireName, OpenApiTypeOf(property.PropertyType));

            var hasDefault = constructorParameters
                .FirstOrDefault(p => string.Equals(p.Name, property.Name, StringComparison.OrdinalIgnoreCase))
                ?.HasDefaultValue ?? false;
            if (!isNullable && (!isRequest || !hasDefault))
            {
                required.Add(wireName);
            }
        }

        return new WireShape(properties, required);
    }

    private static bool IsNullable(PropertyInfo property, NullabilityInfoContext nullability)
    {
        if (Nullable.GetUnderlyingType(property.PropertyType) is not null)
        {
            return true;
        }

        return !property.PropertyType.IsValueType
            && nullability.Create(property).ReadState != NullabilityState.NotNull;
    }

    // The DTO type's OpenAPI scalar/category. Mirrors what the hosts' STJ options emit —
    // Guid/DateOnly/DateTimeOffset serialize as strings, integral numbers as integer,
    // enumerables as array, dictionaries and everything else object-shaped as object.
    private static string OpenApiTypeOf(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string) || t == typeof(Guid) || t == typeof(DateOnly) || t == typeof(DateTimeOffset) || t == typeof(DateTime))
        {
            return "string";
        }

        if (t == typeof(bool))
        {
            return "boolean";
        }

        if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte))
        {
            return "integer";
        }

        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal))
        {
            return "number";
        }

        if (typeof(IDictionary).IsAssignableFrom(t)
            || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
        {
            return "object";
        }

        if (t != typeof(string) && typeof(IEnumerable).IsAssignableFrom(t))
        {
            return "array";
        }

        return "object";
    }
}

/// <summary>One wire property: its snake_case (or attribute-pinned) name and OpenAPI type.</summary>
public sealed record WireProperty(string Name, string Type);
