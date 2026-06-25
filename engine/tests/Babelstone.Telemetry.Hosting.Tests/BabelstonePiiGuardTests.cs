using System.Diagnostics;
using System.Diagnostics.Metrics;
using Babelstone.Telemetry;
using Babelstone.Telemetry.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Babelstone.Telemetry.Hosting.Tests;

/// <summary>
/// The RUNTIME emit-time no-PII guard (bd njt2.9–2.11; commitment OBS_NO_PII_ATTRS / ADR-IC-007 §P4):
/// the load-bearing leg of OBS-3. These Docker-free fitness tests build the SAME tracer / logger / meter
/// providers the three hosts build — via the shared <see cref="BabelstonePiiGuard.AddBabelstonePiiGuard(TracerProviderBuilder)"/>
/// seam — and prove that a span tag, a structured-log field, and a metric dimension carrying personal
/// data are stripped AT EMIT, while the admitted babelstone.*/semantic-convention tier and the
/// operational §P5 log references survive (so Tempo/Grafana are not blinded).
/// </summary>
public sealed class BabelstonePiiGuardTests
{
    private const string TestSourceName = "Babelstone.PiiGuard.Tests";

    /// <summary>
    /// Traces (njt2.9): a span tag whose key is outside the admitted babelstone.*/semantic-convention
    /// tier is dropped at OnEnd before export, while the babelstone.* contract keys AND the Npgsql/AspNetCore
    /// semantic-convention namespaces (db.*, http.*, server.* — note server.address survives even though it
    /// contains the 'address' PII fragment, because the namespace allowlist is checked first) are kept.
    /// </summary>
    [Fact]
    public void Trace_processor_strips_non_admitted_span_tags_and_keeps_the_admitted_tier()
    {
        var exported = new List<Activity>();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(TestSourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddBabelstonePiiGuard()
            .AddInMemoryExporter(exported)
            .Build();

        using (var source = new ActivitySource(TestSourceName))
        using (var activity = source.StartActivity("op"))
        {
            Assert.NotNull(activity);
            // Admitted: babelstone.* contract + semantic-convention namespaces the auto-instrumentation emits.
            activity!.SetTag(BabelstoneAttributes.PartitionKey, "stream-1");
            activity.SetTag("db.system", "postgresql");
            activity.SetTag("http.request.method", "GET");
            activity.SetTag("server.address", "db.internal");          // contains 'address' — admitted via server.
            // Stripped: a PII-named key and a bare un-namespaced key, neither in an admitted namespace.
            activity.SetTag("client_nif", "234567891");
            activity.SetTag("Account", "PT50003300004516123456705");
        }

        provider.ForceFlush();

        var span = Assert.Single(exported);
        Assert.Equal("stream-1", span.GetTagItem(BabelstoneAttributes.PartitionKey));
        Assert.Equal("postgresql", span.GetTagItem("db.system"));
        Assert.Equal("GET", span.GetTagItem("http.request.method"));
        Assert.Equal("db.internal", span.GetTagItem("server.address"));
        Assert.Null(span.GetTagItem("client_nif"));
        Assert.Null(span.GetTagItem("Account"));
    }

    /// <summary>
    /// The corrective signal (njt2.9): each stripped tag increments the
    /// <c>telemetry_pii_attributes_stripped_total</c> counter, tagged by signal — so a strip is never
    /// silently fail-open, and the counter's own <c>babelstone.telemetry_signal</c> dimension survives the
    /// metric View's allowlist (proving the guard's metric leg admits its own corrective dimension).
    /// </summary>
    [Fact]
    public void Trace_strip_increments_the_corrective_counter_under_the_metric_view()
    {
        var metrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(BabelstoneTelemetry.MeterName)
            .AddBabelstonePiiGuard()
            .AddInMemoryExporter(metrics)
            .Build();

        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(TestSourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddBabelstonePiiGuard()
            .Build();

        using (var source = new ActivitySource(TestSourceName))
        using (var activity = source.StartActivity("op"))
        {
            activity?.SetTag("client_nif", "234567891"); // one stripped tag → one corrective increment
        }

        meterProvider.ForceFlush();

        var counter = Assert.Single(metrics, m => m.Name == BabelstonePiiGuard.StrippedAttributesMetricName);
        var traceTotal = 0L;
        var sawSignalDimension = false;
        foreach (ref readonly var point in counter.GetMetricPoints())
        {
            foreach (var tag in point.Tags)
            {
                if (tag.Key == BabelstonePiiGuard.TelemetrySignalTagKey && (string?)tag.Value == "trace")
                {
                    sawSignalDimension = true;
                    traceTotal += point.GetSumLong();
                }
            }
        }

        Assert.True(sawSignalDimension, "the corrective counter must carry its babelstone.telemetry_signal dimension through the View");
        Assert.True(traceTotal >= 1, "a stripped span tag must increment the corrective counter for signal=trace");
    }

    /// <summary>
    /// Logs (njt2.10): a structured-log field whose un-namespaced key carries a PII fragment (e.g. a
    /// <c>{Account}</c> message-template hole) is stripped at OnEnd before export, while the operational
    /// un-namespaced fields §P5 relies on (a correlation reference, integer cents) and the deposit id are
    /// kept — the log model the strict trace allowlist could not express without nuking every log field.
    /// </summary>
    [Fact]
    public void Log_processor_strips_pii_fragment_fields_and_keeps_operational_fields()
    {
        var captured = new CapturingLogProcessor();
        using var factory = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options =>
        {
            options.AddProcessor(new BabelstoneLogRecordTierProcessor()); // the guard runs first…
            options.AddProcessor(captured);                               // …then we copy the post-strip attributes.
        }));

        factory.CreateLogger("test").LogInformation(
            "settlement on {Account} for deposit {deposit_id} corr {correlation_id} amount {AmountCents}",
            "PT50003300004516123456705", Guid.NewGuid(), "corr-7", 5000L);

        var attributes = captured.Captured;
        Assert.NotNull(attributes);
        Assert.DoesNotContain(attributes!, kv => kv.Key == "Account");   // PII fragment 'account' → stripped
        Assert.Contains(attributes!, kv => kv.Key == "deposit_id");      // §P5 reference → kept
        Assert.Contains(attributes!, kv => kv.Key == "correlation_id");  // §P5 reference → kept
        Assert.Contains(attributes!, kv => kv.Key == "AmountCents");     // operational, no fragment → kept
    }

    /// <summary>
    /// Metrics (njt2.11): the View's explicit TagKeys allowlist drops a metric dimension whose key is
    /// outside the admitted operational tier at emit, while an admitted dimension (the saga-dispatch
    /// <c>command_type</c>) is kept — the only emit-time metric filter (a processor cannot touch metrics).
    /// </summary>
    [Fact]
    public void Metric_view_drops_non_admitted_dimension_and_keeps_admitted()
    {
        var metrics = new List<Metric>();
        using var provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(BabelstoneTelemetry.MeterName)
            .AddBabelstonePiiGuard()
            .AddInMemoryExporter(metrics)
            .Build();

        using var meter = new Meter(BabelstoneTelemetry.MeterName);
        var counter = meter.CreateCounter<long>("test_dimension_filter_total");
        counter.Add(
            1,
            new KeyValuePair<string, object?>("command_type", "ConstituteDeposit"), // admitted
            new KeyValuePair<string, object?>("client_nif", "234567891"));           // NOT admitted → dropped

        provider.ForceFlush();

        var metric = Assert.Single(metrics, m => m.Name == "test_dimension_filter_total");
        var tagKeys = new List<string>();
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            foreach (var tag in point.Tags)
            {
                tagKeys.Add(tag.Key);
            }
        }

        Assert.Contains("command_type", tagKeys);
        Assert.DoesNotContain("client_nif", tagKeys);
    }

    /// <summary>Copies each ending record's post-strip attributes into a stable list (LogRecord is pooled,
    /// so we snapshot rather than retain the record).</summary>
    private sealed class CapturingLogProcessor : BaseProcessor<LogRecord>
    {
        public List<KeyValuePair<string, object?>>? Captured { get; private set; }

        public override void OnEnd(LogRecord data)
            => Captured = data.Attributes?.ToList();
    }
}
