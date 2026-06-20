using Babelstone.Engine;
using Babelstone.Engine.Hosting;
using Babelstone.EventStore;
using Babelstone.Families.TermDeposit;
using Babelstone.Families.TermDeposit.Application.Migrations;
using Babelstone.RateSheets;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Babelstone.Families.TermDeposit.Application;

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
            // The STORE codec (ADR-PC-028 §Decision): self-describing JSON fills events.payload (the
            // book of record) and is the sole decode/replay path. UNCHANGED by the dual-encode split.
            serviceProvider.GetRequiredService<IEventSerializer>(),
            serviceProvider.GetRequiredService<IPiiProtector>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            () => DepositPosition.Empty,
            // The catalog-gated relay (ADR-IC-017 §P1): the runtime writes an outbox row — the only
            // publishable artefact — ONLY for a catalogued integration event; an uncatalogued event is
            // store-only by construction. The shared IIntegrationEventCatalog (the real AvroSchemaCatalog,
            // registered in Program.cs) is the family-agnostic membership predicate.
            integrationEventCatalog: serviceProvider.GetRequiredService<IIntegrationEventCatalog>(),
            // The BUS codec (ADR-PC-028 §Decision dual-encode / STORE_BUS_ENCODING_EQUIVALENCE): when
            // Bus:Encoding=avro the host registers a BusEventSerializer (real Avro + registered
            // schema_id, ADR-IC-002 §P3 / ADR-IC-004 §P3) and the runtime encodes the outbox row with
            // it; with none registered this is null and the outbox reuses the JSON store codec (the
            // pre-split single-encoding). Resolving it here is what triggers the lazy SR registration
            // — only in avro mode, only at first command.
            busSerializer: serviceProvider.GetService<BusEventSerializer>()?.Inner,
            // A.11 snapshot wiring (ADR-PC-003 §P2 / bd e6fr.11): flip v1 snapshots ON. The typed
            // SnapshotStore<DepositPosition> composes the family-agnostic spine snapshot storage
            // (ISnapshotStorage, registered in Program.cs) with the family's structural JSON state codec
            // — the SAME JsonStateSerializer the projections use, so a snapshot serialises deposit-position
            // state exactly as a projection row does (deterministic, no PII — ADR-PC-004 §P2). Threading a
            // non-null store + policy is what enables the post-commit write side; LoadAsync already did
            // snapshot-then-tail on the read side.
            snapshots: new SnapshotStore<DepositPosition>(
                serviceProvider.GetRequiredService<ISnapshotStorage>(),
                new JsonStateSerializer<DepositPosition>()),
            // The v1 cadence: the COMPOSING per-N + lifecycle + calendar trigger (event-store §8.1 /
            // ADR-PC-003 §P2). The per-N threshold is configurable via Engine:SnapshotEveryNEvents
            // (default 100 — the §8.1 "typically 100-1000" floor, comfortably above a term deposit's
            // ~24-260-event lifecycle so the cold-replay budget, event-store §8.2, is met without churn).
            // CountBasedSnapshotPolicy ORs in the two boundary flags: the LIFECYCLE flag is supplied by the
            // family's events (DepositConstituted/Matured/Renewed/… override DomainEvent.IsLifecycleBoundary),
            // and the CALENDAR flag is computed by the runtime from the calendar policy below — so all three
            // triggers (bd e6fr.12) are now live with no rewiring.
            snapshotPolicy: new CountBasedSnapshotPolicy(
                ctx.Configuration.GetValue("Engine:SnapshotEveryNEvents", 100L)),
            // The CALENDAR-boundary trigger (ADR-PC-003 §P2 / event-store §8.1): a snapshot at month-end /
            // year-end so as-of queries at reporting-period boundaries return without a long replay. The
            // granularity is per-family/host config via Engine:SnapshotCalendarGranularity (None/Month/Year;
            // default Month). The runtime owns the transaction-time clock (ADR-PC-010 §P5), so IT — not a
            // handler — decides the crossing by comparing the previous head's transaction_time to the
            // append's; a None granularity turns the calendar trigger off entirely.
            calendarBoundaryPolicy: new CalendarBoundaryPolicy(
                ctx.Configuration.GetValue(
                    "Engine:SnapshotCalendarGranularity", CalendarGranularity.Month)),
            // Fail-soft sink for a post-commit snapshot-write failure (ADR-PC-003 §P2): the append already
            // committed and IS the book of record, so a snapshot-write blip must not fail the command — it
            // is logged (so the §P6 snapshot-lag alarm sees it) and the next rebuild is merely slower, never
            // wrong. The kernel hands the exception out as a callback (logging-library-agnostic spine); the
            // host binds it to its logger here.
            onSnapshotError: ex => serviceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Babelstone.Families.TermDeposit.Snapshots")
                .LogWarning(ex, "Post-commit snapshot write failed; the committed event is unaffected, the next rebuild is slower not wrong.")));

        // The engine-side product-config store (Fork B rework, bd t7o3.11 / 3k10 / c8d8, ADR-PC-009):
        // the engine resolves product_code → the structural facts (term / variant / renewal policy /
        // coupon cadence / role) at constitution from the committed product-configs/*.yaml, so the
        // orchestrator carries NO product-family knowledge — the engine is the single home of product
        // config. Disk-backed and load-once (mirrors HostPack / HostPackStore); the dir is configurable
        // via Engine:ProductConfigsDir and auto-found from the binary otherwise. This is a family-agnostic
        // spine seam (IProductConfigStore lives in Babelstone.RateSheets) consumed by the family decider —
        // the family→spine arrow keeps EngineFamilyAgnosticTests green.
        services.AddSingleton<IProductConfigStore>(
            _ => new YamlProductConfigStore(ctx.Configuration["Engine:ProductConfigsDir"]));

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
            ]),
            // The engine-side product-config resolver (Fork B rework): the minimal saga body
            // {deposit_id, product_id, principal_cents, funding_account} resolves its structural facts
            // through this seam at constitution (ADR-PC-009 / ADR-PC-008 §S2).
            productConfigStore: serviceProvider.GetRequiredService<IProductConfigStore>()));

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
