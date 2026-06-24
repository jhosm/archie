using Babelstone.Notification;
using Babelstone.Telemetry;
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
// ADR-PC-027 read-contract access only — NO scheduler timing, NO event emission (those are the
// downstream children bd babelstone-60n8.2 / .3).
//
// FAMILY-AGNOSTIC by construction (ADR-IC-019 §D2/§D3): the host references neither the engine kernel
// nor any product family — it reads deposit facts over the storage-opaque ADR-PC-027 HTTP contract,
// gated by NOTIFICATION_FAMILY_AGNOSTIC. The earlier skeleton bound the engine kernel +
// the term-deposit family directly; bd babelstone-60n8.5 relocated that read onto the contract.
var builder = Host.CreateApplicationBuilder(args);

// OpenTelemetry tracing (ADR-IC-007 Layer 1): turn ON the tracer for this host and export over OTLP
// to the Collector (§P1 — never direct-to-backend). The host opens spans on the SHARED
// Babelstone.Engine ActivitySource (BabelstoneTelemetry.ActivitySourceName, from the SDK-free
// Babelstone.Telemetry leaf — NOT the engine kernel) — one instrumentation scope across the estate,
// never a competing source — so the notification service's work shows up in the same trace surface as
// the engine and orchestrator. The resource stamps service.name=babelstone-notification +
// service.namespace=babelstone + deployment.environment so every span is attributable (OBS-1);
// ResolveEnvironment fails fast on an unset environment.
//
// NB: NO AspNetCore instrumentation (this host has no inbound HTTP surface). The OUTBOUND read calls
// become CLIENT spans once the scheduler/emission children (bd babelstone-60n8.2 / .3) issue them on
// a clock and the HttpClient instrumentation lands with them; this skeleton issues no calls (the
// worker idles), so tracing here is resource + exporter wiring only — no MeterProvider yet either.
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
        .AddOtlpExporter());

// The engine API ENDPOINT the notification service READS deposit facts from (ADR-PC-027 canonical
// read resource; ADR-IC-019 §D3). This is a service ENDPOINT, not a credential, so — like the
// orchestrator's Engine:BaseUrl — it resolves straight from configuration (no ISecretProvider). The
// read is over the storage-opaque HTTP contract, so the notification core never touches the engine's
// read-model store, its byte-oriented projection boundary, or any family type (ADR-IC-019 §D2/§P2).
var engineBaseUrl =
    builder.Configuration["Engine:BaseUrl"]
    ?? builder.Configuration.GetConnectionString("Engine")
    ?? Environment.GetEnvironmentVariable("BABELSTONE_ENGINE_BASE_URL")
    ?? throw new InvalidOperationException(
        "No engine API base URL configured. Set Engine:BaseUrl, ConnectionStrings:Engine, or " +
        "BABELSTONE_ENGINE_BASE_URL (the ADR-PC-027 deposit read surface — GET /v1/deposits/{id}).");

// The typed read client over the deposit resource. IHttpClientFactory owns connection pooling and
// lifetime (the same pattern the orchestrator's dispatcher uses for its engine POSTs). BaseAddress is
// normalised to a trailing "/" so the client's relative "v1/deposits/{id}" resolves correctly.
builder.Services.AddHttpClient<DepositReadClient>(client =>
    client.BaseAddress = new Uri(engineBaseUrl.EndsWith('/') ? engineBaseUrl : engineBaseUrl + "/"));

// The wall-clock the worker loop OWNS (ADR-PC-023 §6 — the engine emits no clock-driven signal, so
// the downstream scheduler owns the clock). TimeProvider.System in production; a test substitutes a
// FakeTimeProvider so the loop can be driven with no real wall-clock wait. Only the worker LOOP reads
// it — the MaturityScheduler pass stays a deterministic function of the as-of date.
builder.Services.AddSingleton(TimeProvider.System);

// The maturity scheduler's cadence/retry/backoff knobs (ADR-PC-023 §6). Bound from the "Notification"
// configuration section so an operator can tune the poll interval; the generous one-hour default sits
// well inside the 14-day opt-out window's latency tolerance.
var schedulerOptions = new NotificationSchedulerOptions();
var pollSeconds = builder.Configuration.GetValue<double?>("Notification:PollIntervalSeconds");
if (pollSeconds is > 0)
{
    schedulerOptions = new NotificationSchedulerOptions { PollInterval = TimeSpan.FromSeconds(pollSeconds.Value) };
}

builder.Services.AddSingleton(schedulerOptions);

// The slot-4 idempotency ledger (ADR-PC-025): the "already raised this notification_id" memory the
// scheduler dedupes against. In-memory for v1 (it proves the "re-runs don't re-notify" invariant); a
// durable, crash-surviving ledger is the emission child's concern (bd babelstone-60n8.3).
builder.Services.AddSingleton<INotificationDedupeLedger, InMemoryNotificationDedupeLedger>();

// The downstream maturity scheduler (ADR-PC-023 §6 / ADR-PC-025): reads the maturity calendar over the
// ADR-PC-027 contract, selects the 14-day pre-maturity opt-out window (02 §2.4.4), and produces
// idempotent "reminder due" decisions keyed on the composite notification_id (ADR-PC-025 slot 4).
builder.Services.AddSingleton<MaturityScheduler>();

// The host shell — the standing BackgroundService the maturity scheduler (bd babelstone-60n8.2) runs
// inside. It OWNS the clock, cadence, retry and backoff (ADR-PC-023 §6); the NotificationDue emission
// over the outbox is the sibling child bd babelstone-60n8.3.
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
