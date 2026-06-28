using Babelstone.Engine;
using Babelstone.Engine.Hosting;
using Babelstone.EventStore;
using Babelstone.Families.PersonalLoan;
using Babelstone.Families.PersonalLoan.Application.Migrations;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Babelstone.Families.PersonalLoan.Application;

/// <summary>
/// The personal_loan (credito_pessoal) family's host composition (ADR-PC-021 §D4/§P4; ADR-PC-031 §P5),
/// realized as an <see cref="IFamilyHostModule"/> — the sibling of <c>TermDepositHostModule</c> for the
/// closed-end-asset (personal loan) family. It owns everything personal-loan-specific: the closed-generic
/// <see cref="AggregateRuntime{TState}"/> over <see cref="LoanPosition"/> (seeded
/// <c>() =&gt; LoanPosition.Empty</c>, fed the family's fold registry), the
/// <see cref="PersonalLoanConstitutionService"/> decider, and the <c>/v1/loans</c> command/query surface
/// (<see cref="LoansEndpoints"/>).
///
/// <para>
/// <b>Discovery, not a host edit (ADR-PC-031 §P5 / ADR-PC-021 §A3).</b> The host's
/// <c>HostModuleLoader</c> discovers this module by assembly-scan over the <c>Babelstone.Families.*.dll</c>
/// in its output dir (where the host's <c>ProjectReference</c> copies this assembly); the host names no
/// personal-loan type in code, so <c>ENGINE_API_HOST_FAMILY_AGNOSTIC</c> stays green. The pinned pack's
/// family-manifest (<c>families.yaml</c>) pins <c>personal_loan@2026.1</c>, so
/// <c>HostModuleLoader.CrossCheckAgainstPackManifest</c> passes fail-closed (ADR-PC-009 §P1).
/// </para>
/// <para>
/// <b>Read model.</b> The loan's THREE bitemporal projections — the loan position, the amortization
/// schedule, and the installment calendar (<see cref="PersonalLoanProjectionModule"/>) — materialise into
/// the GENERIC, engine-owned <c>projections</c> table, and the live read path folds the stream through the
/// <see cref="AggregateRuntime{TState}"/>; this module registers only its <see cref="IProjectionModule"/>,
/// and the family-agnostic projection relay term-deposit registers drains it (the relay's registry
/// enumerates EVERY registered <see cref="IProjectionModule"/>). The family ALSO now owns a denormalized
/// Postgres read-model table — <c>read_model.installment_calendar</c>, the forward next-installment read
/// surface — created by its OWN forward-only migration set
/// (<see cref="Babelstone.Families.PersonalLoan.Application.Migrations"/>, under its own
/// <c>schema_migrations_personal_loan</c> ledger), applied on startup by the
/// <see cref="ReadModelMigrationHostedService"/> registered below. That family-named table lives in the
/// family's migration set, NOT the engine's, so the engine event-store migrations still carry zero
/// personal-loan-named tables (ADR-PC-021 family-owned ownership; ADR-IC-005 §S1 — same Postgres tier).
/// </para>
/// <para>
/// <b>Read-model producer (bd babelstone-6cpq.12).</b> That table is now FED. This module registers the
/// family-owned <see cref="IInstallmentCalendarReadModelStore"/> plus a second
/// <see cref="IProjectionModule"/> whose runner is a <see cref="ReadModelRunner{TState,TRow}"/> over
/// <see cref="LoanPosition"/> — it folds the same position the live read path computes and UPSERTs the
/// next-unpaid occurrence per Active loan into <c>read_model.installment_calendar</c> with the ADR-IC-005
/// §P2 last_sequence idempotency guard. Routing the read-model runner through the
/// <see cref="IProjectionModule"/> seam (rather than standing up a second relay) is what lets the SAME
/// term-deposit-registered relay drain it — that relay's registry enumerates every registered module — so
/// the "installments due in [from, to)" range scan returns rows with no extra background loop. Mirrors
/// term-deposit's <c>read_model.deposits</c> feed (ADR-IC-005 §D4).
/// </para>
/// </summary>
public sealed class PersonalLoanHostModule : IFamilyHostModule
{
    // The single source of truth for this family's identity is its fold module — the SAME
    // FamilyName/SchemaVersion that stamps every EventEnvelope (ADR-PC-009 §P1), so the load-time
    // family-manifest cross-check compares the pinned pack against the value that actually rides on events.
    private static readonly PersonalLoanFamilyModule FoldModule = new();

    public string FamilyName => FoldModule.FamilyName;

    public string SchemaVersion => FoldModule.SchemaVersion;

    // aggregate_type == family_name by the engine's documented convention (ADR-IC-004 §Consequences).
    public string AggregateType => FoldModule.FamilyName;

    public void ConfigureServices(IServiceCollection services, FamilyHostContext ctx)
    {
        // The durable runtime over the personal_loan family. The family owns this closed generic, so the
        // host never names LoanPosition. Shared infrastructure (store, sink, codec, PII protector, clock,
        // integration-event catalog, snapshot storage) is resolved from the container; only the fold
        // registry + seed are ours. Mirrors TermDepositHostModule one-for-one over LoanPosition.
        services.AddSingleton(serviceProvider => new AggregateRuntime<LoanPosition>(
            serviceProvider.GetRequiredService<IEventStore>(),
            serviceProvider.GetRequiredService<IEventSink>(),
            PersonalLoanFamilyModule.Registry(),
            // The STORE codec (ADR-PC-028 §Decision): self-describing JSON fills events.payload (the book
            // of record) and is the sole decode/replay path.
            serviceProvider.GetRequiredService<IEventSerializer>(),
            serviceProvider.GetRequiredService<IPiiProtector>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            () => LoanPosition.Empty,
            // The catalog-gated relay (ADR-IC-017 §P1): the runtime writes a publishable outbox row ONLY for
            // a catalogued integration event; an uncatalogued event is store-only by construction. The
            // shared IIntegrationEventCatalog (the real AvroSchemaCatalog, registered in Program.cs) is the
            // family-agnostic membership predicate.
            integrationEventCatalog: serviceProvider.GetRequiredService<IIntegrationEventCatalog>(),
            // The BUS codec (ADR-PC-028 §Decision dual-encode): null in JSON mode (the outbox reuses the
            // JSON store codec), the real Avro+schema_id serializer when Bus:Encoding=avro is registered.
            busSerializer: serviceProvider.GetService<BusEventSerializer>()?.Inner,
            // Snapshot wiring (ADR-PC-003 §P2): the typed SnapshotStore<LoanPosition> composes the
            // family-agnostic spine snapshot storage (ISnapshotStorage, registered in Program.cs) with the
            // family's structural JSON state codec — the SAME JsonStateSerializer the projections use, so a
            // snapshot serialises loan-position state exactly as a projection row does (deterministic, no
            // PII — ADR-PC-004 §P2).
            snapshots: new SnapshotStore<LoanPosition>(
                serviceProvider.GetRequiredService<ISnapshotStorage>(),
                new JsonStateSerializer<LoanPosition>()),
            // The v1 cadence: per-N + lifecycle + calendar (event-store §8.1 / ADR-PC-003 §P2). The per-N
            // threshold is configurable via Engine:SnapshotEveryNEvents (default 100, comfortably above a
            // loan's ~36-installment lifecycle). CountBasedSnapshotPolicy ORs in the LIFECYCLE flag the
            // family's events supply (LoanDisbursed/LoanSettled/… override IsLifecycleBoundary) and the
            // CALENDAR flag the runtime computes below.
            snapshotPolicy: new CountBasedSnapshotPolicy(
                ctx.Configuration.GetValue("Engine:SnapshotEveryNEvents", 100L)),
            // The CALENDAR-boundary trigger (ADR-PC-003 §P2): a snapshot at month-/year-end so as-of
            // queries at reporting boundaries return without a long replay. Configurable via
            // Engine:SnapshotCalendarGranularity (None/Month/Year; default Month).
            calendarBoundaryPolicy: new CalendarBoundaryPolicy(
                ctx.Configuration.GetValue(
                    "Engine:SnapshotCalendarGranularity", CalendarGranularity.Month)),
            // Fail-soft sink for a post-commit snapshot-write failure (ADR-PC-003 §P2): the append already
            // committed and IS the book of record, so a snapshot blip must not fail the command — it is
            // logged and the next rebuild is merely slower, never wrong.
            onSnapshotError: ex => serviceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Babelstone.Families.PersonalLoan.Snapshots")
                .LogWarning(ex, "Post-commit snapshot write failed; the committed event is unaffected, the next rebuild is slower not wrong.")));

        // The personal_loan decider (ADR-PC-021): this module is its composition root (§D4). It has no
        // eager settlement dependency — every money-moving leg is de-settled onto the substrate-owned
        // settlement saga via Movement-bearing events (ADR-PC-032 slot 5; bd babelstone-t7o3.16). It depends
        // only on generic engine ports (AggregateRuntime, IRateSheetStore) plus the pinned pack.
        services.AddSingleton(serviceProvider => new PersonalLoanConstitutionService(
            serviceProvider.GetRequiredService<AggregateRuntime<LoanPosition>>(),
            serviceProvider.GetRequiredService<Babelstone.RateSheets.IRateSheetStore>(),
            ctx.Pack));

        // The family's projection declarations (two-modes §5.4): the loan position, the amortization
        // schedule, and the installment calendar — all bitemporal/async into the GENERIC projections table.
        // Registering only the IProjectionModule (not a relay) is deliberate: the family-agnostic projection
        // relay registered by the term-deposit module builds its ProjectionRegistry from EVERY registered
        // IProjectionModule (serviceProvider.GetServices<IProjectionModule>()), so a single relay drains both
        // families' projections — registering a second relay here would double-drain the shared
        // projections/checkpoint tables.
        services.AddSingleton<IProjectionModule, PersonalLoanProjectionModule>();

        // The FAMILY-OWNED installment-calendar read-model store (ADR-PC-021 §D2/§P2; bd babelstone-6cpq.12).
        // read_model.installment_calendar is a family-NAMED, loan-shaped table — one family's domain shape,
        // not the spine's — so its IInstallmentCalendarReadModelStore registration belongs HERE, in the
        // family's host module, NOT the host (which must name no family read-model type). The connection
        // string is the host's already-secret-resolved engine credential threaded via
        // FamilyHostContext.EngineConnectionString, so the ISecretProvider boundary (ADR-PC-004 A1) stays at
        // the host composition root — the family module never re-crosses it. The read-model runner below
        // resolves this store; the family's ReadModelMigrationHostedService (registered further down) owns
        // the table's schema. Mirrors TermDepositHostModule's IDepositReadModelStore registration.
        services.AddSingleton<IInstallmentCalendarReadModelStore>(
            _ => new PostgresInstallmentCalendarReadModelStore(ctx.EngineConnectionString));

        // The CQRS read-model PRODUCER (ADR-IC-005 §D4, bd babelstone-6cpq.12), surfaced as a SECOND
        // IProjectionModule so the term-deposit-registered, family-agnostic relay drains it with no extra
        // background loop: that relay builds its ProjectionRegistry from EVERY registered IProjectionModule
        // (serviceProvider.GetServices<IProjectionModule>()), so contributing the read-model runner here is
        // all it takes for "installments due in [from, to)" to return rows. The runner folds the SAME
        // LoanPosition the live read path computes and UPSERTs the next-unpaid occurrence per Active loan
        // into read_model.installment_calendar under the ADR-IC-005 §P2 last_sequence idempotency guard
        // (PersonalLoanProjectionModule.CreateReadModelRunner / MapToReadModel). Term-deposit appends its
        // read-model runner directly to the registry it composes; personal_loan reaches that same shared
        // registry through this module seam instead — same outcome, lane-local wiring.
        services.AddSingleton<IProjectionModule>(serviceProvider =>
            new InstallmentCalendarReadModelModule(
                serviceProvider.GetRequiredService<IInstallmentCalendarReadModelStore>()));

        // The operator pack-migration write-path (ADR-PC-009 §P3, surface §3.6): re-pin a live instance to a
        // newer pack by appending the engine-declared PackVersionMigrated through this family's runtime. The
        // mechanics are family-AGNOSTIC and live in the engine HOSTING library
        // (Babelstone.Engine.Hosting.PackMigrationService<TState>, the non-spine host/command-side assembly —
        // ADR-PC-021 §A9/§A11) — they run no personal-loan domain logic, only read each instance's current
        // pin off the event store head and append the migration pinned to the target pack (the re-pin lives
        // on the envelope). The family closes the generic over its own LoanPosition here, composing the
        // family runtime + the shared event store. Mirrors TermDepositHostModule one-for-one over LoanPosition.
        services.AddSingleton(serviceProvider => new PackMigrationService<LoanPosition>(
            serviceProvider.GetRequiredService<AggregateRuntime<LoanPosition>>(),
            serviceProvider.GetRequiredService<IEventStore>(),
            FoldModule.FamilyName));

        // Expose the closed write-path through the family-AGNOSTIC IPackMigrationService facade so the single
        // dispatching /v1/pack-migrations route (registered once at host level) can select it by
        // product_family without naming LoanPosition (ADR-PC-021 §P2). The same singleton instance is
        // returned — the facade is just the non-generic view the endpoint dispatches over.
        services.AddSingleton<IPackMigrationService>(
            serviceProvider => serviceProvider.GetRequiredService<PackMigrationService<LoanPosition>>());

        // The family side of the surface §3.6 instance_filter seam: resolve the
        // { product_family, currently_active } predicate to the live loan population. Unlike term-deposit
        // (which queries a family-OWNED read model), personal_loan has NO read model that lists active loans,
        // so this resolver folds the EVENT STORE — it enumerates the family's streams
        // (IEventStore.ReadStreamIdsAsync) and keeps the ones whose folded LoanPosition is still
        // LoanLifecycle.Active (the family owns what "active" MEANS; the spine hands over a family-agnostic
        // predicate and gets back a flat id list it feeds, unchanged, into the existing PackMigrationService
        // preview/migrate loop). Both primitives it folds over — the event store and the family runtime — are
        // already registered above.
        services.AddSingleton<IPackMigrationInstanceResolver>(
            serviceProvider => new LoanInstanceFilterResolver(
                serviceProvider.GetRequiredService<IEventStore>(),
                serviceProvider.GetRequiredService<AggregateRuntime<LoanPosition>>(),
                FoldModule.FamilyName));

        // The family OWNS its read-model schema (ADR-PC-021 family-owned ownership): read_model.installment_calendar
        // is a family-NAMED table, so its forward-only migration set lives in this family's Application
        // assembly, not the engine's. The HOST is the composition root that may name a family (ADR-PC-021
        // A2 — the spine still may not), so it is here, not in the spine, that we apply the family migration.
        // This hosted service runs the family MigrationRunner on startup against the migration connection
        // string (DDL privileges, ADR-PC-001 §P3). It assumes the ENGINE schema is already present (the
        // engine host does NOT run the engine migrations today — only deployment machinery + tests do), and
        // the family migration's own fail-loud guard RAISEs if the babelstone_engine role (engine migration
        // 0002) is absent, so an out-of-order run fails clearly rather than corrupting state.
        //
        // Resolution prefers a DEDICATED migration-role connection (the production split: DDL privileges
        // separate from the runtime role, ADR-PC-001 §P3) and falls back to the runtime Engine connection
        // for the dev/test path (one superuser connection for both). It still fails fast if NOTHING resolves,
        // rather than booting against an un-migrated read model (mirroring TermDepositHostModule + the
        // orchestrator's SagaMigrationHostedService).
        var migrationConnectionString =
            ctx.Configuration.GetConnectionString("EngineMigration")
            ?? ctx.Configuration["Engine:MigrationConnectionString"]
            ?? Environment.GetEnvironmentVariable("ENGINE_MIGRATION_CONNECTION_STRING")
            ?? ctx.Configuration.GetConnectionString("Engine");
        services.AddHostedService(_ => new ReadModelMigrationHostedService(migrationConnectionString));
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        LoansEndpoints.Map(app);
        // The operator pack-migration command surface (POST /v1/pack-migrations, ADR-PC-009 §P3 / surface
        // §3.6) is NOT mapped per family: it is registered ONCE at host level (Program.cs) because the route
        // is identical across families — a per-family Map would collide (AmbiguousMatchException) the moment
        // a second family is hosted. The single route dispatches on product_family to this family's registered
        // IPackMigrationService / IPackMigrationInstanceResolver (wired above), so personal_loan IS now
        // pack-migratable in v1.
    }
}

/// <summary>
/// The personal_loan family's CQRS read-model runner, surfaced as an <see cref="IProjectionModule"/> so the
/// term-deposit-registered, family-agnostic projection relay drains it (the relay enumerates EVERY
/// registered module to build its <c>ProjectionRegistry</c>; bd babelstone-6cpq.12). A SEPARATE module from
/// <see cref="PersonalLoanProjectionModule"/> — that one declares the bitemporal projections over the
/// generic <c>projections</c> store; this one declares the single flat read-model runner over the
/// family-owned <c>read_model.installment_calendar</c> table, a distinct surface with a distinct
/// (truncate-and-refold) rebuild discipline. Both carry <see cref="FamilyName"/> <c>personal_loan</c>, which
/// the drainer reads to know whose streams feed the runner; the read-model kind
/// (<see cref="PersonalLoanProjectionModule.InstallmentReadModelKind"/>) is unique, so the registry accepts
/// both. The host injects the family-owned store; the family supplies the fold + the state→row mapper over
/// the generic <see cref="ReadModelInfra{TRow}"/>.
/// </summary>
internal sealed class InstallmentCalendarReadModelModule(IInstallmentCalendarReadModelStore store) : IProjectionModule
{
    private static readonly PersonalLoanProjectionModule Declarations = new();

    public string FamilyName => Declarations.FamilyName;

    public IReadOnlyList<IProjectionRunner> CreateRunners(ProjectionInfra infra) =>
        [Declarations.CreateReadModelRunner(new ReadModelInfra<InstallmentCalendarReadModelRow>(store, infra.EventSerializer))];
}

/// <summary>
/// Applies the personal_loan family's read-model schema on startup (the family OWNS this schema —
/// ADR-PC-021 family-owned ownership; <c>read_model.installment_calendar</c> is a family-named table, so it
/// lives in the family's migration set, not the engine's). A hosted service so the host's lifetime owns it;
/// idempotent — a boot with nothing pending is a no-op (the family <see cref="MigrationRunner"/>'s own
/// <c>schema_migrations_personal_loan</c> ledger guards it). Modelled on the term-deposit family's
/// <c>ReadModelMigrationHostedService</c> and the orchestrator's <c>SagaMigrationHostedService</c>. Refuses
/// to run with no migration connection string rather than booting against an un-migrated read model.
///
/// Engine-before-family ordering: this assumes the engine event-store schema is already present (the engine
/// host runs no engine migrations today; deployment machinery + tests apply them). The family migration's
/// fail-loud SQL guard RAISEs a clear error if the <c>babelstone_engine</c> role (engine migration 0002) is
/// absent, so an out-of-order boot fails loud rather than silently.
/// </summary>
internal sealed class ReadModelMigrationHostedService(string? migrationConnectionString) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(migrationConnectionString))
        {
            throw new InvalidOperationException(
                "No engine migration connection string configured for the personal-loan read-model "
                + "migrations. Set ConnectionStrings:EngineMigration, Engine:MigrationConnectionString, "
                + "or ENGINE_MIGRATION_CONNECTION_STRING.");
        }

        await new MigrationRunner(migrationConnectionString).ApplyAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
