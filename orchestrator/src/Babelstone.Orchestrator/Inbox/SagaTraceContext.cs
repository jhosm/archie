using System.Diagnostics;

namespace Babelstone.Orchestrator.Inbox;

/// <summary>
/// The W3C Trace Context (<c>traceparent</c>) extract/inject seam for the saga (H.5). It turns
/// the identity trio the saga already carries into a CONNECTED distributed trace: the inbound
/// event's <c>traceparent</c> header is parsed into the parent <see cref="ActivityContext"/> the
/// advance span hangs off, and the advance span's own context is serialized back into the
/// <c>traceparent</c> string the outbound command carries — so the saga's spans across services
/// thread into one trace (ADR-IC-007 Layer 1: "its W3C Trace Context propagation
/// (<c>traceparent</c> header) is the mechanism by which the identity trio … becomes distributed
/// tracing"; Document 06 "context propagation between systems").
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure string ↔ context conversion, no I/O, no clock.</b> This is a transport-shape adapter,
/// not a decider: it neither mints ids nor reads the wall clock. The real consume loop (the
/// engine's <c>InboxPump</c>, plugged in via its <c>IInboxMessageHandler</c> seam, t7o3.1) reads
/// the <c>traceparent</c> Kafka header off the inbound record and hands it on the
/// <see cref="SagaInboxEvent"/>; the outbox relay (Epic E's drain) reads the persisted
/// <c>traceparent</c> off the outbox row and re-emits it as the outbound Kafka header. This class
/// owns only the W3C string parsing/formatting between those two edges, kept decoupled from
/// Confluent/Avro exactly like the rest of the substrate.
/// </para>
/// <para>
/// <b>The header is operational, not PII (ADR-PC-004 §P2).</b> A <c>traceparent</c> is
/// <c>00-&lt;trace-id&gt;-&lt;span-id&gt;-&lt;flags&gt;</c> — opaque structural identifiers only,
/// never a NIF/IBAN/name/amount. It correlates to the saga's pseudonymous
/// <c>correlation_id</c>/<c>causation_id</c> (Document 06), so it rides the durable bus safely.
/// </para>
/// </remarks>
public static class SagaTraceContext
{
    /// <summary>
    /// Parse an inbound W3C <c>traceparent</c> header into the parent <see cref="ActivityContext"/>
    /// the advance span hangs off. Returns <see cref="ActivityContext"/> <c>default</c> when the
    /// header is absent or malformed — a saga arriving with no upstream trace simply ROOTS a new
    /// trace rather than throwing (a missing/garbled header is not a poison condition; the dedup
    /// identity is the ce_id, not the trace header). The <c>remote: true</c> flag marks the parent
    /// as coming from another process, per W3C propagation semantics.
    /// </summary>
    public static ActivityContext ParseTraceParent(string? traceParent) =>
        ActivityContext.TryParse(traceParent, traceState: null, isRemote: true, out var context)
            ? context
            : default;

    /// <summary>
    /// Render an activity's context into the outbound W3C <c>traceparent</c> string the emitted
    /// command carries, so the NEXT service threads its spans under this saga's trace. Returns
    /// <c>null</c> when there is no live activity (no tracer listening — the common test/library
    /// path, where <see cref="ActivitySource.StartActivity(string,ActivityKind)"/> returned
    /// <c>null</c>): with nothing to propagate, the outbound header is simply absent and the
    /// downstream consumer roots its own trace. Format per W3C:
    /// <c>00-&lt;trace-id&gt;-&lt;span-id&gt;-&lt;flags&gt;</c>.
    /// </summary>
    public static string? FormatTraceParent(Activity? activity)
    {
        if (activity is null)
        {
            return null;
        }

        // Activity.Id is already the W3C traceparent string when the activity's IdFormat is W3C
        // (the .NET default since Activity.DefaultIdFormat = W3C). Compose it explicitly from the
        // context so the output is stable regardless of ambient Id-format configuration.
        var flags = (activity.ActivityTraceFlags & ActivityTraceFlags.Recorded) != 0 ? "01" : "00";
        return $"00-{activity.TraceId}-{activity.SpanId}-{flags}";
    }
}
