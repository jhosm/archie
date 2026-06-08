using Babelstone.Orchestrator;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Migrations;
using Babelstone.Orchestrator.Saga;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// The in-house saga orchestrator host (ADR-IC-003 "Event-driven application orchestrator").
// A worker, not an HTTP API (ADR-IC-003 §S2 "a Redpanda consumer like every other service")
// — so Host.CreateApplicationBuilder, not WebApplication. This composition root applies the
// saga schema and wires the hand-rolled state machine, the persistence stores, and the
// inbox-driven advance handler. The Redpanda consume loop (the engine's InboxPump, G.2) is
// wired onto the SagaAdvanceHandler here once H.2 (babelstone-n55u) brings the Confluent/Avro
// host surface; this substrate (babelstone-mj2i) delivers the state machine + persistence +
// idempotent advance the loop will drive.

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

// The hand-rolled ConstitutionProcess state machine (ADR-IC-003 §P2: the table is the spec).
// One machine per saga type; H.3 renewal (babelstone-mtto) registers its own alongside.
builder.Services.AddSingleton<ISagaStateMachine, ConstitutionProcess>();
builder.Services.AddSingleton<SagaStateStore>();
builder.Services.AddSingleton<SagaTransitionLog>();

// The command sink is the outbox seam (ADR-IC-003 §P1): H.2 replaces this recorder with the
// real outbox-row writer. Until then the substrate proves the advance handler decides and
// routes the right commands.
builder.Services.AddSingleton<ISagaCommandSink, RecordingCommandSink>();

builder.Services.AddSingleton(sp => new SagaAdvanceHandler(
    sp.GetRequiredService<ISagaStateMachine>(),
    sp.GetRequiredService<SagaStateStore>(),
    sp.GetRequiredService<SagaTransitionLog>(),
    sp.GetRequiredService<ISagaCommandSink>())
{
    StartEventType = ConstitutionProcess.ConstitutionRequested,
});

builder.Services.AddHostedService(sp => new SagaMigrationHostedService(migrationConnectionString));

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
