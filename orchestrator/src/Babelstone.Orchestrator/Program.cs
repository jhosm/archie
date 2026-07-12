using Babelstone.Cadence;
using Babelstone.Orchestrator;
using Babelstone.Orchestrator.Dispatch;
using Babelstone.Orchestrator.Edge;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Migrations;
using Babelstone.Orchestrator.Saga;
using Babelstone.Orchestrator.Saga.Settlement;
using Babelstone.Telemetry;
using Babelstone.Telemetry.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
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

// Server-side internal mTLS on the saga edge (bd babelstone-zla1.12.25; ADR-IC-006 §P5 Boundary 2 /
// ADR-IC-016 plane (i), commitment SVC_ENGINE_ORCH_MTLS): when configured, the orchestrator's Kestrel
// host — the 202 + SSE front door Kong and Mission Control dial — REQUIRES a client cert and validates
// it by chaining to the pinned internal CA, the SERVER half of the caller-side dispatcher leg (bd
// babelstone-zla1.12.10) wired below. Gated on the SAME InternalMtls:CaCertPath as that outbound leg,
// so both turn on together in one maintenance window (internal-mtls.patch.yaml's ROLLOUT ORDER —
// caller and server flip together) and demo/local/test stay plain HTTP. The HTTPS transport (endpoint
// URL + server cert) stays config-driven; only the require+validate policy is code — the orchestrator
// holds no engine-kernel reference (ADR-PC-019 §P2).
if (InternalMtls.IsConfigured(builder.Configuration))
{
    InternalMtls.ConfigureKestrel(builder.WebHost, builder.Configuration);
}

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
// would emit a competing header.
//
// Metrics + logs are wired here too (njt2.10/2.11): a MeterProvider so the substrate's saga inbox +
// dispatch counters (SagaConsumeLoop / SagaCommandDispatchDrainer, on the shared Babelstone.Engine
// meter) are exported, and a LoggerProvider so structured logs ship over OTLP — both carrying the
// runtime no-PII guard (AddBabelstonePiiGuard) so no personal data rides any telemetry signal
// (OBS_NO_PII_ATTRS / ADR-IC-007 §P4).
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
        // The runtime no-PII guard (njt2.9): strips any non-admitted span tag at OnEnd before export.
        .AddBabelstonePiiGuard()
        .AddOtlpExporter())
    // Metrics (njt2.11): wire the MeterProvider the substrate's saga inbox/dispatch counters need (none
    // existed before — every Counter.Add was a no-op). The View-based no-PII guard keeps only the admitted
    // operational dimensions (source_topic / command_type / …), dropping any PII-shaped label at emit.
    .WithMetrics(metrics => metrics
        .AddMeter(BabelstoneTelemetry.MeterName)
        .AddBabelstonePiiGuard()
        .AddOtlpExporter())
    // Logs (njt2.10): net-new LoggerProvider so structured logs export over OTLP, with the log no-PII
    // guard stripping any un-namespaced PII-fragment field before export.
    .WithLogging(logging => logging
        .AddBabelstonePiiGuard()
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
// The engine-OWNED current-account settlement target (ADR-PC-043). OPTIONAL — when
// unset, no leg is engine-CA-routed and every settlement command stays on Settlement:BaseUrl (the legacy
// ACL), so the pre-ADR-PC-043 behaviour is preserved with no config change. The demo/staging bring-up
// points Settlement:EngineCaBaseUrl at the engine's own command surface so the constitution funding debit
// (and the substrate settlement legs) reach the engine-CA authorize/capture/credit ingress.
var engineCaSettlementBaseUrl = builder.Configuration["Settlement:EngineCaBaseUrl"];

// The orchestrator hosts the FAMILY-AGNOSTIC substrate; each concrete saga is a FAMILY-OWNED MODULE
// (ADR-IC-018 §D1/§D4/§P4). The host — the §D4 composition root, the standing exemption that MAY
// ProjectReference families/** (ADR-PC-021 §A2 pattern; the <BabelstoneRole>CompositionRoot marker,
// ADR-PC-040 §D2) — DISCOVERS the family saga modules by assembly-scan (SagaModuleLoader over the
// shared FamilyModuleScanner, ADR-IC-018 §D6's realized "assembly-scan later" / ADR-PC-040 §D3) and
// loops over them: each module registers its family-owned services (ConfigureServices) and the host
// registers the machine/bridge/router each contributes. It names NO family: adding a family's saga is
// the family's own .Orchestration module + the host ProjectReference (so its dll lands beside the host
// for the scan) — ZERO edit here (ADR-IC-018 §Consequences), gated by COMPOSITION_ROOT_NAMES_NO_FAMILY.
var sagaModuleContext = new SagaModuleContext(
    RuntimeConnectionString: runtimeConnectionString
        ?? throw new InvalidOperationException(
            "No orchestrator runtime connection string configured. Set ConnectionStrings:Orchestrator, " +
            "Orchestrator:ConnectionString, or ORCHESTRATOR_CONNECTION_STRING."),
    EngineBaseUrl: engineBaseUrl,
    SettlementBaseUrl: settlementBaseUrl,
    EngineCaSettlementBaseUrl: engineCaSettlementBaseUrl);

// At v1 this discovers the term-deposit family's TWO modules: the EdgeStarted constitution saga and
// the EventAutoStarted renewal saga (which starts on the engine's DepositMatured fact with a non-NONE
// ce_autorenewalpolicy header, in its OWN consumer group over the same family topic; ADR-IC-003 §A7 —
// the engine resolves every renewal fact from the closing deposit, so its command body is minimal).
var familySagaModules = new SagaModuleLoader().LoadAll(SagaModuleLoader.FamilySagaAssemblies(), sagaModuleContext);

// The SUBSTRATE-OWNED settlement saga (bd babelstone-t7o3.15, ADR-PC-032; ADR-IC-018 Amendment
// 2026-06-24). UNLIKE the family modules it lives in the SUBSTRATE — it names no family, keying only
// on the Movement atom's generic direction + opaque account_ref — so it is the one shared home that
// effects any family's cash leg, and it is NOT discovered from a family assembly: the host constructs
// it explicitly (a substrate name, not a family name). EventAutoStarted on a Movement-bearing event (a
// ce_movementorigin == Originated header); the direction branch (debit funds-gated Reserve->Confirm vs
// credit confirmation-gated Confirm) is resolved by the machine's IEventSubstitutor from the promoted
// ce_movementdirections list. Its subscribe set — the family integration topics where Movement-bearing
// events arrive — is the UNION of the DISCOVERED family modules' declared FamilyIntegrationTopics
// (each answers from its catalogue-generated constants), so neither the substrate module (ORCH-3) nor
// this host names a family topic: a new family's integration topics arrive with its discovered module,
// zero diff here. Ordered for a deterministic subscription list across boots.
var familyIntegrationTopics = familySagaModules
    .SelectMany(module => module.FamilyIntegrationTopics)
    .Distinct(StringComparer.Ordinal)
    .Order(StringComparer.Ordinal)
    .ToArray();

var sagaModules = new List<ISagaModule>(familySagaModules)
{
    new SettlementSagaModule(sagaModuleContext, familyIntegrationTopics),
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
        // Off by default; Kafka:Debug (e.g. "cgrp,broker,consumer") makes the loop's log handler surface
        // the librdkafka group-coordination sequence — for diagnosing a consumer that connects but never
        // joins its group (bd babelstone-u79p.17).
        KafkaDebug = builder.Configuration["Kafka:Debug"],
    };

    // Capture the per-module loop in the factory closure (NOT a DI singleton) so each hosted service
    // gets its OWN loop+group; the shared advance handler is resolved from DI. Both the hosted service and
    // the loop get their loggers wired (bd babelstone-u79p.17): a null logger on either turned a dead
    // consumer into a silent stall — the loop's error/log handlers + the service's backoff log now surface it.
    builder.Services.AddHostedService(sp =>
        new SagaInboxConsumerService(
            new SagaConsumeLoop(
                moduleOptions,
                sp.GetRequiredService<SagaAdvanceHandler>(),
                logger: sp.GetRequiredService<ILogger<SagaConsumeLoop>>()),
            sp.GetRequiredService<ILogger<SagaInboxConsumerService>>()));
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
    // The engine-CA settlement counterparty (ADR-PC-043). Null keeps every leg on the
    // legacy ACL (SettlementBaseUrl) — the substrate settlement router fails an engine-ca leg closed when it
    // is unset, so an engine-ca-targeted leg never silently settles on the legacy core.
    EngineCaSettlementBaseUrl = engineCaSettlementBaseUrl,
});
// The routing seam is multi-saga (ADR-IC-018 §D2): each family module contributed its own
// ISagaCommandRouter via the module loop above (it serves its own saga_type). The CompositeCommandRouter
// is the ICommandRouter the dispatcher consumes — it collects every ISagaCommandRouter (GetServices) into
// a saga_type → router registry and delegates by the outbox row's saga_type, naming no family.
builder.Services.AddSingleton<ICommandRouter>(sp =>
    new CompositeCommandRouter(sp.GetServices<ISagaCommandRouter>()));
builder.Services.AddHttpClient();

// Caller-side internal mTLS on the dispatcher's outbound HTTP hop (bd babelstone-zla1.12.10; ADR-IC-006
// §P5 Boundary 2 / ADR-IC-016 plane (i)). The dispatcher POSTs to the engine via the DEFAULT
// IHttpClientFactory client (SagaCommandDispatchDrainer.CreateClient()). Once the engine's Kestrel host
// is flipped to HTTPS-with-a-REQUIRED-client-cert (the gated overlays/staging/internal-mtls.patch.yaml),
// that hop must PRESENT a client cert signed by the shared internal CA and PIN the engine's server cert
// to that same CA — otherwise the RequireCertificate handshake rejects it. ConfigureHttpClientDefaults
// applies the mTLS primary handler to EVERY factory client (there is only the default one), so no named
// client is needed and SagaCommandDispatchDrainer stays untouched. It is OFF unless InternalMtls:CaCertPath
// is configured (staging mounts the client cert + CA and sets it), so the demo/local/test hosts keep their
// plain-HTTP default byte-for-byte. On staging the CA env is set UNCONDITIONALLY, so this hop is https +
// client-cert the moment the manifest is applied — which is why the callers, the server patch, and the
// deck-sync land TOGETHER in one maintenance window (internal-mtls.patch.yaml ROLLOUT ORDER steps 3–4);
// applying the caller half while the engine is still plain HTTP would break it. Wiring only — the
// orchestrator stays extraction-ready (ADR-PC-019 §P2).
if (InternalMtls.IsConfigured(builder.Configuration))
{
    builder.Services.ConfigureHttpClientDefaults(http =>
        http.ConfigurePrimaryHttpMessageHandler(() => InternalMtls.BuildHandler(builder.Configuration)));
}
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

// The SCHEDULED PAYOUT-LANDING RECONCILER (bd babelstone-qa92.2; ADR-PC-043 reconcile-signals-only,
// ADR-PC-023 clock-free, ADR-IC-019 cadence). In plain English: PayoutLandingReconciler already knows how to
// spot a payout that never landed, landed twice, or landed at the wrong amount — but until now nothing RAN it
// in production, so its mismatch signals reached no one. This wires a clock-owning poll loop (the shared
// Babelstone.Cadence worker the notification scheduler and the lifecycle driver reuse) that, once per tick,
// reads the source payouts + CA landings as-of today, reconciles them, and surfaces every non-matched signal
// to an OPERATOR sink — a per-class Prometheus counter + a structured log (ADR-IC-007 Layer 1). It moves no
// money: signal only (ADR-PC-043). The clock lives HERE in the worker; the injected asOf flows into the
// classifier, which stays clock-free (ADR-PC-023 §6).
//
// The READ side (IPayoutLandingSource — the live movement-ledger + CA-landing read as-of a date) needs a
// running stack and is a human bring-up follow-up (bd babelstone-qa92.2 §Scope): the worker starts ONLY when
// a host registers an IPayoutLandingSource AND opts in via Reconciler:PayoutLanding:Enabled, so the demo/local
// hosts (which register no source) run byte-for-byte unchanged and CI exercises the pass against an in-memory
// fake. The sink, the metrics, and the cadence knobs are registered regardless — with no meter listener every
// counter/gauge is a near-zero-cost no-op.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(BabelstoneTelemetry.ActivitySource);
// The operator-facing signal sink (ADR-IC-007 Layer 1): the per-ReconciliationClass Prometheus counter +
// structured log. Registered unconditionally — it is the default sink the pass emits through; a deployment
// MAY replace it (e.g. to add a spine operational event, ADR-PC-043 names that optional) without touching
// the pass.
builder.Services.AddSingleton<IReconciliationSignalSink, OperatorReconciliationSignalSink>();
// The reconciler's cadence/retry/backoff knobs (ADR-PC-023 §6 — the downstream consumer owns the read
// cadence). The one-hour default sits well inside a DROP-SLA-day tolerance; an operator tunes it from the
// "Reconciler:PayoutLanding" section. Registered as its own instance (not a shared singleton) so it cannot
// collide with another cadence consumer's options in the same host.
var payoutReconcilerPollSeconds =
    builder.Configuration.GetValue<double?>("Reconciler:PayoutLanding:PollIntervalSeconds");
var payoutReconcilerOptions = payoutReconcilerPollSeconds is > 0
    ? new CadenceSchedulerOptions { PollInterval = TimeSpan.FromSeconds(payoutReconcilerPollSeconds.Value) }
    : new CadenceSchedulerOptions();
// The interim DROP SLA (DefaultDropSlaDays=3, Q-AG calibration pending — ADR-PC-043 §Residual risks): the
// reconciler's own default unless an operator overrides it here (a single configured value, never a literal
// restated). A null flows straight to PayoutLandingReconciler.DefaultDropSlaDays.
var payoutReconcilerDropSlaDays =
    builder.Configuration.GetValue<int?>("Reconciler:PayoutLanding:DropSlaDays");
var payoutReconcilerEnabled =
    builder.Configuration.GetValue("Reconciler:PayoutLanding:Enabled", defaultValue: false);
if (payoutReconcilerEnabled)
{
    // The worker starts only when the host also registered an IPayoutLandingSource (the live read side, a
    // human follow-up). We register the pass + worker as a hosted service; a host that flips Enabled without
    // wiring a source fails loud at resolve time (a mis-wired deployment must not run a blind reconciler),
    // exactly the fail-loud stance the lifecycle driver's connection resolution takes.
    builder.Services.AddSingleton(sp => new PayoutLandingReconciliationSchedulePass(
        sp.GetRequiredService<IPayoutLandingSource>(),
        sp.GetRequiredService<IReconciliationSignalSink>(),
        payoutReconcilerDropSlaDays,
        sp.GetService<ILogger<PayoutLandingReconciliationSchedulePass>>()));
    builder.Services.AddSingleton(payoutReconcilerOptions);
    builder.Services.AddHostedService(sp => new PayoutLandingReconciliationWorker(
        sp.GetRequiredService<PayoutLandingReconciliationSchedulePass>(),
        payoutReconcilerOptions,
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<PayoutLandingReconciliationWorker>>(),
        sp.GetService<System.Diagnostics.ActivitySource>()));
}

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
