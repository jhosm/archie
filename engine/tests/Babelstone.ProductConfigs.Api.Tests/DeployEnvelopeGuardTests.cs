using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace Babelstone.ProductConfigs.Api.Tests;

/// <summary>
/// Hermetic (no Docker, no HTTP stack) unit tests for the deploy envelope guard
/// (<see cref="DeployProductConfigEndpoint.ValidateEnvelope"/>): a well-formed envelope passes, and
/// each missing/blank/default field is named in the 400 validation problem. Mirrors the rate-sheet
/// host's <c>DeployEnvelopeGuardTests</c>; runs in the default Docker-free CI job.
/// </summary>
public sealed class DeployEnvelopeGuardTests
{
    [Fact]
    public void A_well_formed_envelope_passes()
    {
        Assert.Null(DeployProductConfigEndpoint.ValidateEnvelope(ProductConfigTestData.ValidRequest()));
    }

    [Fact]
    public void A_blank_version_id_is_named_in_the_400()
    {
        var request = ProductConfigTestData.ValidRequest() with { ProductConfigVersionId = "  " };

        var problem = Assert.IsType<ValidationProblem>(DeployProductConfigEndpoint.ValidateEnvelope(request));
        Assert.Contains("product_config_version_id", problem.ProblemDetails.Errors.Keys);
    }

    [Fact]
    public void A_blank_product_id_is_named_in_the_400()
    {
        var request = ProductConfigTestData.ValidRequest() with { ProductId = "" };

        var problem = Assert.IsType<ValidationProblem>(DeployProductConfigEndpoint.ValidateEnvelope(request));
        Assert.Contains("product_id", problem.ProblemDetails.Errors.Keys);
    }

    [Fact]
    public void A_default_effective_from_is_named_in_the_400()
    {
        var request = ProductConfigTestData.ValidRequest() with { EffectiveFrom = default };

        var problem = Assert.IsType<ValidationProblem>(DeployProductConfigEndpoint.ValidateEnvelope(request));
        Assert.Contains("effective_from", problem.ProblemDetails.Errors.Keys);
    }

    [Fact]
    public void An_empty_body_is_named_in_the_400()
    {
        var request = ProductConfigTestData.ValidRequest() with { Body = new JsonObject() };

        var problem = Assert.IsType<ValidationProblem>(DeployProductConfigEndpoint.ValidateEnvelope(request));
        Assert.Contains("body", problem.ProblemDetails.Errors.Keys);
    }

    [Fact]
    public void Every_missing_field_is_reported_at_once()
    {
        var request = new ProductConfigDeployRequest(
            ProductConfigVersionId: "",
            ProductId: "",
            PackVersion: "",
            EffectiveFrom: default,
            ApprovedBy: "",
            ApprovalRef: "",
            Body: new JsonObject());

        var problem = Assert.IsType<ValidationProblem>(DeployProductConfigEndpoint.ValidateEnvelope(request));
        Assert.Equal(
            new[] { "approval_ref", "approved_by", "body", "effective_from", "pack_version", "product_config_version_id", "product_id" },
            problem.ProblemDetails.Errors.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }
}
