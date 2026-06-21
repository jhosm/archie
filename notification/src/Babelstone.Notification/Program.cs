using Babelstone.EventStore;
using Babelstone.Notification;
using Babelstone.Telemetry;
using Babelstone.Telemetry.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// The in-house notification worker host (ADR-IC-011 runtime — .NET, stack-coherent with the engine;
// ADR-IC-013 in-house estate placement; ADR-PC-019 §P2 extraction-ready subtree). A per-service
// OUTBOX worker (ADR-IC-004): a long-running BackgroundService host, NOT an HTTP API — so
// Host.CreateApplicationBuilder (the worker host), the same shape the engine's outbox relay and the
// orchestrator's consume loop run as hosted services. This skeleton stands up the host + the
// ADR-IC-005 projection READ access only — NO scheduler timing, NO event emission (those are the
// downstream children bd babelstone-60n8.2 / .3).
var builder = Host.CreateApplicationBuilder(args);

// OpenTelemetry tracing (ADR-IC-007 Layer 1): turn ON the tracer for this host and export over OTLP
// to the Collector (§P1 — never direct-to-backend). The host opens spans on the SHARED
// Babelstone.Engine ActivitySource (BabelstoneTelemetry.ActivitySourceName) — one instrumentation
// scope across the estate, never a competing source — so the notification service's work shows up in
// the same trace surface as the engine and orchestrator. Npgsql's built-in query instrumentation
// (AddNpgsqlQueryTelemetry, K.5) makes each projection READ a CLIENT span on this same provider, so
// a slow read-surface query is visible in Tempo/Grafana with no per-call wiring. The resource stamps
// service.name=babelstone-notification + service.namespace=babelstone + deployment.environment so
// every span is attributable (OBS-1); ResolveEnvironment fails fast on an unset environment.
//
// NB: NO AspNetCore instrumentation (this host has no inbound HTTP surface) and NO HttpClient
// instrumentation yet (no outbound calls until the emission child). Tracing only — the skeleton adds
// no MeterProvider; the metrics leg lands with the emission/scheduler children that produce
// instruments.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(BabelstoneResource.NotificationServiceName)
        .AddAttributes(
        [
            new KeyValuePair<string, object>(BabelstoneResource.ServiceNamespaceKey, BabelstoneResource.ServiceNamespace),
            new KeyValuePair<string, object>(BabelstoneResource.DeploymentEnvironmentKey, BabelstoneResource.ResolveEnvironment()),
        ]))
    .WithTracing(tracing => tracing
        .AddSource(BabelstoneTelemetry.ActivitySourceName)
        // Npgsql per-command CLIENT spans for the projection reads (K.5), on THIS same provider so
        // they share the host's resource + exporter.
        .AddNpgsqlQueryTelemetry()
        .AddOtlpExporter());

// The RUNTIME-role connection the notification service READS the engine's projections through
// (ADR-IC-005). The credential resolves from configuration at the composition root — the same
// ADR-PC-004 Amendment A1 boundary the engine and orchestrator hosts use — and carries a SELECT-only
// grant on the read-model store (a reader never writes a projection). It NEVER rides a message nor
// the durable bus (ADR-PC-004 §P2). The read surface is PostgreSQL because ADR-IC-005 makes it the
// sole read-model storage technology.
var readModelConnectionString =
    builder.Configuration.GetConnectionString("Notification")
    ?? builder.Configuration["Notification:ConnectionString"]
    ?? Environment.GetEnvironmentVariable("NOTIFICATION_CONNECTION_STRING")
    ?? throw new InvalidOperationException(
        "No notification read-model connection string configured. Set " +
        "ConnectionStrings:Notification, Notification:ConnectionString, or " +
        "NOTIFICATION_CONNECTION_STRING (the ADR-IC-005 read surface — PostgreSQL).");

// The byte-oriented projection boundary onto the engine's read-model store (ADR-IC-005 / ADR-PC-002
// §P1). Hand-rolled, Npgsql-only — the same store the engine writes its projections through, read
// here over the runtime-role credential.
builder.Services.AddSingleton<IProjectionStorage>(
    _ => new PostgresProjectionStore(readModelConnectionString));

// The typed read window onto the three term-deposit projections the notification service needs
// (maturity_calendar / accrual_schedule / withholding_ledger — all registered today in
// TermDepositProjectionModule). Read-only; no timing, no emission (bd babelstone-60n8.1).
builder.Services.AddSingleton<TermDepositProjectionReader>();

// The host shell — the standing BackgroundService the maturity scheduler (bd babelstone-60n8.2) and
// the NotificationDue emission (bd babelstone-60n8.3) will later run inside. Skeleton: it idles, it
// does not schedule or emit.
builder.Services.AddHostedService<NotificationWorker>();

var app = builder.Build();
await app.RunAsync();

namespace Babelstone.Notification
{
    /// <summary>
    /// Marker partial so the host assembly exposes a public type the test project can name when it
    /// asserts the composition (the top-level statements above compile into <c>Program</c>). No
    /// behaviour — the host is composed by the statements above.
    /// </summary>
    public sealed partial class Program;
}
