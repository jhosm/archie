using Babelstone.Cadence;
using Babelstone.Lifecycle;
using Babelstone.Lifecycle.Host;
using Babelstone.Telemetry;
using Babelstone.Telemetry.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// The lifecycle-command driver worker HOST (ADR-PC-036 §Decision 2, candidate A; ADR-IC-011 runtime — .NET,
// stack-coherent with the engine; ADR-IC-013 in-house estate placement; ADR-PC-019 §P2 extraction-ready
// subtree; bd babelstone-6cpq.7). A long-running BackgroundService host, NOT an HTTP API — so
// Host.CreateApplicationBuilder, the same shape the engine's outbox relay, the orchestrator's consume loop,
// and the notification scheduler run as hosted services.
//
// This is the new SIBLING deployable that owns the clock the engine deliberately lacks (ADR-PC-023): it ticks
// on a cadence, asks each family rule which lifecycle commands are due as-of today, and POSTs each to the
// engine's existing ADR-PC-029 command surface with the canonical, server-derived, number-pinned idempotency
// key (LCD-1). The engine stays CLOCKLESS — the driver reaches it ONLY over the command HTTP surface, never
// the byte store, and never makes the engine read a clock (NO_CLOCK_DRIVEN_ENGINE_SIGNAL holds).
var builder = Host.CreateApplicationBuilder(args);

// OpenTelemetry tracing + logs (ADR-IC-007 Layer 1): turn ON the tracer/logger for this driver host and export
// over OTLP to the Collector (§P1 — never direct-to-backend), mirroring the notification host. Spans open on
// the SHARED Babelstone.Engine ActivitySource (from the SDK-free Babelstone.Telemetry leaf — NOT the engine
// kernel): the base CadenceWorker's per-tick `cadence.pass` and the sink's `lifecycle.dispatch` show up in the
// same trace surface as the engine, orchestrator and notification worker. The resource stamps
// service.name=babelstone-lifecycle + service.namespace=babelstone + deployment.environment (OBS-1);
// ResolveEnvironment fails fast on an unset environment. The runtime no-PII guard (OBS_NO_PII_ATTRS /
// ADR-IC-007 §P4) strips any non-admitted span tag / log field at emit, before the exporter.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(BabelstoneResource.LifecycleServiceName)
        .AddAttributes(
        [
            new KeyValuePair<string, object>(BabelstoneResource.ServiceNamespaceKey, BabelstoneResource.ServiceNamespace),
            new KeyValuePair<string, object>(BabelstoneResource.DeploymentEnvironmentKey, BabelstoneResource.ResolveEnvironment()),
        ]))
    .WithTracing(tracing => tracing
        .AddSource(BabelstoneTelemetry.ActivitySourceName)
        .AddBabelstonePiiGuard()
        .AddOtlpExporter())
    // Metrics (bd babelstone-1nkm.4): the MeterProvider the driver's operational instruments need — with
    // none registered every LifecycleDriverMetrics record is a no-op, which is how the un-hardened host
    // ran blind. AddMeter turns on the shared Babelstone.Engine meter (the lifecycle_dispatch_total /
    // lifecycle_dispatch_failure_total counters, the lifecycle_dispatch_lag_seconds histogram, and the
    // lifecycle_pass_last_success_timestamp_seconds tick-liveness heartbeat the lifecycle-driver alert
    // group reads — the always-on money-mover's health surface). The View-based no-PII guard keeps only
    // the admitted operational dimensions (command_kind), the same posture as the orchestrator host.
    .WithMetrics(metrics => metrics
        .AddMeter(BabelstoneTelemetry.MeterName)
        .AddBabelstonePiiGuard()
        .AddOtlpExporter())
    .WithLogging(logging => logging
        .AddBabelstonePiiGuard()
        .AddOtlpExporter());

// The SHARED Babelstone.Engine ActivitySource the LifecycleWorker resolves and hands to its base CadenceWorker
// (which opens `cadence.pass` on it). Registered as a singleton so the worker's constructor injection resolves
// it; it is the same process-wide source AddSource turned on above, so the two agree by construction.
builder.Services.AddSingleton(BabelstoneTelemetry.ActivitySource);

// The engine's ADR-PC-029 command surface this driver POSTs to. A service ENDPOINT, not a credential, so — like
// the notification host's read endpoint and the orchestrator's — it resolves straight from configuration (no
// ISecretProvider). Fail-loud: a driver that cannot resolve its target engine must not start.
var engineBaseUrl =
    builder.Configuration["Engine:BaseUrl"]
    ?? builder.Configuration.GetConnectionString("Engine")
    ?? Environment.GetEnvironmentVariable("BABELSTONE_ENGINE_BASE_URL")
    ?? throw new InvalidOperationException(
        "No engine API base URL configured. Set Engine:BaseUrl, ConnectionStrings:Engine, or " +
        "BABELSTONE_ENGINE_BASE_URL (the ADR-PC-029 command surface — POST /v1/loans/{id}/installment, " +
        "POST /v1/deposits/{id}/maturity).");

// The engine read-model database the family rules range-scan to find due work (ADR-PC-036 §Decision 2/5;
// ADR-IC-005 read-model tier). The forward calendars — term_deposit's maturity range scan and personal_loan's
// installment_calendar — are the temporal signal (ADR-PC-023): the rule reads them as-of today, the engine
// stays clockless. This is the read-side connection (the family read-model stores' SELECTs); it is DISTINCT
// from Engine:BaseUrl above (the WRITE-side command HTTP surface the sink POSTs). Fail-loud: a driver that
// cannot reach the read model cannot discover due commands, so it must not start.
var readModelConnectionString =
    builder.Configuration["Engine:ReadModelConnectionString"]
    ?? builder.Configuration.GetConnectionString("EngineReadModel")
    ?? Environment.GetEnvironmentVariable("BABELSTONE_ENGINE_READMODEL_CONNECTION")
    ?? throw new InvalidOperationException(
        "No engine read-model connection string configured. Set Engine:ReadModelConnectionString, " +
        "ConnectionStrings:EngineReadModel, or BABELSTONE_ENGINE_READMODEL_CONNECTION (the ADR-IC-005 " +
        "read-model tier the family rules range-scan: read_model.deposits, read_model.installment_calendar).");

// The durable dispatch-ledger database (ADR-PC-038 §Decision 1): where the driver's OWN
// lifecycle_dispatch_ledger table lives — the crash-surviving "already fired this occurrence" record whose
// atomic per-occurrence claim is ALSO the multi-replica single-firing guard (§Decision 2). This is the
// RUNTIME-role connection (babelstone_lifecycle — claim/record only, no DDL; ADR-PC-001 §P3). It is
// DISTINCT from the engine read-model connection above (a different concern: the calendar the rules READ
// vs the ledger the driver WRITES), though a deployment may point both at the same cluster. Fail-loud: a
// driver whose dispatches would not survive a restart must not start (the in-memory ledger is a test
// double, never a production fallback).
var ledgerConnectionString =
    builder.Configuration["Lifecycle:LedgerConnectionString"]
    ?? builder.Configuration.GetConnectionString("LifecycleLedger")
    ?? Environment.GetEnvironmentVariable("BABELSTONE_LIFECYCLE_LEDGER_CONNECTION")
    ?? throw new InvalidOperationException(
        "No lifecycle dispatch-ledger connection string configured. Set Lifecycle:LedgerConnectionString, " +
        "ConnectionStrings:LifecycleLedger, or BABELSTONE_LIFECYCLE_LEDGER_CONNECTION (the durable " +
        "lifecycle_dispatch_ledger substrate, ADR-PC-038 §Decision 1).");

// The MIGRATION-role connection the ledger's forward-only series applies over (DDL privileges,
// ADR-PC-001 §P3) — distinct from the runtime role in production, wired by deployment; a dev/test stack
// MAY fall back to the runtime connection (the same single-credential convenience the demo stack uses for
// the engine store).
var ledgerMigrationConnectionString =
    builder.Configuration["Lifecycle:LedgerMigrationConnectionString"]
    ?? builder.Configuration.GetConnectionString("LifecycleLedgerMigration")
    ?? Environment.GetEnvironmentVariable("BABELSTONE_LIFECYCLE_LEDGER_MIGRATION_CONNECTION")
    ?? ledgerConnectionString;

// The wall-clock the worker loop OWNS (ADR-PC-023 §6 — the engine emits no clock-driven signal, so the
// downstream driver owns the clock). TimeProvider.System in production; a test substitutes a fake so the loop
// can be driven with no real wall-clock wait.
builder.Services.AddSingleton(TimeProvider.System);

// The driver's cadence/retry/backoff knobs (ADR-PC-023 §6). Bound from the "Lifecycle" configuration section so
// an operator can tune the poll interval; the generous one-hour default sits well inside a maturity/installment
// due date's latency tolerance (a one-shot maturity may fire up to a poll-interval late — acceptable,
// ADR-PC-036 §Residual risks).
var schedulerOptions = new CadenceSchedulerOptions();
var pollSeconds = builder.Configuration.GetValue<double?>("Lifecycle:PollIntervalSeconds");
if (pollSeconds is > 0)
{
    schedulerOptions = new CadenceSchedulerOptions { PollInterval = TimeSpan.FromSeconds(pollSeconds.Value) };
}

builder.Services.AddSingleton(schedulerOptions);

// The DURABLE dispatch ledger (ADR-PC-036 §Decision 2, hardened per ADR-PC-038): the crash-surviving
// Postgres lifecycle_dispatch_ledger whose per-occurrence atomic claim (FOR UPDATE SKIP LOCKED + the
// per-instance advisory lock) is the multi-replica single-firing guard, keyed on the canonical
// number-pinned dispatch id (LCD-1). A restart does not re-POST (LIFECYCLE_DISPATCH_LEDGER_DURABLE) and
// N replicas fire each occurrence once (LIFECYCLE_DRIVER_SINGLE_FIRING); the engine's command_dedup stays
// the authoritative idempotency backstop regardless. (InMemoryLifecycleDispatchLedger is the Docker-free
// test double — never wired here.)
builder.Services.AddSingleton<ILifecycleDispatchLedger>(
    new PostgresLifecycleDispatchLedger(ledgerConnectionString));

// The ledger schema migration runs FIRST (registered before the worker: hosted services start in
// registration order), applying the driver host's OWN forward-only series (ADR-PC-038 §Decision 1) so
// lifecycle_dispatch_ledger exists before the first tick can claim on it — the same boot shape as the
// orchestrator's SagaMigrationHostedService. Idempotent: a boot with nothing pending is a no-op (the
// MigrationRunner ledger + its advisory lock guard it, including against a concurrently booting replica).
builder.Services.AddHostedService(_ => new LifecycleLedgerMigrationHostedService(ledgerMigrationConnectionString));

// The command-POST SINK (ADR-PC-036 §Decision 2): a typed HttpClient whose BaseAddress is the engine's ADR-PC-029
// command surface, normalised to a trailing "/" so a "/v1/..." command path resolves. This is the ONLY runtime
// path the driver takes to the engine. The sink presents the canonical server-derived idempotency key (LCD-1) and
// the scoped non-interactive SCA principal on money-mover routes (ADR-PC-036 §Decision 1).
builder.Services.AddHttpClient<ILifecycleCommandSink, HttpLifecycleCommandSink>(client =>
    client.BaseAddress = new Uri(engineBaseUrl.EndsWith('/') ? engineBaseUrl : engineBaseUrl + "/"));

// Compose the family ILifecycleCommandRule contributions by ASSEMBLY-SCAN discovery (ADR-PC-036; ADR-PC-021 —
// the lifecycle-driver twin of the engine's HostModuleLoader). The host is the composition root — the ONLY
// place that MAY name a family (ADR-IC-019) — but it names NONE: LifecycleModuleLoader scans the
// Babelstone.Families.*.Lifecycle assemblies shipped beside the host for IFamilyLifecycleModule contributions
// and fails loud on a duplicate FamilyName. Each module registers its OWN family-owned Npgsql read-model store
// (behind the family-agnostic store interface the rule depends on) + its rule, over the read-model connection
// conveyed here — so the read side stays storage-agnostic and the driver core names no family (the family →
// core arrow, ADR-IC-019). The rules range-scan their forward calendars as-of today and emit one decision per
// due occurrence; the generic pass derives the number-pinned id, dedupes, and POSTs. Adding a clock-driven
// lifecycle to a NEW family is its .Lifecycle module + the host ProjectReference (so its dll lands beside the
// host for the scan) — ZERO edit here (ADR-PC-036, "a fourth rule with zero core diff").
var moduleContext = new LifecycleModuleContext(builder.Configuration, readModelConnectionString);
foreach (var module in new LifecycleModuleLoader().LoadAll(LifecycleModuleLoader.FamilyLifecycleAssemblies()))
{
    module.ConfigureServices(builder.Services, moduleContext);
}

// The per-tick engine over the registered family rules + the dispatch ledger + the sink (ADR-PC-036 §Decision 2).
builder.Services.AddSingleton<LifecycleSchedulePass>();

// The host shell — the standing BackgroundService the schedule pass runs inside. It OWNS the clock, cadence,
// retry and backoff (ADR-PC-023 §6); it lives here, in a downstream sibling host, never inside the engine
// (a timer there trips BENG004).
builder.Services.AddHostedService<LifecycleWorker>();

var app = builder.Build();
await app.RunAsync();

namespace Babelstone.Lifecycle.Host
{
    /// <summary>
    /// Marker partial so the host assembly exposes a public type a test project can name when it asserts the
    /// composition (the top-level statements above compile into <c>Program</c>). No behaviour — the host is
    /// composed by the statements above.
    /// </summary>
    public sealed partial class Program;

    /// <summary>
    /// Applies the lifecycle driver's OWN dispatch-ledger schema on startup (ADR-PC-038 §Decision 1 — the
    /// driver host owns its forward-only migration series, exactly as the orchestrator owns its saga
    /// schema). A hosted service so the host's lifetime owns it, registered BEFORE the worker so the
    /// <c>lifecycle_dispatch_ledger</c> table exists before the first tick claims on it; idempotent — a
    /// boot with nothing pending is a no-op, and the runner's session advisory lock serialises a
    /// concurrently booting replica instead of racing it.
    /// </summary>
    internal sealed class LifecycleLedgerMigrationHostedService(string migrationConnectionString) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken) =>
            await new Babelstone.Lifecycle.Migrations.MigrationRunner(migrationConnectionString)
                .ApplyAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
