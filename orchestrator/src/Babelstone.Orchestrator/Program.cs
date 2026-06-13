using Babelstone.Orchestrator;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Migrations;
using Babelstone.Orchestrator.Outbox;
using Babelstone.Orchestrator.Saga;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// The in-house saga orchestrator host (ADR-IC-003 "Event-driven application orchestrator").
// A worker, not an HTTP API (ADR-IC-003 §S2 "a Redpanda consumer like every other service")
// — so Host.CreateApplicationBuilder, not WebApplication. This composition root applies the
// saga schema and wires the hand-rolled state machine, the persistence stores, the inbox-driven
// advance handler, AND the Redpanda consume loop (t7o3.2) that actually READS events off the bus
// and drives the saga. Before t7o3.2 nothing consumed events, so a started saga never progressed;
// now the hosted SagaInboxConsumerService subscribes to the saga's topics, decodes each record's
// CloudEvents headers into a PII-free SagaInboxEvent, and drives the SagaAdvanceHandler inside one
// transaction that commits the Kafka offset only after the DB transaction commits.

var builder = Host.CreateApplicationBuilder(args);

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

// The command sink is the outbox seam (ADR-IC-003 §P1). H.2 (babelstone-n55u) swaps the
// substrate's in-memory RecordingCommandSink for the REAL durable writer: each command the saga
// decides is a saga_outbox row committed in the SAME transaction as the state move (effectively-
// once command emission). The recorder remains as a test stand-in only.
builder.Services.AddSingleton<ISagaCommandSink, SagaCommandOutboxSink>();

builder.Services.AddSingleton(sp => new SagaAdvanceHandler(
    sp.GetRequiredService<ISagaStateMachine>(),
    sp.GetRequiredService<SagaStateStore>(),
    sp.GetRequiredService<SagaTransitionLog>(),
    sp.GetRequiredService<ISagaCommandSink>())
{
    StartEventType = ConstitutionProcess.ConstitutionRequested,
});

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

var host = builder.Build();
await host.RunAsync();

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
