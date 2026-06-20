using Babelstone.Families.TermDeposit.Orchestration;
using Babelstone.Orchestrator;
using Babelstone.Orchestrator.Dispatch;
using Babelstone.Orchestrator.Edge;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Migrations;
using Babelstone.Orchestrator.Saga;
using Babelstone.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// The in-house saga orchestrator host (ADR-IC-003 "Event-driven application orchestrator").
// It is BOTH a Redpanda consumer (ADR-IC-003 §S2 "a Redpanda consumer like every other service")
// AND — since I.1 — the application behind the EDGE that fronts the constitution saga (ADR-IC-006
// §P4 / Document 05 §Step 0). So it is now a WebApplication, NOT Host.CreateApplicationBuilder:
// the Kestrel HTTP surface (the 202 + SSE front door) runs ALONGSIDE the existing hosted services
// — the migration service, the Redpanda consume loop (#167) that READS events off the bus and
// advances the saga, and the saga command dispatcher (#170) that drains saga_outbox to the engine
// over HTTP. Adding Kestrel is a FRAMEWORK reference, NOT an engine-kernel ProjectReference, so the
// orchestrator subtree stays extraction-ready (ADR-PC-019 §P2).
var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry tracing (ADR-IC-007 Layer 1, H.5): turn ON the tracer for this host. The saga
// substrate already opens saga-advance spans on the shared Babelstone.Engine ActivitySource and
// threads the W3C traceparent through the saga outbox + Kafka headers — but with NO TracerProvider
// registered, every StartActivity returns null and the whole path is a no-op (which is why
// LIVE·saga showed no real spans). Registering the provider + OTLP exporter here makes those spans
// real and exports them to the Collector (§P1 — never direct-to-backend), so a saga becomes ONE
// connected trace: the edge SERVER span → the saga-advance spans → (via the dispatcher's manually
// propagated traceparent) the engine's deposit.* + Npgsql CLIENT spans. The resource stamps
// service.name=babelstone-orchestrator + service.namespace=babelstone + deployment.environment so
// every span is attributable (OBS-1); ResolveEnvironment fails fast on an unset environment.
//
// NB: AspNetCore instrumentation only (the edge SERVER span + inbound-traceparent join) + the shared
// Babelstone.Engine source. Deliberately NO HttpClient instrumentation: the dispatcher injects the
// traceparent MANUALLY off the durable outbox row (SagaCommandDispatchDrainer), so auto-injection
// would emit a competing header. Tracing only — the orchestrator adds no MeterProvider here.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(BabelstoneResource.OrchestratorServiceName)
        .AddAttributes(
        [
            new KeyValuePair<string, object>(BabelstoneResource.ServiceNamespaceKey, BabelstoneResource.ServiceNamespace),
            new KeyValuePair<string, object>(BabelstoneResource.DeploymentEnvironmentKey, BabelstoneResource.ResolveEnvironment()),
        ]))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource(BabelstoneTelemetry.ActivitySourceName)
        .AddOtlpExporter());

// The application-database connection string resolves from configuration at the composition
// root (the same boundary the engine hosts use, ADR-PC-004 Amendment A1). The migration role
// connection (DDL privileges, ADR-PC-001 §P3) is distinct from the runtime role connection
// (babelstone_orchestrator) the worker uses — wired by deployment. The resolved credential
// NEVER rides a saga message (ADR-IC-003 §P7) nor the durable bus (ADR-PC-004 §P2).
var migrationConnectionString =
    builder.Configuration.GetConnectionString("OrchestratorMigration")
    ?? builder.Configuration["Orchestrator:MigrationConnectionString"]
    ?? Environment.GetEnvironmentVariable("ORCHESTRATOR_MIGRATION_CONNECTION_STRING");

// The RUNTIME-role connection the consume loop persists the saga through — the
// babelstone_orchestrator role, distinct from the DDL migration role above (ADR-PC-001 §P3) and
// resolved through the same credential boundary (ADR-PC-004 Amendment A1). It never rides a
// message (ADR-IC-003 §P7) nor the bus (ADR-PC-004 §P2).
var runtimeConnectionString =
    builder.Configuration.GetConnectionString("Orchestrator")
    ?? builder.Configuration["Orchestrator:ConnectionString"]
    ?? Environment.GetEnvironmentVariable("ORCHESTRATOR_CONNECTION_STRING");

// The HTTP endpoints the saga command dispatcher delivers to (ADR-PC-029). The engine command surface
// (Engine:BaseUrl) and the Core-ACL/settlement target (Settlement:BaseUrl) are service ENDPOINTS, not
// credentials, so they resolve straight from IConfiguration — distinct from the runtime DB credential
// (the ADR-PC-004 Amendment A1 boundary). Resolved up front so the family saga modules can pin them
// onto their command routers via the SagaModuleContext below.
var engineBaseUrl = builder.Configuration["Engine:BaseUrl"]
    ?? builder.Configuration.GetConnectionString("Engine")
    ?? "http://localhost:8080";
var settlementBaseUrl = builder.Configuration["Settlement:BaseUrl"] ?? "http://localhost:8089";

// The orchestrator hosts the FAMILY-AGNOSTIC substrate; each concrete saga is a FAMILY-OWNED MODULE
// (ADR-IC-018 §D1/§D4/§P4). The host — the §D4 composition root, the standing exemption that MAY name a
// family (ADR-PC-021 §A2 pattern) — holds an EXPLICIT module list at the current saga count (ADR-PC-021
// §A3: explicit now, assembly-scan later) and loops over it: it lets each module register its
// family-owned services (ConfigureServices) and registers the machine/bridge/router each contributes.
// Adding a family's saga is a new module here, ZERO substrate diff (ADR-IC-018 §Consequences). H.3
// renewal (babelstone-mtto PR2) is the next module — an EventAutoStarted one.
var sagaModuleContext = new SagaModuleContext(
    RuntimeConnectionString: runtimeConnectionString
        ?? throw new InvalidOperationException(
            "No orchestrator runtime connection string configured. Set ConnectionStrings:Orchestrator, " +
            "Orchestrator:ConnectionString, or ORCHESTRATOR_CONNECTION_STRING."),
    EngineBaseUrl: engineBaseUrl,
    SettlementBaseUrl: settlementBaseUrl);

var sagaModules = new ISagaModule[]
{
    new TermDepositSagaModule(sagaModuleContext),
    // H.3 renewal (bd babelstone-mtto PR2): the SECOND saga on this substrate — an EventAutoStarted one.
    // It starts on the engine's DepositMatured fact (a non-NONE ce_autorenewalpolicy header) and runs in
    // its OWN consumer group over the SAME term_deposit topic. It carries NO product/role/funding config
    // (ADR-IC-003 §A7): the engine resolves every renewal fact from the Matured closing deposit it loads
    // (ADR-PC-009; bd babelstone-mtto.5), so the command body is the minimal { new_deposit_id }.
    new RenewalSagaModule(sagaModuleContext),
};

foreach (var module in sagaModules)
{
    // The module registers its family-owned services (per-saga business-reference store, the outbox
    // command sink that calls the family's payload factory, …). The substrate keeps only the ports.
    module.ConfigureServices(builder.Services, sagaModuleContext);
    // The machine, the result-event bridge, and the command router the module contributes — collected
    // by the substrate handler/drainer/composite as the per-saga-type registries (GetServices).
    builder.Services.AddSingleton(module.StateMachine);
    builder.Services.AddSingleton(module.ResultEventBridge);
    builder.Services.AddSingleton(module.CommandRouter);
}

// The multi-saga command sink (bd babelstone-mtto PR2): each family module registered its OWN
// ISagaTypedCommandSink (it assembles its saga's command bodies — the constitution business-reference
// payloads, the renewal wire bodies). The CompositeSagaCommandSink is the ISagaCommandSink the advance
// handler consumes — it routes each emission to the typed sink for the advancing saga's saga_type,
// naming no family. The edge starter resolves the constitution typed sink directly (it starts only that
// saga); the advance handler uses the composite.
builder.Services.AddSingleton<ISagaCommandSink>(sp =>
    new CompositeSagaCommandSink(sp.GetServices<ISagaTypedCommandSink>()));

// A saga_type → machine registry singleton for the edge SSE read (ADR-IC-018 §D3 / §7): the SSE loop
// resolves the saga's machine by saga_type and asks IT whether a polled state is terminal — never a
// central static predicate. The advance handler builds the same routing internally from GetServices.
builder.Services.AddSingleton<IReadOnlyDictionary<string, ISagaStateMachine>>(sp =>
    sp.GetServices<ISagaStateMachine>().ToDictionary(m => m.SagaType, StringComparer.Ordinal));

// A saga_type → agent-status-map registry singleton for the edge process-status read (bd
// babelstone-vjoi / Document 11 Pattern 2): the get_process_status endpoint resolves the saga's COARSE
// AgentStatus projection by saga_type — the SAME family-owned-vocabulary, resolve-by-saga_type move the
// machine registry above uses for terminality (ADR-IC-018 §D3). Each family module registered its
// ISagaAgentStatusMap in ConfigureServices; this collects them by saga_type. Edge-only — the advance
// handler never reads it.
builder.Services.AddSingleton<IReadOnlyDictionary<string, ISagaAgentStatusMap>>(sp =>
    sp.GetServices<ISagaAgentStatusMap>().ToDictionary(m => m.SagaType, StringComparer.Ordinal));

builder.Services.AddSingleton<SagaStateStore>();
builder.Services.AddSingleton<SagaTransitionLog>();
// The saga_outbox store's write side (ADR-IC-018 §D2 — a SUBSTRATE component, alongside the saga_state
// and saga_transition stores). Each family's typed command sink composes it: the sink assembles its own
// payload bytes, the writer owns the row write + the operational message_id mint, identical for every
// saga type (no per-family INSERT duplication).
builder.Services.AddSingleton<SagaOutboxWriter>();

// The consume loop ADVANCES sagas only — it never starts them. Sagas are started exclusively at the
// edge (EdgeSagaStarter), which pins the business references in the same transaction as the STARTED
// row; the loop resumes a saga on a consumed advance event (ADR-IC-003 §S2; bd babelstone-t7o3.9).
// GetServices (not GetRequiredService) collects EVERY ISagaStateMachine registration so the handler
// hosts N saga types keyed by saga_type (ADR-IC-018 §D2). The substrate handler carries no family
// dependency — the per-saga reissue-budget / approval-fork logic is the family machine's optional
// IEventSubstitutor / IPostAdvanceHook hooks (ADR-IC-018 §P6).
// The sagaModules list is threaded in so the handler builds the EVENT-AUTO-START registry (ADR-IC-018
// §P5): on a LoadAsync miss it checks whether an EventAutoStarted module (the renewal saga) declared
// THIS event type as its start trigger with its header predicate satisfied, and if so starts the saga
// and advances it with the start event in ONE transaction. The substrate reads only the modules'
// DECLARED rules + the record's CloudEvents headers — it names no family.
builder.Services.AddSingleton(sp => new SagaAdvanceHandler(
    sp.GetServices<ISagaStateMachine>(),
    sp.GetRequiredService<SagaStateStore>(),
    sp.GetRequiredService<SagaTransitionLog>(),
    sp.GetRequiredService<ISagaCommandSink>(),
    sagaModules));

// The schema migration runs FIRST (registered before the consume loop): hosted services start in
// registration order, so the saga schema is applied before the consumer can write its first dedup
// row. Idempotent — a boot with nothing pending is a no-op (the MigrationRunner ledger guards it).
builder.Services.AddHostedService(sp => new SagaMigrationHostedService(migrationConnectionString));

// The Redpanda consume loop (t7o3.2) — the same hosted-BackgroundService shape the engine's
// outbox relay and inbox consumer use. Three pieces register here:
//   • SagaInboxConsumerOptions — the broker endpoint plus the consumer group id and the topics the
//     saga reacts to, both DECLARED BY THE FAMILY SAGA MODULE (ISagaModule.ConsumerGroupId /
//     ConsumeTopics; ADR-IC-018 §P4) — the host names no family topic. For the constitution module that
//     is its own group over the internal deposits.process.events domain topic AND the engine's
//     term_deposit FAMILY INTEGRATION topic where the closing DepositConstituted fact arrives
//     (ADR-IC-003 §S2 A6 2026-06-15, bd babelstone-t7o3.11). The Kafka bootstrap address is a broker
//     ENDPOINT, not a credential — already plaintext in infra/compose.yaml — so it resolves straight
//     from IConfiguration (Kafka:BootstrapServers), distinct from the runtime DB credential.
//   • SagaConsumeLoop — the impure shell that owns the consumer, the connection/transaction, and the
//     offset commit (commit AFTER the DB tx → at-least-once delivery, effectively-once advance).
//   • AddHostedService<SagaInboxConsumerService> — the poll loop that drives the loop with
//     exponential backoff on a transient failure (the offset stays uncommitted → redelivery).
// ONE consume loop PER MODULE, each on its OWN consumer group (ADR-IC-018 §P4 / Risk 3; bd
// babelstone-mtto PR2). The substrate's hosted-service shape registered a SINGLE
// SagaInboxConsumerOptions/SagaConsumeLoop/SagaInboxConsumerService as DI singletons; with a SECOND
// module those singletons would COLLIDE (the first registration wins, so the renewal loop would never
// run). The resolution: build a per-module options + loop and register the hosted service via a FACTORY
// that closes over them — no shared singleton to collide on. Each loop owns its module's consumer group,
// so the two sagas read the shared term_deposit topic independently (no shared-group contention). The
// SagaAdvanceHandler is shared (it hosts all N machines + the auto-start registry); only the
// options/loop/hosted-service are per-module.
var bootstrapServers = builder.Configuration["Kafka:BootstrapServers"] ?? "localhost:19092";
foreach (var module in sagaModules)
{
    var moduleOptions = new SagaInboxConsumerOptions
    {
        // The runtime connection string must be present to consume: refusing here (rather than at the
        // first consume) fails fast against a mis-wired deployment, the same stance the migration
        // hosted service takes.
        ConnectionString = sagaModuleContext.RuntimeConnectionString,
        BootstrapServers = bootstrapServers,
        // The module names its OWN consumer group; an operator MAY still override it per saga_type via
        // Kafka:GroupId:<SagaType> (the pre-substrate Kafka:GroupId knob, now keyed by saga so each
        // module's group is independently overridable). Defaults to the module's value so existing
        // committed offsets are preserved (a different group would re-read from the beginning).
        GroupId = builder.Configuration[$"Kafka:GroupId:{module.SagaType}"]
            ?? (module.StartMode == SagaStartMode.EdgeStarted
                ? builder.Configuration["Kafka:GroupId"] ?? module.ConsumerGroupId
                : module.ConsumerGroupId),
        Topics = module.ConsumeTopics,
        // An EventAutoStarted module's group is brand-new on first deploy, so it must start from EARLIEST
        // to see existing DepositMatured facts on the retained topic rather than silently skipping the
        // backlog (the default is already Earliest; pinned here for intent).
        StartFromEarliest = true,
    };

    // Capture the per-module loop in the factory closure (NOT a DI singleton) so each hosted service
    // gets its OWN loop+group; the shared advance handler is resolved from DI.
    builder.Services.AddHostedService(sp =>
        new SagaInboxConsumerService(
            new SagaConsumeLoop(moduleOptions, sp.GetRequiredService<SagaAdvanceHandler>())));
}

// The saga command DISPATCHER (bd babelstone-t7o3.3, ADR-PC-029). The consume loop above advances the
// saga on EVENTS; the saga DECIDES commands and writes them to saga_outbox (the SagaCommandOutboxSink
// write side). This dispatcher is the missing DELIVERY half: it drains saga_outbox and POSTs each
// command to its target over idempotent HTTP, so the saga actually DRIVES the engine. Four pieces
// register here, the same hosted-BackgroundService shape as the engine's outbox relay — but
// delivering to HTTP, not Redpanda (commands ride HTTP point-to-point, Primitive 1; the bus stays
// events-only):
//   • SagaCommandDispatcherOptions — the runtime DB connection + the two configurable HTTP targets.
//     The engine command surface (Engine:BaseUrl) and the Core-ACL/settlement target
//     (Settlement:BaseUrl) are service ENDPOINTS, not credentials, so they resolve straight from
//     IConfiguration — distinct from the runtime DB credential (ADR-PC-004 Amendment A1 boundary),
//     which is the SAME runtimeConnectionString the consume loop persists through. The settlement
//     target is a WireMock stub at v1 (the real ACL is DEF-1, bd ub9s); routing it through config
//     means a later deploy repoints it with no code change.
//   • ICommandRouter (SagaCommandRouter) — the pure command-name → (target, route) seam:
//     ActivateDeposit → the engine's POST /v1/deposits (the Pact-pinned route), the settlement legs
//     → the settlement target.
//   • AddHttpClient — IHttpClientFactory owns connection pooling/lifetime for the dispatcher's POSTs.
//   • AddHostedService<SagaCommandDispatcherService> — the poll loop that drains PENDING rows and
//     applies the ADR-PC-029 slot-5 error model (2xx → PUBLISHED, 4xx → terminal FAILED surfaced for
//     compensation, 5xx/timeout → stay PENDING and retry; idempotency makes the retry safe).
builder.Services.AddSingleton(new SagaCommandDispatcherOptions
{
    ConnectionString = sagaModuleContext.RuntimeConnectionString,
    EngineBaseUrl = engineBaseUrl,
    SettlementBaseUrl = settlementBaseUrl,
});
// The routing seam is multi-saga (ADR-IC-018 §D2): each family module contributed its own
// ISagaCommandRouter via the module loop above (it serves its own saga_type). The CompositeCommandRouter
// is the ICommandRouter the dispatcher consumes — it collects every ISagaCommandRouter (GetServices) into
// a saga_type → router registry and delegates by the outbox row's saga_type, naming no family.
builder.Services.AddSingleton<ICommandRouter>(sp =>
    new CompositeCommandRouter(sp.GetServices<ISagaCommandRouter>()));
builder.Services.AddHttpClient();
// The SagaAdvanceHandler (registered above for the consume loop) is ALSO injected into the dispatcher
// for the command-outcome → result-event bridge (bd babelstone-t7o3.8): at a terminal delivery outcome
// the dispatcher maps (command_type, outcome) → a result event and self-advances the saga IN-PROCESS,
// in the SAME connection+transaction as the saga_outbox status flip — nothing rides the durable bus
// (the SAME pattern as the t7o3.1 approval-fork self-emit). That is what makes the saga walk to a
// terminal state and auto-compensate (e.g. ActivateDeposit refused after the debit → ReverseCoreDebit
// → CANCELLED_AFTER_DEBIT) instead of stalling at PARALLEL_VALIDATION.
builder.Services.AddSingleton(sp => new SagaCommandDispatchDrainer(
    sp.GetRequiredService<SagaCommandDispatcherOptions>(),
    sp.GetRequiredService<ICommandRouter>(),
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<SagaAdvanceHandler>(),
    sp.GetServices<IResultEventBridge>()));
builder.Services.AddHostedService<SagaCommandDispatcherService>();

// The I.1 EDGE HTTP surface (ADR-IC-006 §P4 / Document 05 §Step 0): the 202 + process_id + SSE
// front door that STARTS the saga (NOT a direct engine append — PR #149's rejected anti-pattern).
// EdgeServices composes the EdgeSagaStarter (starts the ConstitutionProcess in-process within one
// transaction, emitting the parallel commands to saga_outbox — nothing on the bus) and the
// SagaStateReader the SSE stream observes the saga state through. It shares the SAME saga stores
// the consume loop registered above (TryAdd keeps a single instance), and runs against the SAME
// runtime DB credential. Nothing here is an engine-kernel reference — the subtree stays
// extraction-ready (ADR-PC-019 §P2).
EdgeServices.Register(builder.Services, runtimeConnectionString
    ?? throw new InvalidOperationException(
        "No orchestrator runtime connection string configured. Set ConnectionStrings:Orchestrator, " +
        "Orchestrator:ConnectionString, or ORCHESTRATOR_CONNECTION_STRING."));

var app = builder.Build();

// Hand the active trace id back to the caller on every edge response (the SAME X-Trace-Id contract
// the engine exposes — Babelstone.Engine.Api.TraceResponseHeader, bd babelstone-2dex / ADR-IC-007
// Layer 1). The edge SERVER span (AddAspNetCoreInstrumentation) roots the saga's trace and writes
// the first saga_outbox row with its traceparent, so THIS id is the whole saga's trace id — the key
// Mission Control's Telemetry tab queries Grafana Tempo by to render the full distributed saga
// trace. Read from Activity.Current in Response.OnStarting (captured just before the headers flush,
// when the request activity is still current). The 32-hex W3C trace id is an opaque operational
// identifier, never PII (ADR-IC-007 §P4 / ADR-PC-004 §P2). Placed before endpoint mapping so it
// wraps every edge response, including the 202 the constitution POST returns.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var traceId = System.Diagnostics.Activity.Current?.TraceId.ToString();
        if (traceId is not null && !context.Response.Headers.ContainsKey("X-Trace-Id"))
        {
            context.Response.Headers["X-Trace-Id"] = traceId;
        }

        return Task.CompletedTask;
    });

    await next(context);
});

// Map the edge routes (POST /api/v1/deposits/constitute, GET /api/v1/processes/{id}/stream). The
// hosted services (migration, consume loop, dispatcher) start on their own via the host lifecycle.
ProcessApiEndpoints.Map(app);

await app.RunAsync();

namespace Babelstone.Orchestrator
{
    /// <summary>
    /// Applies the saga schema on startup (the orchestrator owns its own application-database
    /// schema, ADR-IC-003 §S2). A hosted service so the host's lifetime owns it; idempotent —
    /// a boot with nothing pending is a no-op (the <see cref="MigrationRunner"/> ledger guards
    /// it). Refuses to run with no migration connection string rather than booting against an
    /// un-migrated database.
    /// </summary>
    internal sealed class SagaMigrationHostedService(string? migrationConnectionString) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(migrationConnectionString))
            {
                throw new InvalidOperationException(
                    "No orchestrator migration connection string configured. Set " +
                    "ConnectionStrings:OrchestratorMigration, Orchestrator:MigrationConnectionString, " +
                    "or ORCHESTRATOR_MIGRATION_CONNECTION_STRING.");
            }

            await new MigrationRunner(migrationConnectionString).ApplyAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
