using System.Text.RegularExpressions;
using Xunit;
using YamlDotNet.Serialization;

namespace Babelstone.ContractDrift.Tests;

/// <summary>
/// LAYER 2 of the drift guard (bd babelstone-ax0b.4): the public write path's documented
/// required set must be CONSISTENT with what the edge actually enforces. Kong CE has no
/// request-validator plugin, so the constitute route's body validation is a pre-function Lua
/// chunk in infra/kong/kong.yml (the deposits-constitute route) — this suite extracts the
/// <c>body.&lt;field&gt;</c> checks from that committed chunk and asserts the
/// orchestrator-edge spec's ConstituteRequest documents exactly those fields, with exactly
/// the Kong-required ones marked required. Editing the pre-function's field set without the
/// spec (or vice versa) turns the build RED. Hermetic: reads the committed kong.yml, no
/// gateway runs.
/// </summary>
public sealed class Layer2KongValidatedFieldsTests
{
    private const string KongConfig = "infra/kong/kong.yml";
    private const string EdgeSpec = "contracts/openapi/specs/orchestrator-edge.openapi.yaml";
    private const string ConstituteRouteName = "deposits-constitute";

    [Fact]
    public void Constitute_spec_required_set_equals_kong_validated_fields()
    {
        var lua = ConstituteAccessLua();

        // Every body.<field> the pre-function touches; a field guarded with `~= nil and`
        // is validated-when-present (optional), the rest are hard requirements.
        var all = Regex.Matches(lua, @"body\.(\w+)").Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var optional = Regex.Matches(lua, @"body\.(\w+)\s*~=\s*nil\s*and").Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var required = all.Except(optional).ToHashSet(StringComparer.Ordinal);

        Assert.True(required.Count > 0,
            $"{KongConfig}: no required body.<field> checks found on the {ConstituteRouteName} pre-function — "
            + "the extraction regex no longer matches the committed Lua; fix the extractor, don't skip the layer.");

        var schema = OpenApiSpec.Load(EdgeSpec).Schema("ConstituteRequest");

        var specRequired = schema.Required.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var kongRequired = required.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.True(specRequired.SequenceEqual(kongRequired, StringComparer.Ordinal),
            $"{EdgeSpec} ConstituteRequest.required != the fields Kong's {ConstituteRouteName} pre-function requires — "
            + $"spec-only [{string.Join(", ", specRequired.Except(kongRequired))}], "
            + $"kong-only [{string.Join(", ", kongRequired.Except(specRequired))}]");

        var specNames = schema.Properties.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var kongNames = all.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.True(specNames.SequenceEqual(kongNames, StringComparer.Ordinal),
            $"{EdgeSpec} ConstituteRequest properties != the fields Kong's {ConstituteRouteName} pre-function validates — "
            + $"spec-only [{string.Join(", ", specNames.Except(kongNames))}], "
            + $"kong-only [{string.Join(", ", kongNames.Except(specNames))}]");
    }

    // The concatenated pre-function `config.access` Lua of the deposits-constitute route. The
    // SCA/attestation chunk on the same route reads headers/claims, never body.<field>, so
    // concatenating all access chunks is safe — the body checks are the only body.* citations.
    private static string ConstituteAccessLua()
    {
        var deserializer = new DeserializerBuilder().Build();
        var kong = deserializer.Deserialize<Dictionary<object, object>>(
            File.ReadAllText(Path.Combine(TestRepo.Root, KongConfig.Replace('/', Path.DirectorySeparatorChar))))!;

        foreach (var serviceNode in (List<object>)kong["services"])
        {
            var service = (Dictionary<object, object>)serviceNode;
            if (!service.TryGetValue("routes", out var routes))
            {
                continue;
            }

            foreach (var routeNode in (List<object>)routes)
            {
                var route = (Dictionary<object, object>)routeNode;
                if (!string.Equals((string?)route["name"], ConstituteRouteName, StringComparison.Ordinal)
                    || !route.TryGetValue("plugins", out var plugins))
                {
                    continue;
                }

                var chunks = new List<string>();
                foreach (var pluginNode in (List<object>)plugins)
                {
                    var plugin = (Dictionary<object, object>)pluginNode;
                    if (!string.Equals((string?)plugin["name"], "pre-function", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var config = (Dictionary<object, object>)plugin["config"];
                    if (config.TryGetValue("access", out var access))
                    {
                        chunks.AddRange(((List<object>)access).Select(c => (string)c));
                    }
                }

                Assert.True(chunks.Count > 0,
                    $"{KongConfig}: route {ConstituteRouteName} has no pre-function access chunk — "
                    + "the edge body validation moved; update this suite's extractor with it.");
                return string.Join("\n", chunks);
            }
        }

        throw new InvalidOperationException($"{KongConfig}: route {ConstituteRouteName} not found");
    }
}
