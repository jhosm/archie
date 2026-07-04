using Babelstone.Families.TermDeposit.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// In plain English: the deposit-constitution endpoint declares its body fields non-nullable, but the JSON
/// deserializer will happily bind a MISSING field to null (or 0 for a number) anyway. Before this guard, a
/// POST that left out the product code, the funding account, or the amount rode straight past the handler
/// and blew up deeper as an opaque 500. These tests pin the new engine-side guard: the unconditionally-
/// required trio — product_id, funding_account, principal_cents — is now checked up front and a missing one
/// comes back as a clean 400 that names every offending field.
///
/// bd babelstone-ax0b.8, mirroring <c>DeployRateSheetEndpoint.ValidateEnvelope</c>'s
/// <c>DeployEnvelopeGuardTests</c>. Hermetic — <see cref="DepositsEndpoints.ValidateConstituteEnvelope"/>
/// is a pure function, so no HTTP stack and no database. CRITICAL SCOPE: only the trio is required; the
/// structural quartet (term_days / start_date / interest_variant / auto_renewal_policy) stays OPTIONAL — the
/// engine resolves it from the product config on the minimal saga path (Fork B rework) — so these tests also
/// pin that a trio-only body PASSES.
/// </summary>
public sealed class ConstituteEnvelopeGuardTests
{
    // A minimal, well-formed body: exactly the unconditionally-required trio, no structural quartet — the
    // MINIMAL saga shape the engine resolves the rest of from the product config (Fork B rework).
    private static ConstituteDepositRequest ValidTrioOnly() => new(
        PrincipalCents: 1_000_000,
        ProductId: "dpz_pt_12m_juros_venc",
        FundingAccount: "PT50-DDA-001");

    [Fact]
    public void A_trio_only_body_passes_the_quartet_is_not_required()
    {
        // The structural quartet is deliberately absent — the guard must NOT demand it (the engine resolves
        // it from the product config on the minimal path). A null return means "well-formed, proceed".
        Assert.Null(DepositsEndpoints.ValidateConstituteEnvelope(ValidTrioOnly()));
    }

    [Fact]
    public void A_full_facts_body_also_passes()
    {
        // The explicit full-facts path (a direct caller / the MCP agent) supplies the quartet too — still
        // well-formed, so still a null (pass): the guard only ever adds requirements for the trio.
        var full = ValidTrioOnly() with
        {
            Role = "standard",
            TermDays = 365,
            StartDate = new DateOnly(2026, 1, 15),
            InterestVariant = "AT_MATURITY",
            AutoRenewalPolicy = "NONE",
            PaymentPeriodMonths = 0,
        };
        Assert.Null(DepositsEndpoints.ValidateConstituteEnvelope(full));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_product_id_is_a_400_naming_the_field(string? productId)
    {
        var result = DepositsEndpoints.ValidateConstituteEnvelope(
            ValidTrioOnly() with { ProductId = productId! });

        var problem = Assert.IsType<ValidationProblem>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Contains("product_id", problem.ProblemDetails.Errors.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_funding_account_is_a_400_naming_the_field(string? fundingAccount)
    {
        var result = DepositsEndpoints.ValidateConstituteEnvelope(
            ValidTrioOnly() with { FundingAccount = fundingAccount! });

        var problem = Assert.IsType<ValidationProblem>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Contains("funding_account", problem.ProblemDetails.Errors.Keys);
    }

    [Theory]
    [InlineData(0)]      // the value a missing member binds to
    [InlineData(-1)]
    [InlineData(-1_000_000)]
    public void A_non_positive_principal_is_a_400_naming_the_field(long principalCents)
    {
        var result = DepositsEndpoints.ValidateConstituteEnvelope(
            ValidTrioOnly() with { PrincipalCents = principalCents });

        var problem = Assert.IsType<ValidationProblem>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Contains("principal_cents", problem.ProblemDetails.Errors.Keys);
    }

    [Fact]
    public void Every_missing_trio_field_is_named_at_once()
    {
        // The exact shape a bare `{}` POST binds to: nulls for the strings, 0 for the value-type
        // principal_cents — previously the opaque-500 path. All three trio fields must be named together.
        var empty = new ConstituteDepositRequest(PrincipalCents: 0, ProductId: null!, FundingAccount: null!);

        var problem = Assert.IsType<ValidationProblem>(DepositsEndpoints.ValidateConstituteEnvelope(empty));

        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        var fields = problem.ProblemDetails.Errors.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "funding_account", "principal_cents", "product_id" }, fields);
    }

    [Fact]
    public void A_missing_trio_does_not_drag_the_optional_quartet_into_the_error_set()
    {
        // A bare body is missing the quartet too, but the guard names ONLY the trio — proving the quartet
        // stays optional even when the request is otherwise empty (the structural facts are resolved
        // engine-side, never demanded here).
        var empty = new ConstituteDepositRequest(PrincipalCents: 0, ProductId: null!, FundingAccount: null!);

        var problem = Assert.IsType<ValidationProblem>(DepositsEndpoints.ValidateConstituteEnvelope(empty));

        Assert.DoesNotContain("term_days", problem.ProblemDetails.Errors.Keys);
        Assert.DoesNotContain("start_date", problem.ProblemDetails.Errors.Keys);
        Assert.DoesNotContain("interest_variant", problem.ProblemDetails.Errors.Keys);
        Assert.DoesNotContain("auto_renewal_policy", problem.ProblemDetails.Errors.Keys);
    }
}
