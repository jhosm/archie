using Babelstone.Families.TermDeposit.Notification;
using Babelstone.Notification;
using Babelstone.Telemetry;
using Babelstone.Telemetry.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// The in-house notification worker HOST (ADR-IC-011 runtime — .NET, stack-coherent with the engine;
// ADR-IC-013 in-house estate placement; ADR-PC-019 §P2 extraction-ready subtree). A per-service OUTBOX
// worker (ADR-IC-004): a long-running BackgroundService host, NOT an HTTP API — so
// Host.CreateApplicationBuilder, the same shape the engine's outbox relay and the orchestrator's consume
// loop run as hosted services.
//
// This is the §D4/§A2 COMPOSITION ROOT (ADR-IC-019 §D4 + Amendment 2026-06-24): the ONE place that names a
// family. It holds the explicit list of IFamilyNotificationModule contributions (explicit-list-now,
// assembly-scan-later — ADR-PC-021 §A3), composes each into DI, and runs the family-agnostic core loop. The
// core (Babelstone.Notification) references neither the engine kernel nor any family; the host wires them
// together — gated by NOTIFICATION_FAMILY_AGNOSTIC over the CORE, not this host.
var builder = Host.CreateApplicationBuilder(args);

// OpenTelemetry tracing (ADR-IC-007 Layer 1): turn ON the tracer for this host and export over OTLP to the
// Collector (§P1 — never direct-to-backend). Spans open on the SHARED Babelstone.Engine ActivitySource (from
// the SDK-free Babelstone.Telemetry leaf — NOT the engine kernel), so the notification service's work shows
// up in the same trace surface as the engine and orchestrator. The resource stamps
// service.name=babelstone-notification + service.namespace=babelstone + deployment.environment (OBS-1);
// ResolveEnvironment fails fast on an unset environment.
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
        // The runtime no-PII guard (OBS_NO_PII_ATTRS / ADR-IC-007 §P4, bd njt2.9): strips any non-admitted
        // span tag at OnEnd before export — the load-bearing emit-time control, the same guard the log
        // provider below runs. Registered before the exporter so nothing un-admitted reaches the Collector.
        .AddBabelstonePiiGuard()
        .AddOtlpExporter())
    // Logs (bd njt2.10): a LoggerProvider so structured logs export over OTLP, carrying the log no-PII guard
    // that strips any un-namespaced PII-fragment field before export (§P5's correlation_id/deposit_id survive).
    .WithLogging(logging => logging
        .AddBabelstonePiiGuard()
        .AddOtlpExporter());

// The engine API ENDPOINT the notification service READS deposit facts from (ADR-PC-027 canonical read
// resource; ADR-IC-019 §D3). This is a service ENDPOINT, not a credential, so — like the orchestrator's
// Engine:BaseUrl — it resolves straight from configuration (no ISecretProvider). The read is over the
// storage-opaque HTTP contract, so the notification core never touches the engine's read-model store.
var engineBaseUrl =
    builder.Configuration["Engine:BaseUrl"]
    ?? builder.Configuration.GetConnectionString("Engine")
    ?? Environment.GetEnvironmentVariable("BABELSTONE_ENGINE_BASE_URL")
    ?? throw new InvalidOperationException(
        "No engine API base URL configured. Set Engine:BaseUrl, ConnectionStrings:Engine, or " +
        "BABELSTONE_ENGINE_BASE_URL (the ADR-PC-027 deposit read surface — GET /v1/deposits/{id}).");

// The typed read client over the deposit resource (a CORE, family-agnostic service the family rules consume).
// IHttpClientFactory owns connection pooling and lifetime. BaseAddress is normalised to a trailing "/" so the
// client's relative "v1/deposits/{id}" resolves correctly.
builder.Services.AddHttpClient<DepositReadClient>(client =>
    client.BaseAddress = new Uri(engineBaseUrl.EndsWith('/') ? engineBaseUrl : engineBaseUrl + "/"));

// The wall-clock the worker loop OWNS (ADR-PC-023 §6 — the engine emits no clock-driven signal, so the
// downstream scheduler owns the clock). TimeProvider.System in production; a test substitutes a
// FakeTimeProvider so the loop can be driven with no real wall-clock wait.
builder.Services.AddSingleton(TimeProvider.System);

// The schedule pass's cadence/retry/backoff knobs (ADR-PC-023 §6). Bound from the "Notification"
// configuration section so an operator can tune the poll interval; the generous one-hour default sits well
// inside a reminder's latency tolerance.
var schedulerOptions = new NotificationSchedulerOptions();
var pollSeconds = builder.Configuration.GetValue<double?>("Notification:PollIntervalSeconds");
if (pollSeconds is > 0)
{
    schedulerOptions = new NotificationSchedulerOptions { PollInterval = TimeSpan.FromSeconds(pollSeconds.Value) };
}

builder.Services.AddSingleton(schedulerOptions);

// The slot-4 idempotency ledger (ADR-PC-025): the "already raised this notification_id" memory the schedule
// pass dedupes against. In-memory for v1; a durable, crash-surviving ledger is the emission child's concern
// (bd babelstone-60n8.3).
builder.Services.AddSingleton<INotificationDedupeLedger, InMemoryNotificationDedupeLedger>();

// Compose the family notification contributions (ADR-IC-019 §D4 + Amendment 2026-06-24). The host is the
// §A2 composition root — the only place that names a family. Explicit list now (ADR-PC-021 §A3); a duplicate
// FamilyName is a composition error (the host-edge guard the engine's HostModuleLoader also enforces). A
// second family ships a new module on this same list with zero core diff.
var moduleContext = new NotificationModuleContext(builder.Configuration, engineBaseUrl);
IReadOnlyList<IFamilyNotificationModule> notificationModules =
[
    new TermDepositNotificationModule(),
];

var composedFamilies = new HashSet<string>(StringComparer.Ordinal);
foreach (var module in notificationModules)
{
    if (!composedFamilies.Add(module.FamilyName))
    {
        throw new InvalidOperationException(
            $"Duplicate notification module for family '{module.FamilyName}' (ADR-IC-019 §D4 composition).");
    }

    module.ConfigureServices(builder.Services, moduleContext);
}

// The core's generic per-tick engine over the registered family rules + the dedupe ledger (ADR-IC-019 §D2).
builder.Services.AddSingleton<NotificationSchedulePass>();

// The host shell — the standing BackgroundService the schedule pass runs inside. It OWNS the clock, cadence,
// retry and backoff (ADR-PC-023 §6); the NotificationDue emission over the outbox is bd babelstone-60n8.3.
builder.Services.AddHostedService<NotificationWorker>();

var app = builder.Build();
await app.RunAsync();

namespace Babelstone.Notification.Host
{
    /// <summary>
    /// Marker partial so the host assembly exposes a public type the test project can name when it asserts
    /// the composition (the top-level statements above compile into <c>Program</c>). No behaviour — the host
    /// is composed by the statements above.
    /// </summary>
    public sealed partial class Program;
}
