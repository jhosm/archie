using YamlDotNet.Serialization;

namespace Babelstone.ContractDrift.Tests;

/// <summary>
/// A minimal, purpose-built reader over a committed <c>*.openapi.yaml</c> file (bd
/// babelstone-ax0b.4). Deliberately NOT a full OpenAPI object model: the drift suite needs
/// exactly three things — the named component schemas (property names / scalar types /
/// required sets), the operations (method, path, request/response schema refs, the
/// <c>x-sse-stream</c> exemption marker, and header parameters), and the <c>info.version</c>.
/// Reading the YAML directly keeps the suite hermetic and pins it to the committed TEXT the
/// governance gate (scripts/openapi-catalog-validate.sh) validates, with no second parser
/// opinion in between (Microsoft.OpenApi's 3.1 support would add a package + a reader whose
/// leniencies we would then have to reason about; YamlDotNet is already the repo's pinned
/// YAML parser).
/// </summary>
public sealed class OpenApiSpec
{
    private readonly Dictionary<object, object> _root;

    /// <summary>The spec path relative to the repo root (for assertion messages).</summary>
    public string RelativePath { get; }

    private OpenApiSpec(string relativePath, Dictionary<object, object> root)
    {
        RelativePath = relativePath;
        _root = root;
    }

    /// <summary>Load a committed spec by repo-root-relative path (cached per path — the suite reads each file once).</summary>
    public static OpenApiSpec Load(string relativePath)
    {
        return Cache.GetOrAdd(relativePath, static rel =>
        {
            var path = Path.Combine(TestRepo.Root, rel.Replace('/', Path.DirectorySeparatorChar));
            var deserializer = new DeserializerBuilder().Build();
            var root = deserializer.Deserialize<Dictionary<object, object>>(File.ReadAllText(path))
                ?? throw new InvalidOperationException($"{rel}: empty YAML document");
            return new OpenApiSpec(rel, root);
        });
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, OpenApiSpec> Cache = new();

    public string InfoVersion =>
        (string?)AsMap(_root["info"])?["version"]
        ?? throw new InvalidOperationException($"{RelativePath}: no info.version");

    /// <summary>The named schema under <c>components.schemas</c>, reduced to the drift-relevant facts.</summary>
    public SpecSchema Schema(string name)
    {
        var schemas = AsMap(AsMap(_root["components"])!["schemas"])
            ?? throw new InvalidOperationException($"{RelativePath}: no components.schemas");
        if (!schemas.TryGetValue(name, out var node))
        {
            throw new InvalidOperationException($"{RelativePath}: no components.schemas.{name}");
        }

        var schema = AsMap(node)!;
        var properties = new Dictionary<string, SpecProperty>(StringComparer.Ordinal);
        if (schema.TryGetValue("properties", out var propsNode))
        {
            foreach (var (key, value) in AsMap(propsNode)!)
            {
                var prop = AsMap(value)!;
                // A $ref / oneOf property has no inline `type`; the drift check treats a $ref
                // as an object (a nested schema gets its own Layer-1 case where reflection is
                // honest) and leaves a oneOf's type unasserted.
                var type = prop.TryGetValue("type", out var t) ? (string?)t
                    : prop.ContainsKey("$ref") ? "object"
                    : null;
                properties[(string)key] = new SpecProperty((string)key, type);
            }
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetValue("required", out var reqNode))
        {
            foreach (var r in (List<object>)reqNode)
            {
                required.Add((string)r);
            }
        }

        return new SpecSchema(name, properties, required);
    }

    /// <summary>Every operation in the document (path, upper-cased method, and the drift-relevant markers).</summary>
    public IReadOnlyList<SpecOperation> Operations()
    {
        var operations = new List<SpecOperation>();
        foreach (var (pathKey, pathNode) in AsMap(_root["paths"])!)
        {
            foreach (var (methodKey, opNode) in AsMap(pathNode)!)
            {
                var method = (string)methodKey;
                if (method is not ("get" or "put" or "post" or "delete" or "patch" or "head" or "options" or "trace"))
                {
                    continue;
                }

                var op = AsMap(opNode)!;
                var sse = op.TryGetValue("x-sse-stream", out var sseNode) && string.Equals((string?)sseNode, "true", StringComparison.OrdinalIgnoreCase);

                var requestSchemaRef = op.TryGetValue("requestBody", out var body)
                    ? SchemaRefOf(AsMap(body)!)
                    : null;

                var responseSchemaRefs = new List<string>();
                if (op.TryGetValue("responses", out var responses))
                {
                    foreach (var (statusKey, responseNode) in AsMap(responses)!)
                    {
                        var status = (string)statusKey;
                        if (status.Length == 3 && status[0] == '2' && SchemaRefOf(AsMap(responseNode)!) is { } schemaRef)
                        {
                            responseSchemaRefs.Add(schemaRef);
                        }
                    }
                }

                var headerParameters = new List<(string Name, bool Required)>();
                if (op.TryGetValue("parameters", out var parameters))
                {
                    foreach (var parameterNode in (List<object>)parameters)
                    {
                        var parameter = AsMap(parameterNode)!;
                        // Resolve a #/components/parameters/X ref to the shared definition.
                        if (parameter.TryGetValue("$ref", out var refNode))
                        {
                            var refName = ((string)refNode!).Split('/')[^1];
                            parameter = AsMap(AsMap(AsMap(_root["components"])!["parameters"])![refName])!;
                        }

                        if (string.Equals((string?)parameter["in"], "header", StringComparison.Ordinal))
                        {
                            var isRequired = parameter.TryGetValue("required", out var req)
                                && string.Equals((string?)req, "true", StringComparison.OrdinalIgnoreCase);
                            headerParameters.Add(((string)parameter["name"], isRequired));
                        }
                    }
                }

                operations.Add(new SpecOperation(
                    (string)pathKey, method.ToUpperInvariant(), sse, requestSchemaRef, responseSchemaRefs, headerParameters));
            }
        }

        return operations;
    }

    // The `#/components/schemas/X` name behind requestBody/response `content.application/json.schema.$ref`.
    private static string? SchemaRefOf(Dictionary<object, object> bodyOrResponse)
    {
        if (!bodyOrResponse.TryGetValue("content", out var content))
        {
            return null;
        }

        foreach (var (_, mediaNode) in AsMap(content)!)
        {
            var media = AsMap(mediaNode)!;
            if (media.TryGetValue("schema", out var schemaNode)
                && AsMap(schemaNode) is { } schema
                && schema.TryGetValue("$ref", out var refNode))
            {
                return ((string)refNode!).Split('/')[^1];
            }
        }

        return null;
    }

    private static Dictionary<object, object>? AsMap(object? node) => node as Dictionary<object, object>;
}

/// <summary>One schema's drift-relevant facts: named properties and the declared required set.</summary>
public sealed record SpecSchema(
    string Name,
    IReadOnlyDictionary<string, SpecProperty> Properties,
    IReadOnlySet<string> Required);

/// <summary>One property: its wire name and its declared scalar type (null when $ref-less oneOf etc.).</summary>
public sealed record SpecProperty(string Name, string? Type);

/// <summary>One operation's drift-relevant facts.</summary>
public sealed record SpecOperation(
    string Path,
    string Method,
    bool IsSseStream,
    string? RequestSchemaRef,
    IReadOnlyList<string> ResponseSchemaRefs,
    IReadOnlyList<(string Name, bool Required)> HeaderParameters);

/// <summary>Repo-root discovery — the same disk-marker walk the OutboxPublisher tests use.</summary>
public static class TestRepo
{
    public static string Root { get; } = Find();

    private static string Find()
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
