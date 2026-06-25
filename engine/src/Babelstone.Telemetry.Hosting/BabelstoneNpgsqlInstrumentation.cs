using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Babelstone.Telemetry.Hosting;

/// <summary>
/// Wires Npgsql's BUILT-IN OpenTelemetry instrumentation (K.5, bd scd2.3) onto the OTel providers a
/// host already configures (ADR-IC-007 Layer 1). In plain terms: every database call the engine
/// makes to PostgreSQL — the event-store appends, the outbox drain + lag observer, the projection
/// and checkpoint stores, the rate-sheet store — now produces a query-level trace span and feeds the
/// standard <c>db.client.operation.duration</c> latency histogram, so a slow or stuck query is
/// visible in Tempo/Grafana right next to the manual <c>deposit.*</c> spans, with no per-call wiring.
///
/// <para>
/// It works because every engine Postgres call runs on a plain <see cref="NpgsqlConnection"/> /
/// <c>NpgsqlCommand</c>, and Npgsql's instrumentation hooks the driver GLOBALLY: once
/// <see cref="AddNpgsqlQueryTelemetry(TracerProviderBuilder)"/> registers the <c>Npgsql</c>
/// ActivitySource on the host's existing <see cref="TracerProviderBuilder"/> and
/// <see cref="AddNpgsqlQueryTelemetry(MeterProviderBuilder)"/> registers the <c>Npgsql</c> meter on
/// the existing <see cref="MeterProviderBuilder"/>, the spans and the histogram flow through the
/// SAME provider (and so the SAME OTLP exporter + resource, OBS-1) the engine already stands up —
/// never a second, parallel provider. When no provider lists the <c>Npgsql</c> source/meter the
/// instrumentation is a near-zero-cost no-op, exactly like the manual <see cref="Babelstone.Telemetry"/>
/// source.
/// </para>
///
/// <para>
/// This is the engine's CLIENT-tier complement to the manual product-semantic spans
/// (<c>accrual.computed</c> / <c>withholding.applied</c>, ADR-IC-007 P2) and the request-tier
/// <c>AddAspNetCoreInstrumentation</c> SERVER span: server span → manual product spans → these
/// Npgsql query spans, one connected trace per request. It carries the OTel database semantic-
/// convention attributes (db.system, db.namespace, …) — structural, never PII (ADR-IC-007 P4 /
/// ADR-PC-004 §P2): the instrumentation tags the operation, not parameter values.
/// </para>
///
/// <para>
/// <b>Packaging note (bd njt2.9):</b> this project keeps <c>Npgsql.OpenTelemetry</c> as a
/// <c>PrivateAssets="all"</c> dependency, so it is NOT carried transitively to projects that merely
/// reference this seam for the SDK-free <c>AddBabelstonePiiGuard</c> guard (the DB-free notification
/// host, ADR-IC-019, must not gain a Postgres driver). Therefore any host that actually CALLS
/// <see cref="AddNpgsqlQueryTelemetry(TracerProviderBuilder)"/> must declare
/// <c>&lt;PackageReference Include="Npgsql.OpenTelemetry" /&gt;</c> in its own <c>.csproj</c> — otherwise
/// the assembly is absent at runtime and the call throws <see cref="System.IO.FileNotFoundException"/>
/// at host startup. The engine API and rate-sheets API hosts (and this project's test) do so.
/// </para>
/// </summary>
public static class BabelstoneNpgsqlInstrumentation
{
    /// <summary>
    /// The instrumentation-scope name Npgsql's built-in OTel emits its query spans AND its
    /// <c>db.client.operation.duration</c> histogram under (the driver names both its
    /// <c>ActivitySource</c> and its <c>Meter</c> <c>"Npgsql"</c>). Registering this exact string on
    /// a provider — which <c>AddNpgsql()</c> / <c>AddNpgsqlInstrumentation()</c> do — is what turns
    /// the instrumentation on; the fitness test asserts the registration by this name, so a future
    /// driver rename surfaces here rather than silently dropping the engine's query telemetry.
    /// </summary>
    public const string InstrumentationScopeName = "Npgsql";

    /// <summary>
    /// Registers Npgsql's per-command CLIENT spans on the host's EXISTING tracer provider (ADR-IC-007
    /// Layer 1). Call this inside the same <c>WithTracing(...)</c> lambda that already does
    /// <c>AddSource(BabelstoneTelemetry.ActivitySourceName)</c> + <c>AddOtlpExporter()</c>, so the
    /// query spans share the host's resource and exporter and nest under the request's trace.
    /// </summary>
    public static TracerProviderBuilder AddNpgsqlQueryTelemetry(this TracerProviderBuilder tracing)
        // PII ENVELOPE — DO NOT pass NpgsqlTracingOptions that enable command-text/db.statement tags
        // (ADR-IC-007 §P4 / OBS_NO_PII_ATTRS, ADR-PC-004 §P2). Bare AddNpgsql() uses the driver's
        // default options, which attach the OPERATION shape (db.system, db.namespace, …) to spans but
        // NOT the SQL statement text — so query parameter values never reach the trace backend. The
        // class-level "tags the operation, not parameter values" guarantee holds ONLY while this stays
        // default; enabling statement-text tagging here would be a silent PII regression, so any such
        // change must be a deliberate, reviewed decision at this call site.
        => tracing.AddNpgsql();

    /// <summary>
    /// Registers Npgsql's <c>db.client.operation.duration</c> histogram (and the related connection
    /// metrics) on the host's EXISTING meter provider (ADR-IC-007 Layer 1 / ADR-IC-004 §P4 sibling).
    /// Call this inside the same <c>WithMetrics(...)</c> lambda that already does
    /// <c>AddMeter(BabelstoneTelemetry.MeterName)</c> + <c>AddOtlpExporter()</c>, so the query-latency
    /// histogram is exported alongside the engine's own meter instruments (the outbox-lag SLI et al.)
    /// through one provider — never a second, parallel one.
    /// </summary>
    public static MeterProviderBuilder AddNpgsqlQueryTelemetry(this MeterProviderBuilder metrics)
        => metrics.AddNpgsqlInstrumentation();
}
