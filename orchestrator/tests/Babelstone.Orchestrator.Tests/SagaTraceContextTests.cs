using System.Diagnostics;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Telemetry;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// Docker-free unit tests for the W3C Trace Context extract/inject seam (H.5,
/// <see cref="SagaTraceContext"/>) — the pure string ↔ <see cref="ActivityContext"/> conversion
/// that turns the saga's identity trio into a CONNECTED distributed trace (ADR-IC-007 Layer 1:
/// "its W3C Trace Context propagation (traceparent header) is the mechanism by which the identity
/// trio … becomes distributed tracing"). No DB, no Integration trait: the helper neither mints ids
/// nor reads a clock, so it is asserted directly.
/// </summary>
public sealed class SagaTraceContextTests
{
    private const string SampleTraceId = "0af7651916cd43dd8448eb211c80319c";
    private const string SampleSpanId = "b7ad6b7169203331";
    private const string SampleTraceParent = $"00-{SampleTraceId}-{SampleSpanId}-01";

    [Fact]
    public void Parses_a_valid_inbound_traceparent_into_a_remote_parent_context()
    {
        var context = SagaTraceContext.ParseTraceParent(SampleTraceParent);

        Assert.Equal(SampleTraceId, context.TraceId.ToString());
        Assert.Equal(SampleSpanId, context.SpanId.ToString());
        Assert.True(context.IsRemote, "An inbound (cross-process) parent must be marked remote per W3C propagation.");
        Assert.Equal(ActivityTraceFlags.Recorded, context.TraceFlags & ActivityTraceFlags.Recorded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-traceparent")]
    [InlineData("00-tooshort-tooshort-01")]
    public void A_missing_or_malformed_traceparent_yields_the_default_context_not_a_throw(string? header)
    {
        // A saga arriving with no upstream trace (or a garbled header) ROOTS a new trace rather
        // than throwing — a missing/garbled trace header is never a poison condition (the dedup
        // identity is the ce_id, not the trace header).
        var context = SagaTraceContext.ParseTraceParent(header);
        Assert.Equal(default, context);
    }

    [Fact]
    public void Formats_a_live_activitys_context_as_a_W3C_traceparent_that_round_trips()
    {
        // A listener is required for StartActivity to return a live (sampled) Activity.
        using var listener = AllDataListener();
        ActivitySource.AddActivityListener(listener);

        using var activity = BabelstoneTelemetry.ActivitySource.StartActivity(
            BabelstoneAttributes.SpanSagaAdvance, ActivityKind.Consumer);
        Assert.NotNull(activity);

        var traceParent = SagaTraceContext.FormatTraceParent(activity);
        Assert.NotNull(traceParent);

        // The injected header re-parses to the SAME trace + span (the outbound header a downstream
        // consumer would parent under).
        var reparsed = SagaTraceContext.ParseTraceParent(traceParent);
        Assert.Equal(activity!.TraceId.ToString(), reparsed.TraceId.ToString());
        Assert.Equal(activity.SpanId.ToString(), reparsed.SpanId.ToString());
        Assert.StartsWith("00-", traceParent);
    }

    [Fact]
    public void Format_of_a_null_activity_is_null()
    {
        // No tracer listening ⇒ StartActivity returned null ⇒ nothing to propagate ⇒ the outbound
        // header is simply absent and the downstream consumer roots its own trace.
        Assert.Null(SagaTraceContext.FormatTraceParent(null));
    }

    [Fact]
    public void A_started_span_parents_onto_the_inbound_traceparents_trace()
    {
        // The advance span (opened by the handler) hangs off the inbound traceparent: same trace id,
        // the inbound span id as its parent — the connecting link of the distributed trace.
        using var listener = AllDataListener();
        ActivitySource.AddActivityListener(listener);

        var parent = SagaTraceContext.ParseTraceParent(SampleTraceParent);
        using var child = BabelstoneTelemetry.ActivitySource.StartActivity(
            BabelstoneAttributes.SpanSagaAdvance, ActivityKind.Consumer, parent);

        Assert.NotNull(child);
        Assert.Equal(SampleTraceId, child!.TraceId.ToString());
        Assert.Equal(SampleSpanId, child.ParentSpanId.ToString());
    }

    private static ActivityListener AllDataListener() => new()
    {
        ShouldListenTo = source => source.Name == BabelstoneTelemetry.ActivitySourceName,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    };
}
