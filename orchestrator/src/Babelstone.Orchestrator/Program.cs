using Babelstone.Orchestrator;
using Babelstone.Orchestrator.Dispatch;
using Babelstone.Orchestrator.Edge;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Migrations;
using Babelstone.Orchestrator.Outbox;
using Babelstone.Orchestrator.Saga;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

// The hand-rolled ConstitutionProcess state machine (ADR-IC-003 §P2: the table is the spec).
// One machine per saga type; H.3 renewal (babelstone-mtto) registers its own alongside.
builder.Services.AddSingleton<ISagaStateMachine, ConstitutionProcess>();
builder.Services.AddSingleton<SagaStateStore>();
builder.Services.AddSingleton<SagaTransitionLog>();
// The per-saga business-reference store (bd babelstone-t7o3.1): the edge writes the pinned
// references at start; the approval fork (self-emitted at VALIDATIONS_COMPLETE) and the
// command-payload assembly read them. A shared singleton the sink and the advance handler both use.
builder.Services.AddSingleton<SagaBusinessReferenceStore>();

// The command sink is the outbox seam (ADR-IC-003 §P1). H.2 (babelstone-n55u) swaps the
// substrate's in-memory RecordingCommandSink for the REAL durable writer: each command the saga
// decides is a saga_outbox row committed in the SAME transaction as the state move (effectively-
// once command emission). With the pinned business references present it writes the FULL typed
// command payloads (bd babelstone-t7o3.1); the recorder remains as a test stand-in only.
builder.Services.AddSingleton<ISagaCommandSink>(sp =>
    new SagaCommandOutboxSink(sp.GetRequiredService<SagaBusinessReferenceStore>()));

// The consume loop ADVANCES sagas only — it never starts them. Sagas are started exclusively at the
// edge (EdgeSagaStarter), which pins the business references in the same transaction as the STARTED
// row; the loop resumes a saga on a consumed advance event (ADR-IC-003 §S2; bd babelstone-t7o3.9).
builder.Services.AddSingleton(sp => new SagaAdvanceHandler(
    sp.GetRequiredService<ISagaStateMachine>(),
    sp.GetRequiredService<SagaStateStore>(),
    sp.GetRequiredService<SagaTransitionLog>(),
    sp.GetRequiredService<ISagaCommandSink>(),
    sp.GetRequiredService<SagaBusinessReferenceStore>()));

// The schema migration runs FIRST (registered before the consume loop): hosted services start in
// registration order, so the saga schema is applied before the consumer can write its first dedup
// row. Idempotent — a boot with nothing pending is a no-op (the MigrationRunner ledger guards it).
builder.Services.AddHostedService(sp => new SagaMigrationHostedService(migrationConnectionString));

// The Redpanda consume loop (t7o3.2) — the same hosted-BackgroundService shape the engine's
// outbox relay and inbox consumer use. Three pieces register here:
//   • SagaInboxConsumerOptions — the broker endpoint, the consumer group id, and the topics the
//     constitution saga reacts to (the internal deposits.process.events domain topic, Document 05
//     §1). The Kafka bootstrap address is a broker ENDPOINT, not a credential — already plaintext in
//     infra/compose.yaml and the k8s manifests — so it resolves straight from IConfiguration
//     (Kafka:BootstrapServers via env/appsettings), distinct from the orchestrator runtime DB
//     credential which goes through the ADR-PC-004 Amendment A1 boundary. The dev default matches the
//     Redpanda external listener in infra/compose.yaml (localhost:19092).
//   • SagaConsumeLoop — the impure shell that owns the consumer, the connection/transaction, and the
//     offset commit (commit AFTER the DB tx → at-least-once delivery, effectively-once advance).
//   • AddHostedService<SagaInboxConsumerService> — the poll loop that drives the loop with
//     exponential backoff on a transient failure (the offset stays uncommitted → redelivery).
var bootstrapServers = builder.Configuration["Kafka:BootstrapServers"] ?? "localhost:19092";
var consumerGroupId = builder.Configuration["Kafka:GroupId"] ?? "babelstone-orchestrator";
builder.Services.AddSingleton(new SagaInboxConsumerOptions
{
    // The runtime connection string must be present to consume: refusing here (rather than at the
    // first consume) fails fast against a mis-wired deployment, the same stance the migration
    // hosted service takes. A null/blank value throws a clear ArgumentException on the required-init.
    ConnectionString = runtimeConnectionString
        ?? throw new InvalidOperationException(
            "No orchestrator runtime connection string configured. Set ConnectionStrings:Orchestrator, " +
            "Orchestrator:ConnectionString, or ORCHESTRATOR_CONNECTION_STRING."),
    BootstrapServers = bootstrapServers,
    GroupId = consumerGroupId,
    Topics = SagaConsumeTopics.ConstitutionProcessTopics,
});
builder.Services.AddSingleton(sp => new SagaConsumeLoop(
    sp.GetRequiredService<SagaInboxConsumerOptions>(),
    sp.GetRequiredService<SagaAdvanceHandler>()));
builder.Services.AddHostedService<SagaInboxConsumerService>();

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
var engineBaseUrl = builder.Configuration["Engine:BaseUrl"]
    ?? builder.Configuration.GetConnectionString("Engine")
    ?? "http://localhost:8080";
var settlementBaseUrl = builder.Configuration["Settlement:BaseUrl"] ?? "http://localhost:8089";
builder.Services.AddSingleton(new SagaCommandDispatcherOptions
{
    ConnectionString = runtimeConnectionString
        ?? throw new InvalidOperationException(
            "No orchestrator runtime connection string configured. Set ConnectionStrings:Orchestrator, " +
            "Orchestrator:ConnectionString, or ORCHESTRATOR_CONNECTION_STRING."),
    EngineBaseUrl = engineBaseUrl,
    SettlementBaseUrl = settlementBaseUrl,
});
builder.Services.AddSingleton<ICommandRouter, SagaCommandRouter>();
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
    sp.GetRequiredService<SagaAdvanceHandler>()));
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
