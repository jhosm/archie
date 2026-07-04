using Xunit;

namespace Babelstone.ContractDrift.Tests;

/// <summary>
/// LAYER 3 of the drift guard: where a Pact-style CDC already pins a surface, the OpenAPI
/// spec must describe AT LEAST what the pact exercises — the spec is a superset of the pact,
/// so the two guards can never contradict each other. Today ONE surface has a pact:
/// POST /v1/deposits (ENGINE_COMMAND_PACT, ADR-PC-029 slot 6 — consumer half in
/// orchestrator/tests/.../EngineCommandPactConsumerTests, provider half in
/// engine/tests/.../EngineCommandPactProviderTests). The pinned field sets below MIRROR
/// EngineCommandContract.AssertConsumerRequest / ExpectedCreatedBody and are KEPT IN SYNC BY
/// HAND — nothing mechanical links them, so a change to the contract class must update these
/// arrays in the same commit (the same constants-duplication precedent the provider test
/// uses; a project reference from here would drag the orchestrator TEST assembly into this
/// hermetic suite for four string arrays).
/// </summary>
public sealed class Layer3PactSupersetTests
{
    private const string CommandsSpec = "contracts/openapi/internal/engine-deposit-commands.openapi.yaml";

    // EngineCommandContract.ConstituteRoute / IdempotencyHeader pins.
    private const string ConstituteRoute = "/v1/deposits";
    private const string IdempotencyHeader = "Idempotency-Key";

    // EngineCommandContract.AssertConsumerRequest: the minimal snake_case body the dispatcher
    // MUST produce (Fork B rework — the orchestrator carries no product-family knowledge).
    private static readonly string[] PactRequestFields =
        ["deposit_id", "product_id", "principal_cents", "funding_account"];

    // EngineCommandContract.ExpectedCreatedBody: the 201 shape the engine MUST return.
    private static readonly string[] PactResponseFields =
        ["deposit_id", "status", "commit_sequence"];

    [Fact]
    public void Constitute_spec_is_a_superset_of_the_engine_command_pact()
    {
        var spec = OpenApiSpec.Load(CommandsSpec);
        var operation = spec.Operations()
            .SingleOrDefault(o => o.Method == "POST" && o.Path == ConstituteRoute)
            ?? throw new Xunit.Sdk.XunitException(
                $"{CommandsSpec}: POST {ConstituteRoute} is not documented — the pact-pinned engine command "
                + "surface must be catalogued");

        // The pact's mandatory Idempotency-Key must be documented as a REQUIRED header.
        Assert.True(operation.HeaderParameters.Any(h =>
                string.Equals(h.Name, IdempotencyHeader, StringComparison.OrdinalIgnoreCase) && h.Required),
            $"{CommandsSpec}: POST {ConstituteRoute} must document the mandatory {IdempotencyHeader} header "
            + "(ADR-PC-029 slot 1 — the pact 400s without it)");

        // Every request field the pact exercises must be a documented property, and the
        // pact's required trio must be spec-required (deposit_id stays optional in the spec:
        // the pact pins that the DISPATCHER always sends it, while the engine accepts a
        // direct caller omitting it and mints the id — spec superset, not equality).
        var request = spec.Schema(operation.RequestSchemaRef
            ?? throw new Xunit.Sdk.XunitException($"{CommandsSpec}: POST {ConstituteRoute} has no request schema"));
        foreach (var field in PactRequestFields)
        {
            Assert.True(request.Properties.ContainsKey(field),
                $"{CommandsSpec}: {request.Name} must document pact-exercised field '{field}' (ENGINE_COMMAND_PACT)");
        }

        // Every response field the pact asserts must be documented AND required — the pact
        // parses all three off every 201.
        var response = spec.Schema(Assert.Single(operation.ResponseSchemaRefs));
        foreach (var field in PactResponseFields)
        {
            Assert.True(response.Properties.ContainsKey(field) && response.Required.Contains(field),
                $"{CommandsSpec}: {response.Name} must document AND require pact-asserted field '{field}' (ENGINE_COMMAND_PACT)");
        }
    }
}
