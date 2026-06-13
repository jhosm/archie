namespace Babelstone.Orchestrator.Edge;

/// <summary>
/// The edge's per-process authorization surface (ADR-IC-006 §P4 / Document 05 §Step 0). The Kong
/// gateway validates the bearer token's SIGNATURE and the PSD2 SCA claim (ADR-IC-006 §P2/§P4); the
/// APPLICATION enforces the per-process OWNERSHIP check — that the requester's <c>client_id</c>
/// matches the process's owning client. This two-layer split is the mitigation ADR-IC-006 §P4
/// names for Document 05's authorization note: "the <c>process_id</c> in the URL is not a capability
/// token". A client that guesses or obtains another client's <c>process_id</c> must not receive
/// their saga updates.
/// </summary>
/// <remarks>
/// Kong propagates the validated identity to the upstream as a signed assertion (Document 05 §Step 0
/// "claims propagated as signed assertions"). In this POC the application reads the propagated
/// <c>client_id</c> from the <see cref="ClientIdHeader"/> request header — the trust boundary is
/// Kong (Boundary 1, Document 10): only Kong-fronted, mTLS-authenticated traffic reaches the
/// orchestrator (ADR-IC-006 §P5), so the header is the gateway's attested claim, not client-supplied
/// trust. The header value is an OPAQUE business reference (e.g. <c>CLI-2026-007842</c>), never PII.
/// </remarks>
public static class EdgeAuth
{
    /// <summary>The request header carrying the gateway-attested caller <c>client_id</c> (the
    /// propagated, signed identity, Document 05 §Step 0). The application enforces process
    /// ownership against this value.</summary>
    public const string ClientIdHeader = "X-Client-Id";
}
