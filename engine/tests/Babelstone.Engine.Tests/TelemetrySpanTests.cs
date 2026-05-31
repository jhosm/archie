using System.Diagnostics;
using Babelstone.Engine;
using Babelstone.Telemetry;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// OBS_SPAN_PRODUCT_SEMANTICS (OBS-2, ADR-IC-007): the product-semantic spans
/// (<c>accrual.computed</c>, <c>withholding.applied</c>) are emitted in the IMPURE runtime shell
/// (<see cref="AggregateRuntime{TState}.AppendAsync"/>'s optional span hook), never in the pure
/// decider/fold (ADR-PC-010 §P5), and they carry the structural <c>babelstone.*</c> identifiers
/// (OBS-3 — partition_key + product_code) with NO PII keys (ADR-PC-004 §P2).
///
/// Docker-free: drives the generic runtime over the in-memory <see cref="RecordingSink"/> (the A.5
/// fake) with the counter test family — no PostgreSQL, no Integration trait. An
/// <see cref="ActivityListener"/> on the shared source captures the spans, exactly as a real OTel
/// tracer provider's <c>AddSource(BabelstoneTelemetry.ActivitySourceName)</c> would.
/// </summary>
public sealed class TelemetrySpanTests
{
    // ADR-IC-007 P4 tiers 2/3: any of these as a span-tag key is a GDPR incident in the trace
    // backend. The fitness assertion is structural — keys, not values — so it stays robust.
    private static readonly string[] PiiKeyFragments =
        ["nif", "iban", "account", "name", "email", "client", "phone", "address", "tax_id"];

    [Fact]
    public async Task Accrual_and_withholding_spans_are_emitted_with_structural_keys_and_no_pii()
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BabelstoneTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = captured.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var streamId = Guid.NewGuid();
        const string productCode = "TD-PT-12M";
        var runtime = new AggregateRuntime<CounterState>(
            store: null!, new NullSink(), CounterFamilyModule.Registry(), new JsonEventSerializer(),
            new NullPiiProtector(), new FixedTimeProvider(DateTimeOffset.UnixEpoch), () => new CounterState(0));
        var context = new AppendContext("counter", "pt.2026.1", "counter@2026.1", "test", DateTimeOffset.UnixEpoch);

        // Drive an accrual then a withholding through the instrumented runtime path. The caller
        // (here, standing in for DepositsEndpoints) supplies the span name + structural attribute
        // values; the generic runtime stays domain-agnostic.
        await runtime.AppendAsync(
            streamId, -1, [new Incremented(100)], context, default,
            BabelstoneAttributes.SpanAccrualComputed,
            [
                new(BabelstoneAttributes.PartitionKey, streamId.ToString()),
                new(BabelstoneAttributes.ProductCode, productCode),
                new(BabelstoneAttributes.InterestCents, 100L),
            ]);
        await runtime.AppendAsync(
            streamId, 0, [new Incremented(25)], context, default,
            BabelstoneAttributes.SpanWithholdingApplied,
            [
                new(BabelstoneAttributes.PartitionKey, streamId.ToString()),
                new(BabelstoneAttributes.ProductCode, productCode),
                new(BabelstoneAttributes.TaxCents, 25L),
            ]);

        var accrual = Assert.Single(captured, a => a.OperationName == BabelstoneAttributes.SpanAccrualComputed);
        var withholding = Assert.Single(captured, a => a.OperationName == BabelstoneAttributes.SpanWithholdingApplied);

        foreach (var span in new[] { accrual, withholding })
        {
            Assert.Equal(streamId.ToString(), span.GetTagItem(BabelstoneAttributes.PartitionKey));
            Assert.Equal(productCode, span.GetTagItem(BabelstoneAttributes.ProductCode));

            // OBS-3: every tag key is operational-tier (babelstone.* structural), none PII-ish.
            foreach (var tag in span.TagObjects)
            {
                Assert.StartsWith("babelstone.", tag.Key);
                var lowered = tag.Key.ToLowerInvariant();
                Assert.DoesNotContain(PiiKeyFragments, fragment => lowered.Contains(fragment));
            }
        }

        // Money tags are cents-native integers, never a formatted decimal.
        Assert.Equal(100L, accrual.GetTagItem(BabelstoneAttributes.InterestCents));
        Assert.Equal(25L, withholding.GetTagItem(BabelstoneAttributes.TaxCents));
    }

    [Fact]
    public async Task Append_without_a_span_name_emits_no_activity()
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BabelstoneTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = captured.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var runtime = new AggregateRuntime<CounterState>(
            store: null!, new NullSink(), CounterFamilyModule.Registry(), new JsonEventSerializer(),
            new NullPiiProtector(), new FixedTimeProvider(DateTimeOffset.UnixEpoch), () => new CounterState(0));

        // No spanName ⇒ the runtime opens no span: the impure hook is opt-in by the caller.
        await runtime.AppendAsync(
            Guid.NewGuid(), -1, [new Incremented(1)],
            new AppendContext("counter", "pt.2026.1", "counter@2026.1", "test", DateTimeOffset.UnixEpoch));

        Assert.Empty(captured);
    }
}
