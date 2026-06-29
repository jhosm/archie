namespace Babelstone.Engine.Api;

/// <summary>
/// The response-header contract by which the engine hands the active trace id back to the caller
/// (ADR-IC-007 Layer 1 — Consequences "Surfacing the trace id to the caller").
///
/// <para>The chosen mechanism is an EXPLICIT response header carrying the raw 32-hex W3C trace id
/// (<see cref="System.Diagnostics.Activity.TraceId"/> of the inbound request's SERVER span), NOT
/// the W3C <c>traceresponse</c> header (Trace Context Level 2). Trace Context Level 2 was the other
/// option in the issue; the explicit header is chosen because the immediate consumer is a browser
/// (Mission Control's Telemetry tab) that queries Grafana Tempo by trace id —
/// it needs the bare id, not the <c>00-&lt;trace-id&gt;-&lt;span-id&gt;-&lt;flags&gt;</c> framing it
/// would have to re-parse. The header value therefore equals <c>Activity.Current.TraceId</c>.</para>
///
/// <para>The trace id is an opaque hex identifier minted by the tracer — never a subject reference,
/// NIF, IBAN, or any other personal/financial value (ADR-IC-007 §P4 operational tier /
/// ADR-PC-004 §P2 — no PII on the trace-id surface).</para>
///
/// <para>The <c>X-</c> prefix is deprecated by RFC 6648 in general, but is kept here for consistency
/// with the existing edge convention (<c>X-Client-Id</c>, attested at the Kong edge).</para>
/// </summary>
public static class TraceResponseHeader
{
    /// <summary>The response-header name. Stable wire contract — a caller reads the trace id by this exact string.</summary>
    public const string Name = "X-Trace-Id";
}
