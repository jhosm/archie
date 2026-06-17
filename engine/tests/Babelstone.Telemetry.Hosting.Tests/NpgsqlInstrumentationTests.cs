using System.Diagnostics;
using System.Diagnostics.Metrics;
using Babelstone.Telemetry.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Babelstone.Telemetry.Hosting.Tests;

/// <summary>
/// K.5 (bd scd2.3): the engine wires Npgsql 8/10's BUILT-IN OpenTelemetry instrumentation so every
/// engine Postgres call (event-store appends, outbox drain + lag observer, projection/checkpoint
/// stores, rate-sheet store) produces a query span and feeds the stable
/// <c>db.client.operation.duration</c> latency histogram. This Docker-free fitness test builds the
/// SAME tracer/meter providers the two hosts build — via the shared
/// <see cref="BabelstoneNpgsqlInstrumentation.AddNpgsqlQueryTelemetry(TracerProviderBuilder)"/> /
/// <see cref="BabelstoneNpgsqlInstrumentation.AddNpgsqlQueryTelemetry(MeterProviderBuilder)"/> seam —
/// and asserts the Npgsql instrumentation is actually registered on them. It needs no PostgreSQL
/// because the registration is what the host wires (the driver emits at query time); proving the
/// registration proves the host honours ADR-IC-007 Layer 1 on the EXISTING provider, never a second
/// parallel one.
/// </summary>
public sealed class NpgsqlInstrumentationTests
{
    /// <summary>
    /// Tracing: a provider built through the engine's <c>AddNpgsqlQueryTelemetry()</c> seam listens to
    /// the <c>Npgsql</c> ActivitySource — so the driver's per-command CLIENT spans are captured. A bare
    /// <see cref="ActivitySource"/> with no listener reports <c>HasListeners() == false</c>; once the
    /// provider registers the source the SDK attaches a listener and the flag flips. This is exactly the
    /// switch that turns the engine's query-span telemetry on.
    /// </summary>
    [Fact]
    public void Tracer_provider_built_through_the_seam_listens_to_the_Npgsql_source()
    {
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddNpgsqlQueryTelemetry()
            .Build();

        using var npgsqlSource = new ActivitySource(BabelstoneNpgsqlInstrumentation.InstrumentationScopeName);

        Assert.True(
            npgsqlSource.HasListeners(),
            "AddNpgsqlQueryTelemetry() must register the 'Npgsql' ActivitySource on the tracer provider " +
            "so the driver's query CLIENT spans are captured (ADR-IC-007 Layer 1, K.5).");
    }

    /// <summary>
    /// Metrics: a provider built through the engine's <c>AddNpgsqlQueryTelemetry()</c> seam collects the
    /// stable <c>db.client.operation.duration</c> histogram from the <c>Npgsql</c> meter. We reproduce the
    /// driver's own instrument (Npgsql's <c>MetricsReporter</c> creates a histogram of this exact name on
    /// a <c>Meter("Npgsql")</c>), record one observation, force-flush, and assert it landed — proving both
    /// that the <c>Npgsql</c> meter is registered on THIS provider and that the headline query-latency
    /// metric name flows through it.
    /// </summary>
    [Fact]
    public void Meter_provider_built_through_the_seam_collects_the_db_client_operation_duration_histogram()
    {
        var exported = new List<Metric>();
        using var provider = Sdk.CreateMeterProviderBuilder()
            .AddNpgsqlQueryTelemetry()
            .AddInMemoryExporter(exported)
            .Build();

        // Stand in for Npgsql's internal MetricsReporter: the same meter name + the same stable OTel
        // instrument name the driver emits at query time.
        using var npgsqlMeter = new Meter(BabelstoneNpgsqlInstrumentation.InstrumentationScopeName);
        var operationDuration = npgsqlMeter.CreateHistogram<double>(
            "db.client.operation.duration", unit: "s", description: "Duration of database client operations.");
        operationDuration.Record(0.012);

        provider.ForceFlush();

        // SCOPE OF THIS ASSERTION: it proves registration-BY-METER-NAME — that the seam's
        // AddNpgsqlInstrumentation() registers AddMeter("Npgsql") on THIS provider, so any instrument
        // emitted on a Meter("Npgsql") flows through. It does NOT exercise the driver's real
        // MetricsReporter, so it cannot catch a future driver RENAME of the literal emitted instrument
        // (e.g. "db.client.operation.duration" → something else) — that would only surface at runtime in
        // Grafana. Read this as a registration guard, not an end-to-end metric-name contract.
        var metric = Assert.Single(exported, m => m.Name == "db.client.operation.duration");
        Assert.Equal(MetricType.Histogram, metric.MetricType);
        Assert.Equal(BabelstoneNpgsqlInstrumentation.InstrumentationScopeName, metric.MeterName);
    }

    /// <summary>
    /// Guards the no-second-provider constraint and the scope-name contract: the seam registers the
    /// driver's own <c>Npgsql</c> scope, not a Babelstone-renamed one, so spans and the histogram carry
    /// the standard instrumentation scope a Grafana/Tempo query keys on.
    /// <para>
    /// Deliberately a constant pin with little INDEPENDENT mutation-coverage value: the two behavioural
    /// tests above already self-correct on constant drift (they build the source/meter from this same
    /// constant, so a wrong value flips them red). It is kept as an explicit, cheap contract pin of the
    /// literal scope string a downstream Grafana/Tempo query depends on.
    /// </para>
    /// </summary>
    [Fact]
    public void Instrumentation_scope_name_is_the_drivers_own_Npgsql_scope()
        => Assert.Equal("Npgsql", BabelstoneNpgsqlInstrumentation.InstrumentationScopeName);
}
