using Babelstone.Families.TermDeposit.Application;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// Tests for <see cref="ScaServicePrincipal"/> — the non-interactive, scoped, gateway-attested SCA
/// service principal the ADR-PC-036 lifecycle-command driver presents on the deposit money-mover routes
/// (bd babelstone-6cpq.4; ADR-IC-006 §P2 attest-not-deny / ADR-IC-010 §P8 / ADR-IC-021).
/// </summary>
/// <remarks>
/// In plain English: a machine that fires a deposit's maturity or coupon on its due date cannot do a
/// human strong-authentication step-up — it has no human and no fresh <c>auth_time</c>. So instead it
/// presents a SCOPED credential the bank's gateway attested, which authorises ONLY the maturity + interest
/// routes and nothing else. These tests pin the two load-bearing guarantees: the principal authorises the
/// two money-mover lifecycle routes when it carries the right scope, and it is REFUSED for any other route
/// (a non-money-mover such as terminate) or any caller that does not carry the exact scope — so the
/// fail-closed human-SCA default still governs every other path.
/// </remarks>
public sealed class ScaServicePrincipalTests
{
    private static IHeaderDictionary WithPrincipal(string scopeValue) =>
        new HeaderDictionary { [ScaServicePrincipal.PrincipalHeader] = scopeValue };

    [Theory]
    [InlineData(ScaServicePrincipal.MaturityOperation)]
    [InlineData(ScaServicePrincipal.InterestOperation)]
    public void A_scoped_principal_authorises_the_money_mover_lifecycle_routes(string operation)
    {
        // The exact lifecycle money-mover scope, attested by the gateway, authorises maturity + interest —
        // the two routes the ADR-PC-036 driver fires (no human step-up needed for a machine actor).
        var headers = WithPrincipal(ScaServicePrincipal.LifecycleMoneyMoverScope);

        Assert.True(ScaServicePrincipal.IsAuthorised(headers, operation));
    }

    [Fact]
    public void A_scoped_principal_is_refused_on_a_non_money_mover_route()
    {
        // SCOPE-LIMITED: even WITH a valid principal header, the principal cannot reach a route outside its
        // allowance. terminate (customer-initiated early termination) is human-SCA-only, so the driver's
        // principal is refused there — the "scoped, not blanket" guarantee (ADR-PC-036 §Consequences).
        var headers = WithPrincipal(ScaServicePrincipal.LifecycleMoneyMoverScope);

        Assert.False(ScaServicePrincipal.IsAuthorised(headers, "terminate"));
    }

    [Fact]
    public void The_authorised_operation_set_is_exactly_maturity_and_interest()
    {
        // Lock the scope-limiting set so a future edit cannot silently widen it to terminate or another
        // money-mover (ADR-PC-036 §Decision 1 / Consequences).
        Assert.Equal(
            new[] { ScaServicePrincipal.InterestOperation, ScaServicePrincipal.MaturityOperation },
            ScaServicePrincipal.AuthorisedOperations.OrderBy(o => o, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void A_principal_carrying_the_scope_among_others_is_still_authorised()
    {
        // The OAuth `scope` claim is space-delimited and may carry several scopes; membership of the exact
        // lifecycle money-mover scope is what authorises, so an additional scope does not break recognition.
        var headers = WithPrincipal($"openid {ScaServicePrincipal.LifecycleMoneyMoverScope} deposits:read");

        Assert.True(ScaServicePrincipal.IsAuthorised(headers, ScaServicePrincipal.MaturityOperation));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("lifecycle:deposit-money-mover-typo")]
    [InlineData("deposits:write")]
    [InlineData("lifecycle:deposit")]
    public void A_missing_or_wrong_scope_is_refused_so_the_human_SCA_default_holds(string scopeValue)
    {
        // FAIL-CLOSED: an empty/blank header, a near-miss scope, or an unrelated scope all fail to
        // authorise — so the caller falls back to the human-SCA gate (ScaPrecondition), whose default is
        // 422 SCA_REQUIRED. A partial/substring match of the scope is NOT enough.
        var headers = WithPrincipal(scopeValue);

        Assert.False(ScaServicePrincipal.IsAuthorised(headers, ScaServicePrincipal.MaturityOperation));
    }

    [Fact]
    public void An_absent_principal_header_is_refused()
    {
        // No principal header at all (the human agent / customer flow): not authorised as a principal, so
        // the human-SCA gate governs — the default for every non-principal caller.
        IHeaderDictionary headers = new HeaderDictionary();

        Assert.False(ScaServicePrincipal.IsAuthorised(headers, ScaServicePrincipal.MaturityOperation));
    }

    [Fact]
    public void The_scope_token_matches_the_gateway_attestation_contract()
    {
        // The engine-side scope value MUST equal the Kong route-scoped allowance and the IAM-minted scope
        // (lock-step, ADR-IC-021) — pin the literal so a drift on either side is caught here.
        Assert.Equal("lifecycle:deposit-money-mover", ScaServicePrincipal.LifecycleMoneyMoverScope);
        Assert.Equal("X-SCA-Service-Principal", ScaServicePrincipal.PrincipalHeader);
    }
}
