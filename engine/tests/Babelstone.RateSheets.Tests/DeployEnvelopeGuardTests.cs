using Babelstone.RateSheets.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace Babelstone.RateSheets.Tests;

/// <summary>
/// Hermetic unit tests for the deploy envelope guard
/// (<see cref="DeployRateSheetEndpoint.ValidateEnvelope"/>): System.Text.Json binds a missing
/// member to null/default despite the record's non-nullable declarations, and before this guard a
/// blank <c>rate_sheet_version_id</c> (or a null <c>products</c>) rode past the handler's checks
/// into a NullReferenceException-shaped 500. The catalogued spec
/// (contracts/openapi/internal/engine-rate-sheets.openapi.yaml) marks every envelope field
/// required, so the host must answer a clean 400 that names the offending fields — these tests
/// pin that. No HTTP stack, no database: the guard is a pure function.
/// </summary>
public sealed class DeployEnvelopeGuardTests
{
    private static RateSheetDeployRequest Valid() => new(
        RateSheetVersionId: "rs-2026-02",
        ProductFamily: "term_deposit",
        PackVersion: "pt.2026.1",
        EffectiveFrom: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
        ApprovedBy: "treasury:alm-committee",
        ApprovalRef: "ALM-2026-014",
        Products: []);

    [Fact]
    public void A_well_formed_envelope_passes()
    {
        Assert.Null(DeployRateSheetEndpoint.ValidateEnvelope(Valid()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_version_id_is_a_400_naming_the_field(string? versionId)
    {
        var result = DeployRateSheetEndpoint.ValidateEnvelope(Valid() with { RateSheetVersionId = versionId! });

        var problem = Assert.IsType<ValidationProblem>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Contains("rate_sheet_version_id", problem.ProblemDetails.Errors.Keys);
    }

    [Fact]
    public void Every_missing_envelope_field_is_named_at_once()
    {
        // The exact shape a bare `{}` POST binds to: nulls for the strings/dictionary, the
        // default instant for effective_from — previously the NRE-500 path.
        var empty = new RateSheetDeployRequest(null!, null!, null!, default, null!, null!, null!);

        var problem = Assert.IsType<ValidationProblem>(DeployRateSheetEndpoint.ValidateEnvelope(empty));

        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        var fields = problem.ProblemDetails.Errors.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[]
            {
                "approval_ref", "approved_by", "effective_from",
                "pack_version", "product_family", "products", "rate_sheet_version_id",
            },
            fields);
    }

    [Theory]
    [InlineData("product_family")]
    [InlineData("pack_version")]
    [InlineData("approved_by")]
    [InlineData("approval_ref")]
    public void Each_blank_string_field_is_rejected_individually(string field)
    {
        var request = field switch
        {
            "product_family" => Valid() with { ProductFamily = " " },
            "pack_version" => Valid() with { PackVersion = " " },
            "approved_by" => Valid() with { ApprovedBy = " " },
            _ => Valid() with { ApprovalRef = " " },
        };

        var problem = Assert.IsType<ValidationProblem>(DeployRateSheetEndpoint.ValidateEnvelope(request));
        Assert.Equal([field], problem.ProblemDetails.Errors.Keys);
    }

    [Fact]
    public void A_null_products_body_is_rejected_not_an_NRE()
    {
        var problem = Assert.IsType<ValidationProblem>(
            DeployRateSheetEndpoint.ValidateEnvelope(Valid() with { Products = null! }));
        Assert.Equal(["products"], problem.ProblemDetails.Errors.Keys);
    }
}
