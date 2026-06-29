using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Babelstone.Engine.Hosting;

/// <summary>
/// The step-up-SCA precondition wired as an endpoint filter on the irreversible money-mover route group
/// (ADR-IC-010 §P8 / Q-BE resolution), now with the non-interactive scoped
/// service-principal escape for the ADR-PC-036 lifecycle-command driver.
/// </summary>
/// <remarks>
/// <para>
/// FAMILY-NEUTRAL HOME (ADR-PC-021 §A9). This filter is a cross-cutting host-shell
/// component, so it lives in the shared <c>Babelstone.Engine.Hosting</c> assembly and is <c>public</c> for
/// cross-assembly use — both the term-deposit money-movers (<c>DepositsEndpoints.Map</c>) and the
/// personal-loan money-movers (<c>LoansEndpoints.Map</c>) attach the SAME filter type to their
/// <c>/v1/{family}/{id}</c> money-mover route group. Path-derived scoping (see <c>MoneyMoverOperation</c>)
/// keeps it family-agnostic: it reads the trailing route leaf, never a family type.
/// </para>
/// <para>
/// In plain English: an irreversible money-mover — maturing a deposit, paying a coupon, collecting a loan
/// installment — must not settle unless the bank either just saw a fresh human strong-authentication (SCA)
/// proof, OR the caller is the bank's own automated lifecycle driver carrying a scoped, gateway-attested
/// service-principal credential. That gate is a CROSS-CUTTING concern, not the business handler's job — so it
/// lives here, attached to each family's money-mover route group, instead of being copied into each handler.
/// Anything mapped onto that group is gated by construction; the handler is reached only once authorisation
/// is established, so it stays pure domain orchestration and never has to touch the request headers.
/// </para>
/// <para>
/// TWO authorisation paths, one fail-closed default. (1) The human step-up: the unchanged,
/// separately-tested <see cref="ScaPrecondition"/> reads the gateway-attested <c>X-SCA-Acr</c> /
/// <c>X-SCA-Auth-Time</c> headers and short-circuits <c>422 SCA_REQUIRED</c> on absent/stale proof — the
/// agent / customer flow. (2) The non-interactive principal: a machine actor (the ADR-PC-036 driver) has
/// no human <c>acr</c>/<c>auth_time</c>, so its authorisation is a SCOPED gateway-attested
/// <see cref="ScaServicePrincipal.PrincipalHeader"/> claim that authorises ONLY the clock-driven money-mover
/// leaves — the deposit's <c>/maturity</c> + <c>/interest</c> and the loan's <c>/installment</c>
/// (<see cref="ScaServicePrincipal.AuthorisedOperations"/>). The filter checks the principal FIRST; if it
/// authorises THIS route it AUDIT-LOGS the use and proceeds, otherwise it falls back to the human-SCA check —
/// so the <c>422 SCA_REQUIRED</c> default still holds for every other caller and for any route the principal
/// is not scoped to (a customer-initiated money-mover such as <c>/terminate</c> or <c>/early-repayment</c>).
/// </para>
/// <para>
/// This preserves every §P8 / ADR-PC-010 §P5 constraint the inline check satisfied: the filter runs in the
/// impure host shell, BEFORE the handler executes (so before any side effect). The clock is injected (the
/// shell owns the wall-clock, never the pure decider). The principal-use audit trail (ADR-PC-036
/// §Consequences — the principal's use must be audited, not invisible) resolves its logger via
/// GetRequiredService from the request services on the audit branch, so the obligation is STRUCTURAL: a host
/// missing an ILoggerFactory fails loudly there rather than silently skipping the audit. The filter's
/// CONSTRUCTION still carries no logger dependency and stays trivially composable in any host. The route leaf
/// the principal is scoped against is derived from the request path's trailing segment.
/// </para>
/// </remarks>
public sealed class ScaPreconditionFilter(TimeProvider clock) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var headers = context.HttpContext.Request.Headers;

        // The money-mover route leaf this request targets (e.g. maturity / interest / installment /
        // terminate / early-repayment) — the scope-limiting key. Derived from the trailing path segment; the
        // scoped principal is only ever authorised for the leaves in ScaServicePrincipal.AuthorisedOperations.
        var operation = MoneyMoverOperation(context);

        // Path 2 — the non-interactive scoped service principal (ADR-PC-036). If the gateway attested a
        // principal scoped to THIS route leaf, authorise it (no human step-up exists for a machine) and
        // AUDIT-LOG the use, then proceed. Otherwise fall through to the human-SCA gate below — so a
        // principal scoped only to the clock-driven money-movers cannot reach a customer-initiated one
        // (/terminate, /early-repayment), and a caller with no principal hits the unchanged fail-closed default.
        if (ScaServicePrincipal.IsAuthorised(headers, operation))
        {
            // Structural audit only — the scope token + the route leaf, never PII (ADR-PC-004 §P2). This is
            // the "audited principal, never invisible" obligation of ADR-PC-036 §Consequences — so the audit
            // is STRUCTURAL, not best-effort: the logger is resolved with GetRequiredService, so a host that
            // forgot to register an ILoggerFactory fails LOUDLY here instead of silently dropping the audit
            // (which would soften the obligation). The filter's CONSTRUCTION still needs no logger dependency
            // (any host can compose it with just the clock); only the audit branch touches request services.
            context.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger<ScaPreconditionFilter>()
                .LogInformation(
                    "SCA bypassed via the scoped non-interactive service principal ({Scope}) on the "
                        + "{Operation} money-mover route (ADR-PC-036 lifecycle-command driver).",
                    ScaServicePrincipal.LifecycleMoneyMoverScope,
                    operation);
            return await next(context);
        }

        // Path 1 — the human step-up SCA gate. Same verdict, same ordering as the old inline check.
        var denied = ScaPrecondition.Check(headers, clock.GetUtcNow());
        if (denied is not null)
        {
            return denied;
        }

        return await next(context);
    }

    /// <summary>The trailing path segment of the money-mover route — the operation key the scoped
    /// principal is authorised (or not) against. For <c>/v1/deposits/{id}/maturity</c> this is
    /// <c>maturity</c>; an empty/garbled path yields <c>""</c>, which is in no authorised set, so it
    /// fails closed to the human-SCA gate.</summary>
    private static string MoneyMoverOperation(EndpointFilterInvocationContext context)
    {
        var path = context.HttpContext.Request.Path.Value;
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash < 0 ? trimmed : trimmed[(lastSlash + 1)..];
    }
}
