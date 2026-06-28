using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Babelstone.Families.TermDeposit.Application;

/// <summary>
/// The step-up-SCA precondition wired as an endpoint filter on the irreversible money-mover route group
/// (ADR-IC-010 §P8 / Q-BE resolution, bd babelstone-ziu3.5), now with the non-interactive scoped
/// service-principal escape for the ADR-PC-036 lifecycle-command driver.
/// </summary>
/// <remarks>
/// <para>
/// In plain English: maturing a deposit or paying a coupon must not settle unless the bank either just
/// saw a fresh human strong-authentication (SCA) proof, OR the caller is the bank's own automated
/// lifecycle driver carrying a scoped, gateway-attested service-principal credential. That gate is a
/// CROSS-CUTTING concern, not the business handler's job — so it lives here, as a filter attached to the
/// money-mover route group in <see cref="DepositsEndpoints.Map"/>, instead of being copied into each
/// handler. Anything mapped onto that group is gated by construction; the handler is reached only once
/// authorisation is established, so it stays pure domain orchestration and never has to touch the request
/// headers.
/// </para>
/// <para>
/// TWO authorisation paths, one fail-closed default. (1) The human step-up: the unchanged,
/// separately-tested <see cref="ScaPrecondition"/> reads the gateway-attested <c>X-SCA-Acr</c> /
/// <c>X-SCA-Auth-Time</c> headers and short-circuits <c>422 SCA_REQUIRED</c> on absent/stale proof — the
/// agent / customer flow. (2) The non-interactive principal: a machine actor (the ADR-PC-036 driver) has
/// no human <c>acr</c>/<c>auth_time</c>, so its authorisation is a SCOPED gateway-attested
/// <see cref="ScaServicePrincipal.PrincipalHeader"/> claim that authorises ONLY the
/// <c>/maturity</c> + <c>/interest</c> route leaves (<see cref="ScaServicePrincipal.AuthorisedOperations"/>).
/// The filter checks the principal FIRST; if it authorises THIS route it AUDIT-LOGS the use and proceeds,
/// otherwise it falls back to the human-SCA check — so the <c>422 SCA_REQUIRED</c> default still holds for
/// every other caller and for any route the principal is not scoped to (e.g. <c>/terminate</c>).
/// </para>
/// <para>
/// This preserves every §P8 / ADR-PC-010 §P5 constraint the inline check satisfied: the filter runs in the
/// impure host shell, BEFORE the handler executes (so before any side effect). The clock is injected (the
/// shell owns the wall-clock, never the pure decider). The principal-use audit trail (ADR-PC-036
/// §Consequences — the principal's use must be audited, not invisible) resolves its logger lazily from the
/// request services on the audit branch, so the filter's CONSTRUCTION carries no logger dependency and stays
/// trivially composable in any host. The route leaf the principal is scoped against is derived from the
/// request path's trailing segment.
/// </para>
/// </remarks>
internal sealed class ScaPreconditionFilter(TimeProvider clock) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var headers = context.HttpContext.Request.Headers;

        // The money-mover route leaf this request targets (maturity / interest / terminate) — the
        // scope-limiting key. Derived from the trailing path segment; the scoped principal is only ever
        // authorised for the leaves in ScaServicePrincipal.AuthorisedOperations.
        var operation = MoneyMoverOperation(context);

        // Path 2 — the non-interactive scoped service principal (ADR-PC-036). If the gateway attested a
        // principal scoped to THIS route leaf, authorise it (no human step-up exists for a machine) and
        // AUDIT-LOG the use, then proceed. Otherwise fall through to the human-SCA gate below — so a
        // principal scoped only to maturity/interest cannot reach /terminate, and a caller with no
        // principal hits the unchanged fail-closed default.
        if (ScaServicePrincipal.IsAuthorised(headers, operation))
        {
            // Structural audit only — the scope token + the route leaf, never PII (ADR-PC-004 §P2). This
            // is the "audited principal, never invisible" obligation of ADR-PC-036 §Consequences. The
            // logger is resolved lazily and null-safely from request services so the filter's construction
            // needs no logger dependency (any host can compose it with just the clock).
            context.HttpContext.RequestServices
                .GetService<ILoggerFactory>()
                ?.CreateLogger<ScaPreconditionFilter>()
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
