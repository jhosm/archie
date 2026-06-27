using Babelstone.Engine;
using Babelstone.Engine.Hosting;
using Babelstone.EventStore;
using Babelstone.Families.PersonalLoan;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
/// <b>Read model: the bitemporal projections suffice (ADR-PC-002 §P2).</b> Unlike term-deposit (which adds
/// a denormalized <c>read_model.deposits</c> table + its own migration set), the loan needs NO family-owned
/// Postgres read-model table for v1: its two bitemporal projections — the loan position and the amortization
/// schedule (<see cref="PersonalLoanProjectionModule"/>) — materialise into the GENERIC, engine-owned
/// <c>projections</c> table, and the live read path folds the stream through the
/// <see cref="AggregateRuntime{TState}"/>. So this module registers only its
/// <see cref="IProjectionModule"/>; the family-agnostic projection relay term-deposit registers drains it
/// (the relay's registry enumerates EVERY registered <see cref="IProjectionModule"/>), and the engine
/// event-store migrations carry zero personal-loan-named tables.
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

        // The family's projection declarations (two-modes §5.4): the loan position + the amortization
        // schedule, both bitemporal/async into the GENERIC projections table. Registering only the
        // IProjectionModule (not a relay) is deliberate: the family-agnostic projection relay registered by
        // the term-deposit module builds its ProjectionRegistry from EVERY registered IProjectionModule
        // (serviceProvider.GetServices<IProjectionModule>()), so a single relay drains both families'
        // projections — registering a second relay here would double-drain the shared projections/checkpoint
        // tables. The loan needs NO denormalized family read-model table (see the class remarks).
        services.AddSingleton<IProjectionModule, PersonalLoanProjectionModule>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        LoansEndpoints.Map(app);
        // The operator pack-migration command surface (POST /v1/pack-migrations) is registered ONCE at host
        // level (Program.cs) and dispatches on product_family to a family's registered
        // IPackMigrationService; personal_loan does not yet register one, so it is not pack-migratable in v1
        // (a separable follow-up, NOT part of the operability wiring this issue delivers).
    }
}
