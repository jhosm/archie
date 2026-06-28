using Microsoft.AspNetCore.Http;

namespace Babelstone.Families.TermDeposit.Application;

/// <summary>
/// The non-interactive, scoped, gateway-attested SCA service principal for the lifecycle-command
/// driver's money-mover routes (ADR-PC-036 §Decision 1 / Consequences — "a scoped, audited
/// principal, never a blanket exemption"; ADR-IC-006 §P2 attest-not-deny; ADR-IC-010 §P8;
/// ADR-IC-021).
/// </summary>
/// <remarks>
/// <para>
/// In plain English: a deposit matures on a date, and a coupon falls due on a date. Those are
/// fired by an automated, NON-interactive actor — the ADR-PC-036 lifecycle-command driver — which
/// has no human sitting behind it to pass a fresh strong-authentication (SCA) challenge. The human
/// step-up that <see cref="ScaPrecondition"/> enforces (a fresh <c>acr</c>/<c>auth_time</c>) is
/// therefore the WRONG question for a machine: there is no human and no <c>auth_time</c> to refresh.
/// What authorises the machine instead is a SCOPED claim the bank's authorization server (ADR-IC-021)
/// minted into the driver's own access token — a scope that says "this principal may fire the deposit
/// money-mover lifecycle commands, and ONLY those" — which Kong validated and attested to the engine
/// as the <see cref="PrincipalHeader"/> header (the same <c>set_header</c> overwrite-from-the-token
/// anti-spoof pattern Kong uses for <c>X-Client-Id</c> and the <c>X-SCA-*</c> headers). The engine
/// trusts the gateway attestation, never the caller's word (the Boundary-2 attest-not-deny model,
/// ADR-IC-006 §P5 / ADR-IC-010 §P8).
/// </para>
/// <para>
/// SCOPE-LIMITED BY CONSTRUCTION. This principal authorises ONLY the deposit lifecycle money-movers —
/// <c>/maturity</c> and <c>/interest</c> (<see cref="AuthorisedOperations"/>). It does NOT authorise
/// <c>/terminate</c> (a customer-initiated early termination), which stays a human-SCA-only money-mover:
/// the driver never breaks a deposit, so its principal has no business doing so. A principal presented
/// against any other money-mover (or any operation not in <see cref="AuthorisedOperations"/>) is
/// REFUSED here and the caller falls back to the fail-closed human-SCA gate. This is the "scoped, not
/// blanket" guarantee ADR-PC-036 §Consequences requires made mechanical: the allowance is route-scoped,
/// so a leaked driver token cannot be turned on the termination surface.
/// </para>
/// <para>
/// FAIL-CLOSED. A missing/empty principal header, a header that does not carry the exact
/// <see cref="LifecycleMoneyMoverScope"/> token, or an operation outside the authorised set all return
/// <see langword="false"/> — the caller then runs the unchanged human-SCA <see cref="ScaPrecondition"/>,
/// whose default is the <c>422 SCA_REQUIRED</c> refusal. So recognising the principal can only ever
/// WIDEN authorisation for the two scoped routes; it never weakens the default for any other caller or
/// route. The attestation value carries no PII (ADR-PC-004 §P2) — it is a structural scope token.
/// </para>
/// </remarks>
public static class ScaServicePrincipal
{
    /// <summary>The gateway-attested scoped-service-principal header (the OAuth <c>scope</c> claim Kong
    /// copied from the AS-signed token for the lifecycle driver). A non-empty value carrying
    /// <see cref="LifecycleMoneyMoverScope"/> authorises the deposit money-mover lifecycle commands.
    /// Kong OVERWRITES it from the validated token, so a client-supplied value can never forge it
    /// (the same anti-spoof set_header pattern as <c>X-Client-Id</c> / <c>X-SCA-Acr</c>).</summary>
    public const string PrincipalHeader = "X-SCA-Service-Principal";

    /// <summary>The exact scope token that authorises the non-interactive lifecycle driver to fire the
    /// deposit money-mover lifecycle commands (maturity, coupon). Kept in lock-step with the Kong
    /// route-scoped allowance in <c>infra/kong/kong.yml</c> and the scope the IAM (ADR-IC-021) mints
    /// for the lifecycle-driver service principal.</summary>
    public const string LifecycleMoneyMoverScope = "lifecycle:deposit-money-mover";

    /// <summary>The maturity money-mover route leaf (<c>POST /v1/deposits/{id}/maturity</c>).</summary>
    public const string MaturityOperation = "maturity";

    /// <summary>The PERIODIC-coupon money-mover route leaf (<c>POST /v1/deposits/{id}/interest</c>).</summary>
    public const string InterestOperation = "interest";

    /// <summary>The ONLY route leaves this scoped principal authorises — the two clock-driven lifecycle
    /// money-movers the ADR-PC-036 driver fires. Deliberately EXCLUDES <c>terminate</c> (customer-initiated
    /// early termination stays human-SCA-only). This set IS the route-scoping: an operation outside it is
    /// refused even with a valid principal header.</summary>
    public static readonly IReadOnlySet<string> AuthorisedOperations =
        new HashSet<string>(StringComparer.Ordinal) { MaturityOperation, InterestOperation };

    /// <summary>
    /// True when a valid scoped service principal is attested for <paramref name="operation"/> — i.e. the
    /// gateway-attested <see cref="PrincipalHeader"/> carries the <see cref="LifecycleMoneyMoverScope"/>
    /// token AND <paramref name="operation"/> is one of the <see cref="AuthorisedOperations"/>. False for
    /// every other case (no/blank/wrong-scope header, or a non-authorised operation such as
    /// <c>terminate</c>), so the caller fails closed to the human-SCA gate.
    /// </summary>
    /// <param name="headers">The inbound request's header collection (the gateway-attested headers).</param>
    /// <param name="operation">The money-mover route leaf under request (e.g. <c>maturity</c>,
    /// <c>interest</c>, <c>terminate</c>) — the scope-limiting key.</param>
    public static bool IsAuthorised(IHeaderDictionary headers, string operation)
    {
        // Scope-limited FIRST: a principal can never reach a route outside its allowance, regardless of
        // what scope token it carries (the route-scoped half of "scoped, not blanket").
        if (!AuthorisedOperations.Contains(operation))
        {
            return false;
        }

        var attested = headers[PrincipalHeader].ToString();
        if (string.IsNullOrWhiteSpace(attested))
        {
            return false;
        }

        // The OAuth `scope` claim is space-delimited (one token may sit among others). Membership of the
        // exact lifecycle money-mover scope is the authorisation — a partial/substring match is not enough.
        var scopes = attested.Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return scopes.Contains(LifecycleMoneyMoverScope, StringComparer.Ordinal);
    }
}
