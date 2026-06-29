using Microsoft.AspNetCore.Http;

namespace Babelstone.Engine.Hosting;

/// <summary>
/// The step-up-SCA precondition on an irreversible money-mover (ADR-IC-010 §P8 / Document 11
/// §Human-in-the-Loop · Q-BE resolution, bd babelstone-ziu3.5).
/// </summary>
/// <remarks>
/// <para>
/// FAMILY-NEUTRAL HOME (ADR-PC-021 §A9, bd babelstone-6cpq.14). This gate is a cross-cutting host-shell
/// concern, not one family's business, so it lives in the shared <c>Babelstone.Engine.Hosting</c> assembly
/// alongside the other family-agnostic in-process hosting components — the single home both the term-deposit
/// money-movers (<c>DepositsEndpoints</c>) and the personal-loan money-movers (<c>LoansEndpoints</c>) wire it
/// from. ONE gate mechanism referenced by both families, never a per-family copy.
/// </para>
/// <para>
/// In plain English: before the engine settles something irreversible for an AI agent — maturing a
/// deposit, paying a coupon, collecting a loan installment — it must see proof that the customer just passed
/// a fresh strong-authentication (SCA) challenge at the bank. That proof is NOT something the agent can
/// assert: it is the bank's own
/// authorization server (AS) signing an <c>acr</c> (authentication-context-class) claim into the access
/// token, and Kong — having validated that signature — attesting it to the engine as the
/// <c>X-SCA-Acr</c> / <c>X-SCA-Auth-Time</c> headers (the same <c>set_header</c> overwrite-from-the-token
/// anti-spoof pattern Kong already uses for <c>X-Client-Id</c>). If the proof is absent, too weak, or
/// stale, the engine refuses with <c>422 SCA_REQUIRED</c> and the MCP tool fires the step-up elicitation
/// and retries with a refreshed token. The trust anchor is the AS signature Kong validated, never the
/// courier (the agent) — which is exactly what §P8 requires: "the irreversible action transitions on the
/// bank's own out-of-band signal, not anything the agent reports back."
/// </para>
/// <para>
/// This is Q1 (SCA-trigger detection) of the Q-BE fork: the maintainer chose the engine-returns-a-structured
/// <c>SCA_REQUIRED</c> path (ADR-IC-010 §P8 recommended), over a Kong <c>pre-function</c> gate on <c>/mcp</c>
/// (which would 403 before the server could elicit) or a proactive prompt-always (which over-prompts a
/// caller who already holds fresh SCA). The freshness window mirrors the constitute/SoR REST-route SCA
/// gate already in <c>infra/kong/kong.yml</c> (<c>SCA_MAX_AGE = 300</c> s): a money-mover needs SCA that is
/// recent, not merely ever-completed (Document 10 §"Step-Up Authentication Mid-Session" — an <c>acr</c> too
/// weak or an <c>auth_time</c> too far in the past fails the precondition).
/// </para>
/// <para>
/// FAIL-CLOSED throughout: a missing/empty <c>acr</c>, a missing/non-numeric <c>auth_time</c>, an
/// <c>auth_time</c> in the future, or one older than the window all return the SAME single
/// <c>SCA_REQUIRED</c> verdict — no branch leaks a distinguishing signal. The refusal carries no PII
/// (ADR-PC-004 §P2): a stable code + a generic message only. The header NAMES match the Kong attestation;
/// the engine never reads the raw token (it trusts the gateway attestation, the Boundary-2 model
/// Document 10 / ADR-IC-006 §P5 commit to).
/// </para>
/// <para>
/// NON-INTERACTIVE PRINCIPAL ESCAPE (ADR-PC-036, bd babelstone-6cpq.4 / .9 / .14). This check is the HUMAN
/// step-up gate only. A machine actor — the ADR-PC-036 lifecycle-command driver firing a deposit's maturity
/// / coupon or a loan's installment on its due date — has no human <c>acr</c>/<c>auth_time</c> to present, so
/// it would always 422 here. Its authorisation is instead a SCOPED, gateway-attested service-principal claim
/// recognised by the sibling <see cref="ScaServicePrincipal"/>, which the <see cref="ScaPreconditionFilter"/>
/// consults BEFORE this check. That escape is route-scoped (the deposit maturity / coupon and the loan
/// installment only — never a customer-initiated money-mover such as terminate / early-repayment) and
/// audited; it only ever WIDENS authorisation for those scoped routes. This <see cref="Check"/> is unchanged
/// and remains the fail-closed default for every caller that is not a recognised scoped principal.
/// </para>
/// <para>
/// SENDER-CONSTRAINT (RFC 8705 mTLS-bound, ADR-IC-010 §A8, bd babelstone-26rb). The refreshed step-up
/// token is now sender-constrained: it carries a <c>cnf.x5t#S256</c> thumbprint Kong validated against
/// the presented client cert and attests as <see cref="CnfX5tHeader"/>. A token replayed from a
/// different sender was already 401'd at the gateway (its <c>cnf</c> did not match the presented cert),
/// so the binding is the gateway's to ENFORCE and the engine's to ACCEPT, not re-derive — the same
/// attest-not-deny Boundary-2 split that governs <c>acr</c>/<c>auth_time</c>. The freshness gate below
/// is therefore unchanged by the binding: a fresh, gateway-attested SCA proof passes whether the token
/// was sender-constrained (a non-empty <see cref="CnfX5tHeader"/>) or a plain Bearer (empty/absent).
/// </para>
/// </remarks>
public static class ScaPrecondition
{
    /// <summary>The gateway-attested SCA-completion class header (the OIDC <c>acr</c> claim Kong copied
    /// from the AS-signed token — a non-empty value means SCA was completed).</summary>
    public const string AcrHeader = "X-SCA-Acr";

    /// <summary>The gateway-attested SCA freshness header (the OIDC <c>auth_time</c> claim, seconds since
    /// the Unix epoch — when SCA happened).</summary>
    public const string AuthTimeHeader = "X-SCA-Auth-Time";

    /// <summary>The gateway-attested RFC 8705 mTLS-bound sender-constraint thumbprint (the step-up
    /// token's <c>cnf.x5t#S256</c> Kong validated against the presented client cert and attested,
    /// ADR-IC-010 §A8). A non-empty value means the refreshed step-up token was sender-constrained —
    /// a stolen token replayed from a different sender was already rejected at the gateway (its
    /// <c>cnf</c> did not match the presented cert), so the engine ACCEPTS this attested binding as
    /// additive context and never re-derives it. Empty/absent means a plain (POC-legacy) Bearer.</summary>
    public const string CnfX5tHeader = "X-SCA-Cnf-X5t";

    /// <summary>The stable refusal code the engine returns and the MCP money-mover tool keys its
    /// step-up-then-retry on. Kept in lock-step with the Kong REST-route SCA gate's code.</summary>
    public const string RequiredCode = "SCA_REQUIRED";

    /// <summary>The SCA-completion freshness window in seconds (mirrors the Kong REST-route
    /// <c>SCA_MAX_AGE</c>). A money-mover is PIS-equivalent (Document 10): SCA must be recent, not merely
    /// ever-completed. 300 s (5 min) is the POC default — tighten via IAM/edge policy.</summary>
    public const long MaxAgeSeconds = 300;

    /// <summary>
    /// Returns a <c>422 SCA_REQUIRED</c> <see cref="IResult"/> when the gateway-attested SCA proof is
    /// absent, weak, or stale; <c>null</c> when fresh SCA is present (the caller proceeds to settle).
    /// </summary>
    /// <remarks>
    /// <paramref name="now"/> is the host's wall-clock instant (the impure shell owns the clock,
    /// ADR-PC-010 §P5 — this is a precondition CHECK, never folded into the pure decider). The endpoint
    /// passes <c>TimeProvider.GetUtcNow()</c>; a test passes a fixed instant. <paramref name="headers"/>
    /// is the inbound request's header collection.
    /// </remarks>
    public static IResult? Check(IHeaderDictionary headers, DateTimeOffset now)
    {
        var acr = headers[AcrHeader].ToString();
        if (string.IsNullOrWhiteSpace(acr))
        {
            return Denied();
        }

        var authTimeRaw = headers[AuthTimeHeader].ToString();
        if (!long.TryParse(authTimeRaw, out var authTime))
        {
            return Denied();
        }

        var nowSeconds = now.ToUnixTimeSeconds();
        // Fail-closed on a future auth_time (clock skew / a forged-shaped value) and on a stale one.
        if (authTime > nowSeconds || (nowSeconds - authTime) > MaxAgeSeconds)
        {
            return Denied();
        }

        return null;
    }

    /// <summary>The single fail-closed verdict — one 422 + stable code for every SCA failure, so no
    /// branch leaks a distinguishing signal. No PII (ADR-PC-004 §P2): a stable code + generic message.</summary>
    private static IResult Denied() =>
        Results.Problem(
            "Strong Customer Authentication (PSD2 SCA) is required for this operation. Complete the "
                + "step-up challenge and retry with a refreshed token (ADR-IC-010 §P8).",
            statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?> { ["code"] = RequiredCode });
}
