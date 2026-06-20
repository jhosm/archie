using Microsoft.AspNetCore.Http;

namespace Babelstone.Families.TermDeposit.Application;

/// <summary>
/// The step-up-SCA precondition wired as an endpoint filter on the irreversible money-mover route group
/// (ADR-IC-010 §P8 / Q-BE resolution, bd babelstone-ziu3.5).
/// </summary>
/// <remarks>
/// <para>
/// In plain English: maturing a deposit or paying a coupon must not settle for an AI agent unless the
/// bank just saw a fresh strong-authentication (SCA) proof. That gate is a CROSS-CUTTING concern, not the
/// business handler's job — so it lives here, as a filter attached to the money-mover route group in
/// <see cref="DepositsEndpoints.Map"/>, instead of being copied into each handler. Anything mapped onto
/// that group is gated by construction; the handler is reached only once fresh SCA is present, so it stays
/// pure domain orchestration and never has to touch the request headers.
/// </para>
/// <para>
/// This preserves every §P8 / ADR-PC-010 §P5 constraint the inline check satisfied: the filter runs in the
/// impure host shell, BEFORE the handler executes (so before any side effect), reads the gateway-attested
/// <c>X-SCA-Acr</c> / <c>X-SCA-Auth-Time</c> headers, and short-circuits <c>422 SCA_REQUIRED</c> on
/// absent/stale proof. The decision itself is the unchanged, separately-tested <see cref="ScaPrecondition"/>;
/// this type only moves the INVOCATION off the business endpoint. The clock is injected (the shell owns the
/// wall-clock, never the pure decider).
/// </para>
/// </remarks>
internal sealed class ScaPreconditionFilter(TimeProvider clock) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // Same verdict, same ordering as the old inline check — just hoisted out of the handler.
        var denied = ScaPrecondition.Check(context.HttpContext.Request.Headers, clock.GetUtcNow());
        if (denied is not null)
        {
            return denied;
        }

        return await next(context);
    }
}
