using Babelstone.EventStore;
using Babelstone.Families.TermDeposit;
using Babelstone.Families.TermDeposit.Application;
using Babelstone.Families.TermDeposit.Application.Migrations;
using Babelstone.RateSheets;
using Microsoft.Extensions.Hosting;

namespace Babelstone.Engine.Api;

/// <summary>
/// The term-deposit family's host composition (ADR-PC-021 §D4/§P4), realized as an
/// <see cref="IFamilyHostModule"/>. It owns everything term-deposit-specific that used to be
/// hand-wired inline in <c>Program.cs</c>: the closed-generic <see cref="AggregateRuntime{TState}"/>
/// over <see cref="DepositPosition"/> (seeded <c>() =&gt; DepositPosition.Empty</c>, fed the family's
/// fold registry), the <see cref="TermDepositConstitutionService"/> decider, and the
/// <c>/v1/deposits</c> command/query surface (<see cref="DepositsEndpoints"/>).
///
/// The pack-resolved primitives (<c>act_360</c> day-count, <c>irs_juros</c> withholding) are
/// per-deployment/jurisdiction names the pinned pack declares; they remain inlined here for the
/// walking skeleton (lifting them to <see cref="FamilyHostContext.Configuration"/> is a separable
/// follow-up, independent of this composition seam).
/// </summary>
public sealed class TermDepositHostModule : IFamilyHostModule
{
    public string FamilyName => "term_deposit";

    public void ConfigureServices(IServiceCollection services, FamilyHostContext ctx)
    {
        // The durable runtime over the term-deposit family. The family owns this closed generic, so
        // the host never names DepositPosition. Shared infrastructure (store, sink, codec, PII
        // protector, clock) is resolved from the container; only the fold registry + seed are ours.
        services.AddSingleton(serviceProvider => new AggregateRuntime<DepositPosition>(
            serviceProvider.GetRequiredService<IEventStore>(),
            serviceProvider.GetRequiredService<IEventSink>(),
            TermDepositFamilyModule.Registry(),
            serviceProvider.GetRequiredService<IEventSerializer>(),
            serviceProvider.GetRequiredService<IPiiProtector>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            () => DepositPosition.Empty,
            // The catalog-gated relay (ADR-IC-017 §P1): the runtime writes an outbox row — the only
            // publishable artefact — ONLY for a catalogued integration event; an uncatalogued event is
            // store-only by construction. The shared IIntegrationEventCatalog (the real AvroSchemaCatalog,
            // registered in Program.cs) is the family-agnostic membership predicate.
            integrationEventCatalog: serviceProvider.GetRequiredService<IIntegrationEventCatalog>()));

        // The term-deposit decider (ADR-PC-021): this module is its composition root (§D4).
        services.AddSingleton(serviceProvider => new TermDepositConstitutionService(
            serviceProvider.GetRequiredService<AggregateRuntime<DepositPosition>>(),
            serviceProvider.GetRequiredService<IRateSheetStore>(),
            serviceProvider.GetRequiredService<ISettlementPort>(),
            ctx.Pack,
            dayCountPrimitive: "act_360",
            withholdingPrimitive: "irs_juros",
            // Early-termination policy (02 §2.5) is per-PRODUCT config the bank's pricing team owns; for
            // the walking skeleton it is pinned engine-instance config (ADR-PC-009), like the primitives
            // above. The PT default banded schedule: 100% of accrued interest if broken in the first 30
            // days, 50% up to 90 days, 25% thereafter — the §2.5 worked example. A per-product config
            // registry resolving it per deposit is later work (F.4 ships the decider, not the registry).
            earlyTerminationPolicy: EarlyTerminationPolicy.Banded(
            [
                new EarlyTerminationBand(UpToDays: 30, PenaltyBasisPoints: 10_000, PenaltyBasis.AccruedInterest),
                new EarlyTerminationBand(UpToDays: 90, PenaltyBasisPoints: 5_000, PenaltyBasis.AccruedInterest),
                new EarlyTerminationBand(UpToDays: null, PenaltyBasisPoints: 2_500, PenaltyBasis.AccruedInterest),
            ])));

        // D.2 projection runtime (ADR-PC-002 §P4, two-modes §5.4): the family declares its
        // projections (currently just the deposit position) + their folds; the generic runtime
        // (registry + drainer) lives in the spine. The async relay materialises them into the
        // bitemporal `projections` table. v1 runs every projection async — the runtime above is
        // wired with no post-commit hook — so the live read path (GET /v1/deposits) is unaffected;
        // switching reads to the materialised projection is D.3/D.4.
        services.AddSingleton<IProjectionModule, TermDepositProjectionModule>();
        services.AddSingleton(serviceProvider =>
        {
            var infra = new ProjectionInfra(
                serviceProvider.GetRequiredService<IProjectionStorage>(),
                serviceProvider.GetRequiredService<IEventSerializer>());
            var bitemporalRunners = serviceProvider.GetServices<IProjectionModule>().SelectMany(module => module.CreateRunners(infra));

            // D.4 CQRS read model (ADR-IC-005): the denormalized read-model runner is its own kind
            // alongside the four bitemporal projections, sharing the same async drainer/relay. It
            // folds the same deposit-position state into read_model.deposits (the I.2 Query API
            // surface). Composed here from the FAMILY-OWNED read-model store (the deposit-shaped
            // table is the family's domain shape, not the spine's — ADR-PC-021 §D2/§P2); the family
            // supplies the fold + the state→row mapper over the generic ReadModelInfra<TRow>. The
            // read_model.deposits schema itself is now FAMILY-OWNED too: the family's own migration
            // set (Babelstone.Families.TermDeposit.Application.Migrations, 0001_read_model.sql)
            // creates it, applied by the ReadModelMigrationHostedService registered below — the
            // engine event-store migrations carry zero family-named tables (ADR-PC-021 family-owned
            // ownership).
            var readModelInfra = new ReadModelInfra<DepositReadModelRow>(
                serviceProvider.GetRequiredService<IDepositReadModelStore>(),
                serviceProvider.GetRequiredService<IEventSerializer>());
            var readModelRunner = new TermDepositProjectionModule().CreateReadModelRunner(readModelInfra);

            return new ProjectionRegistry(bitemporalRunners.Append(readModelRunner));
        });
        services.AddSingleton(serviceProvider => new ProjectionDrainer(
            serviceProvider.GetRequiredService<IEventStore>(),
            serviceProvider.GetRequiredService<IProjectionCheckpointStore>(),
            serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(new ProjectionRelayOptions());
        services.AddHostedService<ProjectionRelayService>();

        // The family OWNS its read-model schema (ADR-PC-021 family-owned ownership): read_model.deposits
        // is a family-NAMED table, so its forward-only migration set lives in the family's Application
        // assembly, not the engine's. The HOST is the composition root that may name a family (ADR-PC-021
        // A2 — the spine still may not), so it is here, not in the spine, that we apply the family
        // migration. This hosted service runs the family MigrationRunner on startup against the migration
        // connection string (DDL privileges, ADR-PC-001 §P3). It assumes the ENGINE schema is already
        // present — the engine host does NOT run the engine migrations today (only deployment machinery +
        // tests do) — and the family migration's own fail-loud guard RAISEs if the babelstone_engine role
        // (engine migration 0002) is absent, so an out-of-order run fails clearly rather than corrupting
        // state.
        //
        // Resolution prefers a DEDICATED migration-role connection (the production split: DDL privileges
        // separate from the runtime role, ADR-PC-001 §P3) and falls back to the runtime Engine connection
        // for the dev/test path, which uses one superuser connection for both (the same single-connection
        // dev posture Program.cs takes for ConnectionStrings:Engine). It still fails fast if NOTHING
        // resolves, rather than booting against an un-migrated read model (mirroring the orchestrator's
        // SagaMigrationHostedService).
        var migrationConnectionString =
            ctx.Configuration.GetConnectionString("EngineMigration")
            ?? ctx.Configuration["Engine:MigrationConnectionString"]
            ?? Environment.GetEnvironmentVariable("ENGINE_MIGRATION_CONNECTION_STRING")
            ?? ctx.Configuration.GetConnectionString("Engine");
        services.AddHostedService(_ => new ReadModelMigrationHostedService(migrationConnectionString));
    }

    public void MapEndpoints(IEndpointRouteBuilder app) => DepositsEndpoints.Map(app);
}

/// <summary>
/// Applies the term-deposit family's read-model schema on startup (the family OWNS this schema —
/// ADR-PC-021 family-owned ownership; <c>read_model.deposits</c> is a family-named table, so it lives
/// in the family's migration set, not the engine's). A hosted service so the host's lifetime owns it;
/// idempotent — a boot with nothing pending is a no-op (the family <see cref="MigrationRunner"/>'s own
/// <c>schema_migrations_term_deposit</c> ledger guards it). Modelled on the orchestrator's
/// <c>SagaMigrationHostedService</c>. Refuses to run with no migration connection string rather than
/// booting against an un-migrated read model.
///
/// Engine-before-family ordering: this assumes the engine event-store schema is already present (the
/// engine host runs no engine migrations today; deployment machinery + tests apply them). The family
/// migration's fail-loud SQL guard RAISEs a clear error if the <c>babelstone_engine</c> role (engine
/// migration 0002) is absent, so an out-of-order boot fails loud rather than silently.
/// </summary>
internal sealed class ReadModelMigrationHostedService(string? migrationConnectionString) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(migrationConnectionString))
        {
            throw new InvalidOperationException(
                "No engine migration connection string configured for the term-deposit read-model "
                + "migrations. Set ConnectionStrings:EngineMigration, Engine:MigrationConnectionString, "
                + "or ENGINE_MIGRATION_CONNECTION_STRING.");
        }

        await new MigrationRunner(migrationConnectionString).ApplyAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
