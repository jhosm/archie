namespace Babelstone.Orchestrator.Edge;

/// <summary>
/// Mints the client-facing references the edge returns (Document 05 §Step 0): the <c>PROC-…</c>
/// process reference and the <c>DEP-…</c> deposit reference. These are STABLE, opaque, structural
/// handles — NOT secrets and NOT capability tokens (ADR-IC-006 §P4 / Document 05 §Step 0
/// authorization note). The durable saga key stays the internal UUID <c>process_id</c>; the public
/// reference is the handle the client and the SSE <c>stream_url</c> carry.
/// </summary>
/// <remarks>
/// The minting is an IMPURE-SHELL concern: the edge HTTP handler (never the pure saga state machine)
/// generates the underlying GUID and derives the references from it. The derivation is deterministic
/// over the GUID so the public reference and the internal key round-trip without a second lookup
/// table — the GUID's hex is the body of the reference, prefixed for readability. No PII is in or
/// derivable from a reference (ADR-PC-004 §P2).
/// </remarks>
public static class EdgeReferences
{
    /// <summary>The <c>PROC-…</c> prefix for a constitution process reference (Document 05).</summary>
    public const string ProcessPrefix = "PROC-";

    /// <summary>The <c>DEP-…</c> prefix for a deposit reference (Document 05).</summary>
    public const string DepositPrefix = "DEP-";

    /// <summary>Derive the public <c>PROC-…</c> reference for an internal saga
    /// <paramref name="processId"/>. Deterministic: the same GUID always yields the same reference.</summary>
    public static string ProcessReference(Guid processId) => ProcessPrefix + processId.ToString("N");

    /// <summary>Derive the public <c>DEP-…</c> reference for an internal saga
    /// <paramref name="processId"/>. Deterministic, derived from the same GUID so the deposit and
    /// process references stay paired without a second store.</summary>
    public static string DepositReference(Guid processId) => DepositPrefix + processId.ToString("N");
}
