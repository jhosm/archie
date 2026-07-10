using Microsoft.AspNetCore.Http;

namespace Babelstone.Engine.Hosting;

/// <summary>
/// The non-interactive, scoped, gateway-attested SCA service principal for the lifecycle-command
/// driver's money-mover routes (ADR-PC-036 — "a scoped, audited
/// principal, never a blanket exemption"; ADR-IC-006 attest-not-deny; ADR-IC-010;
/// ADR-IC-021).
/// </summary>
/// <remarks>
/// <para>
/// FAMILY-NEUTRAL HOME (ADR-PC-021). This principal lives in the shared
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
/// authorization server (ADR-IC-021) minted into the driver's own access token — a scope that says "this
/// principal may fire the clock-driven money-mover lifecycle commands, and ONLY those" — which Kong
/// validated and attested to the engine as the <see cref="PrincipalHeader"/> header (the same
/// <c>set_header</c> overwrite-from-the-token anti-spoof pattern Kong uses for <c>X-Client-Id</c> and the
/// <c>X-SCA-*</c> headers). The engine trusts the gateway attestation, never the caller's word (the
/// Boundary-2 attest-not-deny model, ADR-IC-006 / ADR-IC-010).
/// </para>
/// <para>
/// SCOPE-LIMITED BY CONSTRUCTION. This principal authorises ONLY the clock-driven lifecycle money-movers —
/// the deposit's <c>/maturity</c> + <c>/interest</c> and the loan's <c>/installment</c>
/// (<see cref="AuthorisedOperations"/>). It does NOT authorise a CUSTOMER-INITIATED money-mover — the
/// deposit's <c>/terminate</c> (early termination) or the loan's <c>/early-repayment</c> — which stay
/// human-SCA-only: the driver never breaks a deposit or repays a loan early on the customer's behalf, so its
/// principal has no business doing so. Nor does it touch the GDPR / operator surfaces (erase-personal-data,
/// write-off). A principal presented against any operation not in <see cref="AuthorisedOperations"/> is
/// REFUSED here and the caller falls back to the fail-closed human-SCA gate. This is the "scoped, not
/// blanket" guarantee ADR-PC-036 requires made mechanical: the allowance is route-scoped,
/// so a leaked driver token cannot be turned on the termination or early-repayment surfaces.
/// </para>
/// <para>
/// FAIL-CLOSED. A missing/empty principal header, a header that does not carry the exact
/// <see cref="LifecycleMoneyMoverScope"/> token, or an operation outside the authorised set all return
/// <see langword="false"/> — the caller then runs the unchanged human-SCA <see cref="ScaPrecondition"/>,
/// whose default is the <c>422 SCA_REQUIRED</c> refusal. So recognising the principal can only ever
/// WIDEN authorisation for the scoped routes; it never weakens the default for any other caller or
/// route. The attestation value carries no PII (ADR-PC-004) — it is a structural scope token.
/// </para>
/// </remarks>
public static class ScaServicePrincipal
{
    /// <summary>The gateway-attested scoped-service-principal header (the OAuth <c>scope</c> claim Kong
    /// copied from the AS-signed token for the lifecycle driver). A non-empty value carrying
    /// <see cref="LifecycleMoneyMoverScope"/> authorises the clock-driven money-mover lifecycle commands.
    /// Kong OVERWRITES it from the validated token, so a client-supplied value can never forge it
    /// (the same anti-spoof set_header pattern as <c>X-Client-Id</c> / <c>X-SCA-Acr</c>).</summary>
    public const string PrincipalHeader = "X-SCA-Service-Principal";

    /// <summary>The exact scope token that authorises the non-interactive lifecycle driver to fire the
    /// clock-driven money-mover lifecycle commands (the deposit's maturity + coupon and the loan's
    /// installment). Kept in lock-step with the Kong route-scoped allowance in <c>infra/kong/kong.yml</c>
    /// and the scope the IAM (ADR-IC-021) mints for the lifecycle-driver service principal.
    /// <para>NOTE: the literal value keeps its <c>deposit-money-mover</c> spelling for byte-for-byte
    /// lock-step with the existing Kong route allowance, the IAM-minted scope, and the
    /// <c>HttpLifecycleCommandSink</c> contract — it is now a FAMILY-NEUTRAL scope by MEANING (it authorises
    /// the loan installment too) even though the string still reads "deposit". Renaming the value is a
    /// cross-system IAM/Kong contract change, so <see cref="AuthorisedOperations"/> is the authoritative
    /// route-scoping, not the scope's spelling.</para></summary>
    public const string LifecycleMoneyMoverScope = "lifecycle:deposit-money-mover";

    /// <summary>The maturity money-mover route leaf (<c>POST /v1/deposits/{id}/maturity</c>).</summary>
    public const string MaturityOperation = "maturity";

    /// <summary>The PERIODIC-coupon money-mover route leaf (<c>POST /v1/deposits/{id}/interest</c>).</summary>
    public const string InterestOperation = "interest";

    /// <summary>The loan-installment money-mover route leaf (<c>POST /v1/loans/{id}/installment</c>) — the
    /// clock-driven loan occurrence the ADR-PC-036 driver fires on its due date.
    /// Scoped, not blanket: the loan's customer-initiated money-movers (early-repayment) and operator/GDPR
    /// surfaces (write-off, erase-personal-data) are deliberately NOT here.</summary>
    public const string InstallmentOperation = "installment";

    /// <summary>
    /// The current_account synchronous-authorize money-mover route leaf
    /// (<c>POST /v1/accounts/{id}/authorize</c>, ADR-PC-037 §D6 / ADR-PC-034) — the DEBIT-authorising
    /// decision an internal caller (the orchestrator saga dispatcher, the MCP account tools, the ADR-PC-036
    /// lifecycle-command driver's hold-expiry / accrual siblings) invokes on the mTLS-only internal command
    /// surface (ADR-IC-006 §P5 Boundary-2). Defined here as the settled name for the authorize operation so
    /// that IF internal money-mover parity is later adopted (bd babelstone-sx4v acceptance criterion 2), the
    /// gate wires against a NAMED, distinct scope — never a silent reuse of the lifecycle-sweep allowance.
    /// <para>
    /// TRUST-SCOPE DECISION (bd babelstone-sx4v acceptance criterion 1). Authorizing an ARBITRARY debit is a
    /// materially wider power than firing a scheduled, self-computed lifecycle occurrence (a deposit's
    /// maturity / coupon or a loan's installment, whose amount and timing the schedule dictates). So a debit-
    /// authorizing principal MUST carry a DISTINCT scope — <see cref="AuthorizeDebitScope"/> — never the
    /// <see cref="LifecycleMoneyMoverScope"/> the clock-driven sweeps present: a leaked lifecycle-driver token
    /// must not be turnable on the authorize surface, and conversely a payment-authorize principal has no
    /// business firing a maturity. This is the "scoped, not blanket" split (ADR-PC-036 §Consequences) applied
    /// to the authorize surface: two disjoint scopes, two disjoint route allowances.
    /// </para>
    /// <para>
    /// NOT CURRENTLY GATED. <see cref="AuthorizeOperation"/> is DELIBERATELY absent from
    /// <see cref="AuthorisedOperations"/>, and the authorize route is NOT wrapped in
    /// <see cref="ScaPreconditionFilter"/> this run. The reason is the ADR-PC-034 split: strong customer
    /// authentication is an upstream stage-1/2 external/rail concern, not the engine's stage-3/5 de-settled
    /// decision, and the authorize surface's real protection is the Boundary-2 mTLS network boundary
    /// (ADR-IC-006 §P5) plus its deliberate absence from the public Kong edge — it is reachable only by
    /// trusted internal services. Wrapping it in the <see cref="ScaPreconditionFilter"/> would fail those
    /// internal callers CLOSED: they forward a service principal, not a human <c>acr</c>/<c>auth_time</c>, so
    /// the filter's human-SCA fallback would return <c>422 SCA_REQUIRED</c> — and it would break the
    /// AUTHORIZATION_SYNC_IDEMPOTENT contract, whose callers present no SCA header at all. Adopting an engine-
    /// side gate here therefore stays a FUTURE, ADR-PC-034-amending decision; this constant records the
    /// distinct scope so that future adoption is a wiring change, not a fresh trust-scope negotiation.
    /// </para>
    /// </summary>
    public const string AuthorizeOperation = "authorize";

    /// <summary>The DISTINCT scope a debit-authorizing service principal would carry IF internal
    /// money-mover parity is later adopted for <see cref="AuthorizeOperation"/> (bd babelstone-sx4v). It is
    /// DELIBERATELY NOT the <see cref="LifecycleMoneyMoverScope"/>: authorizing an arbitrary debit is a wider
    /// power than firing a scheduled lifecycle occurrence, so the two live in disjoint scope namespaces —
    /// a leaked lifecycle-sweep token cannot reach the authorize surface, and vice versa. Defined but NOT yet
    /// wired: <see cref="IsAuthorised"/> only recognises <see cref="LifecycleMoneyMoverScope"/> today, because
    /// <see cref="AuthorizeOperation"/> is not in <see cref="AuthorisedOperations"/> (the authorize route
    /// stays ungated per ADR-PC-034 — see <see cref="AuthorizeOperation"/> for the full rationale). When
    /// adopted, this value must be minted by the IAM (ADR-IC-021) for the payment-authorize principal and
    /// route-scoped in <c>infra/kong/kong.yml</c>, in lock-step — the same cross-system contract the lifecycle
    /// scope carries.</summary>
    public const string AuthorizeDebitScope = "authorize:current-account-debit";

    /// <summary>The ONLY route leaves this scoped principal authorises — the clock-driven lifecycle
    /// money-movers the ADR-PC-036 driver fires across families (the deposit maturity + coupon and the loan
    /// installment). Deliberately EXCLUDES every customer-initiated money-mover (<c>terminate</c>,
    /// <c>early-repayment</c>) and every operator/GDPR surface (<c>write-off</c>, <c>erase-personal-data</c>),
    /// which stay human-SCA-only. This set IS the route-scoping: an operation outside it is refused even with
    /// a valid principal header.</summary>
    public static readonly IReadOnlySet<string> AuthorisedOperations =
        new HashSet<string>(StringComparer.Ordinal) { MaturityOperation, InterestOperation, InstallmentOperation };

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
