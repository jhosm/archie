using Babelstone.Cadence;
using Babelstone.Notification;
using Babelstone.Notification.Delivery;
using Babelstone.Notification.Host;
using Babelstone.Packs;
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
// This is the §D4/§A2 COMPOSITION ROOT (ADR-IC-019 §D4 + Amendment 2026-06-24): the ONE place that MAY name a
// family — but it names NONE. It DISCOVERS the IFamilyNotificationModule contributions by assembly-scan
// (NotificationModuleLoader, the realized "assembly-scan-later" of ADR-PC-021 §A3), composes each into DI, and
// runs the family-agnostic core loop. The core (Babelstone.Notification) references neither the engine kernel
// nor any family; the host wires them together — gated by NOTIFICATION_FAMILY_AGNOSTIC over the CORE, not this
// host.
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

// NOTE: the typed deposit read client is NOT registered here. A "deposit" (it matures, pays coupons, has tax
// withheld) is term-deposit knowledge, so its read client is FAMILY-OWNED and registered by the family's
// own module (TermDepositNotificationModule.ConfigureServices), over the engine read endpoint conveyed on the
// module context — keeping deposit-shaped types out of both this host and the family-agnostic core
// (ADR-IC-019 §D1).

// The wall-clock the worker loop OWNS (ADR-PC-023 §6 — the engine emits no clock-driven signal, so the
// downstream scheduler owns the clock). TimeProvider.System in production; a test substitutes a
// FakeTimeProvider so the loop can be driven with no real wall-clock wait.
builder.Services.AddSingleton(TimeProvider.System);

// The schedule pass's cadence/retry/backoff knobs (ADR-PC-023 §6). Bound from the "Notification"
// configuration section so an operator can tune the poll interval; the generous one-hour default sits well
// inside a reminder's latency tolerance.
var schedulerOptions = new CadenceSchedulerOptions();
var pollSeconds = builder.Configuration.GetValue<double?>("Notification:PollIntervalSeconds");
if (pollSeconds is > 0)
{
    schedulerOptions = new CadenceSchedulerOptions { PollInterval = TimeSpan.FromSeconds(pollSeconds.Value) };
}

builder.Services.AddSingleton(schedulerOptions);

// The slot-4 idempotency ledger (ADR-PC-025): the "already raised this notification_id" memory the schedule
// pass dedupes against. In-memory for v1; a durable, crash-surviving ledger is the emission child's concern
// (bd babelstone-60n8.3).
builder.Services.AddSingleton<IDedupeLedger, InMemoryDedupeLedger>();

// Resolve the instance-pinned regulatory pack (ADR-PC-007 §P4 / ADR-PC-025 §2) at startup, off disk via the
// structural parser — the walking-skeleton stand-in for the OCI loader, the same disk path the engine host
// uses. The host is the §A2 composition root and MAY reference Babelstone.Packs (the gate protects only the
// family-agnostic core); it conveys the pack's declared template_refs + numeric parameters into the module
// context as PLAIN, GENERIC data, so a family module sources its template-set/window from the pinned pack
// without the core ever holding a pack type (ADR-IC-019 §P2, bd babelstone-60n8.6). Fail-loud: a host that
// cannot resolve its pinned pack must not serve under an unknown disclosure surface.
var pinnedPack = LoadPinnedPack(builder.Configuration);

// Compose the family notification contributions by ASSEMBLY-SCAN discovery (ADR-IC-019 §D4 + Amendment
// 2026-06-24; ADR-PC-021 §A3 — the notification-side twin of the engine's HostModuleLoader). The host is the
// §A2 composition root — the only place that MAY name a family — but it names NONE: NotificationModuleLoader
// scans the Babelstone.Families.*.Notification assemblies shipped beside the host for IFamilyNotificationModule
// contributions and fails loud on a duplicate FamilyName (the host-edge guard the engine's HostModuleLoader
// also enforces). A second family ships a new module + the host ProjectReference (so its dll lands beside the
// host for the scan) with ZERO edit here.
var moduleContext = new NotificationModuleContext(
    builder.Configuration,
    engineBaseUrl,
    PackTemplateRefs: pinnedPack.Manifest.TemplateRefNames,
    PackParameters: new Dictionary<string, int>(StringComparer.Ordinal)
    {
        // The pinned pack's numeric parameters, reflected GENERICALLY under their canonical pack names
        // (ADR-PC-007). A family module selects the ones it needs by name — e.g. the term-deposit
        // auto-renewal opt-out window — so the core conveys the values without ever naming a family-specific
        // parameter (ADR-IC-019 §D1/§P2). The host MAY name them (the §A2 composition-root exemption).
        ["auto_renewal_optout_window_days"] = pinnedPack.Parameters.AutoRenewalOptoutWindowDays,
        ["max_consumer_rate_bps"] = pinnedPack.Parameters.MaxConsumerRateBps,
    });
foreach (var module in new NotificationModuleLoader().LoadAll(NotificationModuleLoader.FamilyNotificationAssemblies()))
{
    module.ConfigureServices(builder.Services, moduleContext);
}

// The core's generic per-tick engine over the registered family rules + the dedupe ledger (ADR-IC-019 §D2).
builder.Services.AddSingleton<NotificationSchedulePass>();

// The host shell — the standing BackgroundService the schedule pass runs inside. It OWNS the clock, cadence,
// retry and backoff (ADR-PC-023 §6); the NotificationDue emission over the outbox is bd babelstone-60n8.3.
builder.Services.AddHostedService<NotificationWorker>();

// Webhook delivery (SCHEDULED + EVENT_DRIVEN legs) — a config-gated no-op until
// Notification:Webhook:EndpointUrl is set, so hosts without a configured receiver
// run exactly as before.
builder.Services.AddNotificationWebhookDelivery(builder.Configuration);

var app = builder.Build();
await app.RunAsync();

// Loads the instance-pinned regulatory pack off disk via the structural parser (ADR-PC-007 §P4). It globs
// every *.yaml under the pack directory into the in-tar-relative-path-keyed map PackParser expects (so it
// picks up primitives, parameters, families, rate-sheet-refs AND templates without a curated file list); the
// parser reads only the files it knows and ignores extras. Configure with Engine:PackVersion (default
// pt.2026.1) and Engine:PacksDir (else walk up to find packs/). Fail-loud on a missing dir or an
// unverifiable/unparsable pack — the same posture as the engine host's disk loader.
static VerifiedPack LoadPinnedPack(IConfiguration configuration)
{
    var version = configuration.GetValue("Engine:PackVersion", "pt.2026.1");
    var packsDir = configuration["Engine:PacksDir"] ?? FindPacksDir();
    var packDir = Path.Combine(packsDir, version);
    if (!Directory.Exists(packDir))
    {
        throw new InvalidOperationException(
            $"pinned pack '{version}' not found under '{packsDir}' (set Engine:PacksDir / Engine:PackVersion). "
            + "The notification host resolves its instance-pinned pack at startup (ADR-PC-007 §P4).");
    }

    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (var path in Directory.EnumerateFiles(packDir, "*.yaml", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(packDir, path).Replace(Path.DirectorySeparatorChar, '/');
        files[relative] = File.ReadAllBytes(path);
    }

    return PackParser.Parse(files, version);
}

// Walk up from the host's base directory to the repo/deploy root that contains packs/ (worktree-safe, no
// .git dependency) — the same disk-marker walk the engine host's HostPack uses.
static string FindPacksDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "packs")))
    {
        dir = dir.Parent;
    }

    return dir is not null
        ? Path.Combine(dir.FullName, "packs")
        : throw new InvalidOperationException(
            $"packs/ directory not found from {AppContext.BaseDirectory}; set Engine:PacksDir.");
}

namespace Babelstone.Notification.Host
{
    /// <summary>
    /// Marker partial so the host assembly exposes a public type the test project can name when it asserts
    /// the composition (the top-level statements above compile into <c>Program</c>). No behaviour — the host
    /// is composed by the statements above.
    /// </summary>
    public sealed partial class Program;
}
