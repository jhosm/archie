using Xunit;

namespace Babelstone.ContractDrift.Tests;

/// <summary>
/// The self-policing closure over the drift guard (bd babelstone-ax0b.4 acceptance: one
/// Layer-1 case per documented surface): EVERY operation in EVERY committed spec must have
/// its request and 2xx-response schemas covered by a Layer-1 case, unless it is exempt —
/// <c>x-sse-stream: true</c> (streams frames, no JSON body to bind; the ADR-IC-020 SSE
/// exemption) or the provisional engine-sor-ops envelope (0.x, no shipped DTO; pinned by
/// Layer1's provisional guard). Documenting a NEW operation without adding its drift case
/// fails HERE, so the catalogue can never quietly outgrow the guard.
/// </summary>
public sealed class SpecOperationCoverageTests
{
    // Every catalogued spec (public + internal). Adding a spec file without listing it here
    // fails the sweep below.
    private static readonly string[] Specs =
    [
        "contracts/openapi/specs/engine-reads.openapi.yaml",
        "contracts/openapi/specs/engine-sor-ops.openapi.yaml",
        "contracts/openapi/specs/orchestrator-edge.openapi.yaml",
        "contracts/openapi/specs/orchestrator-process-status.openapi.yaml",
        "contracts/openapi/internal/engine-deposit-commands.openapi.yaml",
        "contracts/openapi/internal/engine-rate-sheets.openapi.yaml",
        "contracts/openapi/internal/engine-pack-migrations.openapi.yaml",
    ];

    // The provisional-envelope exemption (see Layer1's Sor_ops guard).
    private const string ProvisionalSpec = "contracts/openapi/specs/engine-sor-ops.openapi.yaml";

    [Fact]
    public void Every_committed_spec_file_is_swept()
    {
        var onDisk = new[] { "specs", "internal" }
            .SelectMany(dir =>
            {
                var abs = Path.Combine(TestRepo.Root, "contracts", "openapi", dir);
                return Directory.Exists(abs)
                    ? Directory.EnumerateFiles(abs, "*.openapi.yaml")
                        .Select(f => $"contracts/openapi/{dir}/{Path.GetFileName(f)}")
                    : Enumerable.Empty<string>();
            })
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        Assert.True(onDisk.SequenceEqual(Specs.OrderBy(f => f, StringComparer.Ordinal), StringComparer.Ordinal),
            "the committed spec set and this suite's sweep list diverged — "
            + $"on-disk-only [{string.Join(", ", onDisk.Except(Specs))}], "
            + $"listed-only [{string.Join(", ", Specs.Except(onDisk))}] "
            + "(a new spec needs Layer-1 cases and a sweep entry; a removed one needs both dropped)");
    }

    [Fact]
    public void Every_operation_schema_has_a_layer1_case_or_a_named_exemption()
    {
        // (spec, schema) pairs Layer 1 binds to a DTO.
        var covered = new HashSet<(string Spec, string Schema)>();
        foreach (var row in Layer1SpecDtoStructuralTests.Cases())
        {
            covered.Add(((string)row[0], (string)row[1]));
        }

        foreach (var specPath in Specs)
        {
            var spec = OpenApiSpec.Load(specPath);
            foreach (var operation in spec.Operations())
            {
                if (operation.IsSseStream)
                {
                    continue; // ADR-IC-020 SSE exemption — no JSON body to bind.
                }

                if (string.Equals(specPath, ProvisionalSpec, StringComparison.Ordinal))
                {
                    continue; // provisional 0.x envelope — pinned by the Layer-1 guard.
                }

                if (operation.RequestSchemaRef is { } requestRef)
                {
                    Assert.True(covered.Contains((specPath, requestRef)),
                        $"{specPath}: {operation.Method} {operation.Path} request schema '{requestRef}' has no Layer-1 drift case");
                }

                foreach (var responseRef in operation.ResponseSchemaRefs)
                {
                    Assert.True(covered.Contains((specPath, responseRef)),
                        $"{specPath}: {operation.Method} {operation.Path} 2xx schema '{responseRef}' has no Layer-1 drift case");
                }
            }
        }
    }
}
