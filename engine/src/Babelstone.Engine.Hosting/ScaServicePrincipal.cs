using Microsoft.AspNetCore.Http;

namespace Babelstone.Engine.Hosting;

/// <summary>
/// The non-interactive, scoped, gateway-attested SCA service principal for the lifecycle-command
/// driver's money-mover routes (ADR-PC-036 §Decision 1 / Consequences — "a scoped, audited
/// principal, never a blanket exemption"; ADR-IC-006 §P2 attest-not-deny; ADR-IC-010 §P8;
/// ADR-IC-021).
/// </summary>
/// <remarks>
/// <para>
/// FAMILY-NEUTRAL HOME (ADR-PC-021 §A9, bd babelstone-6cpq.14). This principal lives in the shared
/// <c>Babelstone.Engine.Hosting</c> assembly so both the term-deposit money-movers and the personal-loan
/// money-movers recognise the SAME scoped credential — the lifecycle-command driver is ONE actor firing
/// clock-driven money-movers across families, so it carries one principal, not a per-family copy.
/// </para>
/// <para>
/// In plain English: a deposit matures on a date, a coupon falls due on a date, and a loan installment
/// falls due on a date. Those are fired by an automated, NON-interactive actor — the ADR-PC-036
/// lifecycle-command driver — which has no human sitting behind it to pass a fresh strong-authentication
/// (SCA) challenge. The human step-up that <see cref="ScaPrecondition"/> enforces (a fresh
/// <c>acr</c>/<c>auth_time</c>) is therefore the WRONG question for a machine: there is no human and no
/// <c>auth_time</c> to refresh. What authorises the machine instead is a SCOPED claim the bank's
/// authorization server (ADR-IC-021) minted into the driver's own access token — a PER-FAMILY scope that
/// says "this principal may fire THIS family's clock-driven money-mover lifecycle commands, and ONLY those"
/// (<see cref="DepositMoneyMoverScope"/> for the deposit, <see cref="LoanMoneyMoverScope"/> for the loan) —
/// which Kong validated and attested to the engine as the <see cref="PrincipalHeader"/> header (the same
/// <c>set_header</c> overwrite-from-the-token anti-spoof pattern Kong uses for <c>X-Client-Id</c> and the
/// <c>X-SCA-*</c> headers). The engine trusts the gateway attestation, never the caller's word (the
/// Boundary-2 attest-not-deny model, ADR-IC-006 §P5 / ADR-IC-010 §P8).
/// </para>
/// <para>
/// SCOPE-LIMITED BY CONSTRUCTION. This principal authorises ONLY the clock-driven lifecycle money-movers —
/// the deposit's <c>/maturity</c> + <c>/interest</c> (via <see cref="DepositMoneyMoverScope"/>) and the
/// loan's <c>/installment</c> (via <see cref="LoanMoneyMoverScope"/>), each behind its OWN family scope
/// (<see cref="OperationScopes"/>). It does NOT authorise a CUSTOMER-INITIATED money-mover — the deposit's
/// <c>/terminate</c> (early termination) or the loan's <c>/early-repayment</c> — which stay human-SCA-only:
/// the driver never breaks a deposit or repays a loan early on the customer's behalf, so its principal has no
/// business doing so. Nor does it touch the GDPR / operator surfaces (erase-personal-data, write-off). A
/// principal presented against any operation not in <see cref="AuthorisedOperations"/>, or carrying the
/// wrong family's scope for the operation, is REFUSED here and the caller falls back to the fail-closed
/// human-SCA gate. This is the "scoped, not blanket" guarantee ADR-PC-036 §Consequences requires made
/// mechanical: the allowance is route-scoped AND family-scoped, so a leaked driver token cannot be turned on
/// the termination / early-repayment surfaces, nor across families (a loan token cannot mature a deposit).
/// </para>
/// <para>
/// PER-FAMILY SCOPE (bd babelstone-6cpq.14). Each family names its OWN clock-driven money-mover scope —
/// the deposit's <see cref="DepositMoneyMoverScope"/> (maturity + coupon) and the loan's
/// <see cref="LoanMoneyMoverScope"/> (installment) — so the driver presents the family-specific scope its
/// command targets. This is scoped-not-blanket ACROSS families too: a token scoped only to the loan
/// money-mover cannot mature a deposit, and a deposit-scoped token cannot pay a loan installment
/// (<see cref="OperationScopes"/> maps each operation to the ONE scope that authorises it).
/// </para>
/// <para>
/// FAIL-CLOSED. A missing/empty principal header, a header that does not carry the exact family
/// money-mover scope the operation requires, or an operation outside the authorised set all return
/// <see langword="false"/> — the caller then runs the unchanged human-SCA <see cref="ScaPrecondition"/>,
/// whose default is the <c>422 SCA_REQUIRED</c> refusal. So recognising the principal can only ever
/// WIDEN authorisation for the scoped routes; it never weakens the default for any other caller or
/// route. The attestation value carries no PII (ADR-PC-004 §P2) — it is a structural scope token.
/// </para>
/// </remarks>
public static class ScaServicePrincipal
{
    /// <summary>The gateway-attested scoped-service-principal header (the OAuth <c>scope</c> claim Kong
    /// copied from the AS-signed token for the lifecycle driver). A non-empty value carrying a family
    /// money-mover scope (<see cref="DepositMoneyMoverScope"/> / <see cref="LoanMoneyMoverScope"/>)
    /// authorises that family's clock-driven money-mover lifecycle commands. Kong OVERWRITES it from the
    /// validated token, so a client-supplied value can never forge it (the same anti-spoof set_header
    /// pattern as <c>X-Client-Id</c> / <c>X-SCA-Acr</c>).</summary>
    public const string PrincipalHeader = "X-SCA-Service-Principal";

    /// <summary>The scope token that authorises the non-interactive lifecycle driver to fire the TERM-DEPOSIT
    /// clock-driven money-movers (maturity + coupon). Kept in lock-step with the Kong route-scoped allowance
    /// in <c>infra/kong/kong.yml</c> and the scope the IAM (ADR-IC-021) mints for the lifecycle-driver service
    /// principal.</summary>
    public const string DepositMoneyMoverScope = "lifecycle:deposit-money-mover";

    /// <summary>The scope token that authorises the non-interactive lifecycle driver to fire the PERSONAL-LOAN
    /// clock-driven money-mover (installment) — the loan analogue of <see cref="DepositMoneyMoverScope"/>
    /// (bd babelstone-6cpq.14). A SEPARATE, family-specific scope so a token scoped to loans cannot mature a
    /// deposit (and vice versa). The driver presents this on <c>X-SCA-Service-Principal</c> for an installment
    /// command (PR #404's <c>InstallmentRule.ServicePrincipalScope</c>, currently <see langword="null"/>, must
    /// be set to this value post-merge — see the PR body follow-up). NOTE: like the deposit scope this is a
    /// gateway/IAM contract — registering <c>lifecycle:loan-money-mover</c> in <c>infra/kong/kong.yml</c> and
    /// the IAM scope catalogue (ADR-IC-021) is the same cross-system follow-up the deposit scope already
    /// has.</summary>
    public const string LoanMoneyMoverScope = "lifecycle:loan-money-mover";

    /// <summary>The maturity money-mover route leaf (<c>POST /v1/deposits/{id}/maturity</c>).</summary>
    public const string MaturityOperation = "maturity";

    /// <summary>The PERIODIC-coupon money-mover route leaf (<c>POST /v1/deposits/{id}/interest</c>).</summary>
    public const string InterestOperation = "interest";

    /// <summary>The loan-installment money-mover route leaf (<c>POST /v1/loans/{id}/installment</c>) — the
    /// clock-driven loan occurrence the ADR-PC-036 driver fires on its due date (bd babelstone-6cpq.9 / .14).
    /// Scoped, not blanket: the loan's customer-initiated money-movers (early-repayment) and operator/GDPR
    /// surfaces (write-off, erase-personal-data) are deliberately NOT here.</summary>
    public const string InstallmentOperation = "installment";

    /// <summary>The per-operation route-scoping: each clock-driven lifecycle money-mover the ADR-PC-036 driver
    /// fires maps to the ONE family scope that authorises it. The deposit maturity + coupon require
    /// <see cref="DepositMoneyMoverScope"/>; the loan installment requires <see cref="LoanMoneyMoverScope"/>.
    /// This map IS the route-scoping AND the cross-family isolation: an operation absent from it is refused
    /// outright, and an operation present requires its EXACT family scope — so a deposit-scoped token cannot
    /// reach the loan installment and vice versa. Deliberately EXCLUDES every customer-initiated money-mover
    /// (<c>terminate</c>, <c>early-repayment</c>) and every operator/GDPR surface (<c>write-off</c>,
    /// <c>erase-personal-data</c>), which stay human-SCA-only.</summary>
    private static readonly IReadOnlyDictionary<string, string> OperationScopes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MaturityOperation] = DepositMoneyMoverScope,
            [InterestOperation] = DepositMoneyMoverScope,
            [InstallmentOperation] = LoanMoneyMoverScope,
        };

    /// <summary>The ONLY route leaves this scoped principal can authorise (the keys of
    /// <see cref="OperationScopes"/>) — the clock-driven lifecycle money-movers the ADR-PC-036 driver fires
    /// across families (the deposit maturity + coupon and the loan installment). An operation outside it is
    /// refused even with a valid principal header.</summary>
    public static readonly IReadOnlySet<string> AuthorisedOperations =
        new HashSet<string>(OperationScopes.Keys, StringComparer.Ordinal);

    /// <summary>
    /// True when a valid scoped service principal is attested for <paramref name="operation"/> — i.e.
    /// <paramref name="operation"/> is one of the <see cref="AuthorisedOperations"/> AND the gateway-attested
    /// <see cref="PrincipalHeader"/> carries the EXACT family scope that operation requires
    /// (<see cref="OperationScopes"/>). False for every other case (no/blank/wrong-scope header, a scope for
    /// the WRONG family, or a non-authorised operation such as <c>terminate</c> / <c>early-repayment</c>), so
    /// the caller fails closed to the human-SCA gate.
    /// </summary>
    /// <param name="headers">The inbound request's header collection (the gateway-attested headers).</param>
    /// <param name="operation">The money-mover route leaf under request (e.g. <c>maturity</c>,
    /// <c>installment</c>, <c>terminate</c>) — the scope-limiting key.</param>
    public static bool IsAuthorised(IHeaderDictionary headers, string operation)
    {
        // Scope-limited FIRST: a principal can never reach a route outside its allowance, regardless of
        // what scope token it carries (the route-scoped half of "scoped, not blanket"). The lookup also
        // selects WHICH family scope this operation demands.
        if (!OperationScopes.TryGetValue(operation, out var requiredScope))
        {
            return false;
        }

        var attested = headers[PrincipalHeader].ToString();
        if (string.IsNullOrWhiteSpace(attested))
        {
            return false;
        }

        // The OAuth `scope` claim is space-delimited (one token may sit among others). Membership of the
        // EXACT family money-mover scope this operation requires is the authorisation — a partial/substring
        // match is not enough, and a scope for the WRONG family (deposit scope on a loan op, or vice versa)
        // does not authorise.
        var scopes = attested.Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return scopes.Contains(requiredScope, StringComparer.Ordinal);
    }

    /// <summary>The family money-mover scope a given clock-driven <paramref name="operation"/> requires, or
    /// <see langword="null"/> when the operation is not in the scoped allowance. Used to AUDIT-LOG the exact
    /// scope a principal-authorised request presented (ADR-PC-036 §Consequences — the principal's use is
    /// audited, never invisible).</summary>
    public static string? RequiredScope(string operation) =>
        OperationScopes.TryGetValue(operation, out var scope) ? scope : null;
}
