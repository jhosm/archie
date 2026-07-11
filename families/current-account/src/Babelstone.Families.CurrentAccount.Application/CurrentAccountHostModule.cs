using Babelstone.Engine;
using Babelstone.Engine.Hosting;
using Babelstone.EventStore;
using Babelstone.Families.CurrentAccount;
using Babelstone.RateSheets;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Babelstone.Families.CurrentAccount.Application;

/// <summary>
/// The current_account (conta à ordem) family's host composition (ADR-PC-021 / ADR-PC-037), realized as
/// an <see cref="IFamilyHostModule"/> — the transactional-account sibling of <c>TermDepositHostModule</c>
/// and <c>PersonalLoanHostModule</c>. It owns everything current-account-specific: the closed-generic
/// <see cref="AggregateRuntime{TState}"/> over <see cref="AccountPosition"/> (seeded
/// <c>() =&gt; AccountPosition.Empty</c>, fed the family's fold registry) and the
/// <see cref="CurrentAccountLifecycleService"/> decider behind the <c>/v1/accounts</c> command / query
/// surface (<see cref="AccountsEndpoints"/>).
/// <para>
/// <b>Discovery, not a host edit (ADR-PC-037 / ADR-PC-021).</b> The host's <c>HostModuleLoader</c>
/// discovers this module by assembly-scan over the <c>Babelstone.Families.*.dll</c> in its output dir
/// (where the host's <c>ProjectReference</c> copies this assembly); the host names no current-account
/// type in code, so <c>ENGINE_API_HOST_FAMILY_AGNOSTIC</c> stays green. The pinned pack's family-manifest
/// (<c>families.yaml</c>) pins <c>current_account@2026.1</c>, so
/// <c>HostModuleLoader.CrossCheckAgainstPackManifest</c> passes fail-closed (ADR-PC-009).
/// </para>
/// <para>
/// <b>No family read model.</b> Unlike the deposit / loan hosts, this module registers no read-model
/// store, no <see cref="IProjectionModule"/>, and no read-model migration: a demand account's accounting
/// and available balances and its active-hold set are SPINE-owned folds
/// (<c>AccountBalanceReader</c> + <c>AccountHoldProjector</c>, keyed by the opaque <c>account_ref</c>),
/// not a family-named table (ACCOUNT_BALANCE_IS_A_FOLD, ADR-PC-033). The account read endpoint composes
/// the folded lifecycle position with those spine reads, so there is no denormalized
/// <c>read_model.accounts</c> surface to own here.
/// </para>
/// </summary>
public sealed class CurrentAccountHostModule : IFamilyHostModule
{
    // The single source of truth for this family's identity is its fold module — the SAME
    // FamilyName/SchemaVersion that stamps every EventEnvelope (ADR-PC-009), so the load-time
    // family-manifest cross-check compares the pinned pack against the value that actually rides on events.
    private static readonly CurrentAccountFamilyModule FoldModule = new();

    public string FamilyName => FoldModule.FamilyName;

    public string SchemaVersion => FoldModule.SchemaVersion;

    // aggregate_type == family_name by the engine's documented convention (ADR-IC-004).
    public string AggregateType => FoldModule.FamilyName;

    public void ConfigureServices(IServiceCollection services, FamilyHostContext ctx)
    {
        // The durable runtime over the current_account family. The family owns this closed generic, so the
        // host never names AccountPosition. Shared infrastructure (store, sink, codec, PII protector,
        // clock, integration-event catalog, bus serializer, snapshot storage) is resolved from the
        // container; only the fold registry + seed are ours. Mirrors PersonalLoanHostModule over
        // AccountPosition.
        services.AddSingleton(serviceProvider => new AggregateRuntime<AccountPosition>(
            serviceProvider.GetRequiredService<IEventStore>(),
            serviceProvider.GetRequiredService<IEventSink>(),
            CurrentAccountFamilyModule.Registry(),
            // The STORE codec (ADR-PC-028): self-describing JSON fills events.payload (the book of record)
            // and is the sole decode/replay path.
            serviceProvider.GetRequiredService<IEventSerializer>(),
            serviceProvider.GetRequiredService<IPiiProtector>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            () => AccountPosition.Empty,
            // The catalog-gated relay (ADR-IC-017): the runtime writes a publishable outbox row ONLY for a
            // catalogued integration event; an uncatalogued event is store-only by construction. Adding a
            // family event's .avsc to the catalog is what promotes it to the bus — no host code change. So
            // the four catalogued account lifecycle events (opened / dormant / reactivated / closed)
            // publish, and the store-only AccountOpeningFailed does not.
            integrationEventCatalog: serviceProvider.GetRequiredService<IIntegrationEventCatalog>(),
            // The BUS codec (ADR-PC-028 dual-encode): null in JSON mode (the outbox reuses the JSON store
            // codec), the real Avro+schema_id serializer when Bus:Encoding=avro is registered.
            busSerializer: serviceProvider.GetService<BusEventSerializer>()?.Inner,
            // Snapshot wiring (ADR-PC-003): the typed SnapshotStore<AccountPosition> composes the
            // family-agnostic spine snapshot storage with the family's structural JSON state codec — the
            // SAME codec a projection row uses, so a snapshot serialises deterministically with no PII
            // (ADR-PC-004).
            snapshots: new SnapshotStore<AccountPosition>(
                serviceProvider.GetRequiredService<ISnapshotStorage>(),
                new JsonStateSerializer<AccountPosition>()),
            // The v1 cadence: per-N (config Engine:SnapshotEveryNEvents, default 100) ORed with the
            // lifecycle-boundary flag the family's AccountOpened / AccountClosed events supply
            // (IsLifecycleBoundary) and the calendar flag the runtime computes below.
            snapshotPolicy: new CountBasedSnapshotPolicy(
                ctx.Configuration.GetValue("Engine:SnapshotEveryNEvents", 100L)),
            // The calendar-boundary trigger (ADR-PC-003): a snapshot at month-/year-end so as-of queries at
            // reporting boundaries return without a long replay. Config Engine:SnapshotCalendarGranularity
            // (None/Month/Year; default Month).
            calendarBoundaryPolicy: new CalendarBoundaryPolicy(
                ctx.Configuration.GetValue(
                    "Engine:SnapshotCalendarGranularity", CalendarGranularity.Month)),
            // Fail-soft sink for a post-commit snapshot-write failure (ADR-PC-003): the append already
            // committed and IS the book of record, so a snapshot blip must not fail the command — it is
            // logged and the next rebuild is merely slower, never wrong.
            onSnapshotError: ex => serviceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Babelstone.Families.CurrentAccount.Snapshots")
                .LogWarning(ex, "Post-commit snapshot write failed; the committed event is unaffected, the next rebuild is slower not wrong.")));

        // The current_account LIFECYCLE decider's orchestration (ADR-PC-021): this module is its
        // composition root. It depends only on the family runtime and the pinned pack (no rate sheet — a
        // demand account's lifecycle carries no priced rate).
        services.AddSingleton(serviceProvider => new CurrentAccountLifecycleService(
            serviceProvider.GetRequiredService<AggregateRuntime<AccountPosition>>(),
            ctx.Pack));

        // The family's OWN product-config store (ADR-PC-037; see CurrentAccountProductConfigStore for
        // why it is family-owned, not the deposit spine store): reads product-configs/current-account/ at
        // startup and resolves product_code → arranged overdraft / per-transaction cap for the authorize
        // decider's stage-4 rules. Engine:CurrentAccountConfigsDir overrides the default walk-up discovery
        // (dev/test boots with none set).
        services.AddSingleton(_ => new CurrentAccountProductConfigStore(
            ctx.Configuration["Engine:CurrentAccountConfigsDir"]));

        // The synchronous AUTHORIZE decider's orchestration (ADR-PC-034 technique / ADR-PC-037) — a
        // SEPARATE service on the payment hot path (lifecycle relabels state, authorize earmarks funds). It
        // composes the family runtime with the spine-owned, host-level singletons the decision reads: the
        // available-balance + freeze readers, the projection drainer it drains before deciding
        // (read-your-writes), the store codec + PII protector it uses to reconstruct a replayed verdict off
        // the single appended event, and the family's own product-config store the stage-4 overdraft/limit
        // rules resolve from. All are family→engine dependencies — the host still names no current-account
        // type (ENGINE_API_HOST_FAMILY_AGNOSTIC).
        services.AddSingleton(serviceProvider => new CurrentAccountAuthorizeService(
            serviceProvider.GetRequiredService<AggregateRuntime<AccountPosition>>(),
            serviceProvider.GetRequiredService<AccountBalanceReader>(),
            serviceProvider.GetRequiredService<AccountFreezeReader>(),
            serviceProvider.GetRequiredService<SpineProjectionDrainer>(),
            ctx.Pack,
            serviceProvider.GetRequiredService<IEventStore>(),
            serviceProvider.GetRequiredService<IEventSerializer>(),
            serviceProvider.GetRequiredService<IPiiProtector>(),
            serviceProvider.GetRequiredService<CurrentAccountProductConfigStore>()));

        // The projection-derived HOLD-EXPIRY command shell (ADR-PC-037): the /holds/{holdId}/expire
        // target the ADR-PC-036 lifecycle-command driver POSTs to. A THIRD service, distinct from both the
        // lifecycle state machine (relabels state) and the authorize money-mover (places holds): it only
        // releases a hold, moving no money. It composes just the family runtime and the pinned pack — the
        // append is a plain spine fact with no balance read or decider (holds are spine-owned, ADR-PC-033).
        services.AddSingleton(serviceProvider => new CurrentAccountHoldExpiryService(
            serviceProvider.GetRequiredService<AggregateRuntime<AccountPosition>>(),
            ctx.Pack));

        // The projection-derived OVERDRAFT-ACCRUAL command shell (ADR-PC-037): the /overdraft/accrue target
        // the ADR-PC-036 lifecycle-command driver POSTs to for an account it found drawn below zero. A FOURTH
        // service — it posts a fee Movement against the drawn balance, so unlike hold-expiry it reads the
        // balance and resolves a rate: it composes the family runtime, the spine balance reader, the family's
        // own product-config store (the overdraft rate REF), the rate-sheet store (the numeric TAN, ADR-PC-008),
        // and the pinned pack. All are family→engine dependencies — the host still names no current-account type
        // (ENGINE_API_HOST_FAMILY_AGNOSTIC). The fee math is command-side in the pure decider (ADR-PC-037).
        services.AddSingleton(serviceProvider => new CurrentAccountOverdraftAccrualService(
            serviceProvider.GetRequiredService<AggregateRuntime<AccountPosition>>(),
            serviceProvider.GetRequiredService<AccountBalanceReader>(),
            serviceProvider.GetRequiredService<CurrentAccountProductConfigStore>(),
            serviceProvider.GetRequiredService<IRateSheetStore>(),
            ctx.Pack));

        // The settlement CREDIT-receive command shell (ADR-PC-043): the /credit
        // target the substrate SettlementProcess saga POSTs to. The family's FIRST money-IN service — it loads
        // the account's own stream, runs the pure CurrentAccountCreditAdmissionDecider, and appends
        // AccountCredited at the loaded version under per-stream OCC (a Closed/Erased account is refused by
        // construction). It composes just the family runtime + the pinned pack (admission is a pure decision on
        // the folded lifecycle; no balance read needed). A family→engine dependency (ENGINE_API_HOST_FAMILY_AGNOSTIC).
        services.AddSingleton(serviceProvider => new CurrentAccountCreditReceiveService(
            serviceProvider.GetRequiredService<AggregateRuntime<AccountPosition>>(),
            ctx.Pack));

        // The settlement CAPTURE command shell (ADR-PC-043 / ADR-PC-037): the /capture
        // target the substrate SettlementProcess saga POSTs to. It turns an authorize reservation into a real
        // debit — appending the spine HoldCaptured + the family AccountDebited in one atomic batch — so unlike
        // the credit service it reads the active-hold set (to enforce authorize.hold_id == capture.target and
        // to surface a partial/over-capture) and drains the spine projection first (read-your-writes). It
        // composes the family runtime, the spine balance/hold reader, the projection drainer, and the pinned
        // pack — all family→engine dependencies (the host still names no current-account type).
        services.AddSingleton(serviceProvider => new CurrentAccountCaptureService(
            serviceProvider.GetRequiredService<AggregateRuntime<AccountPosition>>(),
            serviceProvider.GetRequiredService<AccountBalanceReader>(),
            serviceProvider.GetRequiredService<SpineProjectionDrainer>(),
            ctx.Pack));
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        AccountsEndpoints.Map(app);
        // The engine-CA SETTLEMENT INGRESS (bd babelstone-u79p.5; ADR-PC-043): the three counterparty-invariant
        // settlement paths (/v1/reservations, /v1/debits, /v1/credits) the orchestrator's settlement dispatcher
        // POSTs to when a leg is ce_settlementtarget=engine-ca. The adapter maps them onto this family's
        // authorize/capture/credit writers for the account the leg's account_ref names — mTLS-only internal
        // ingress, never a public Kong route.
        SettlementIngressEndpoints.Map(app);
        // The operator pack-migration command surface (POST /v1/pack-migrations, ADR-PC-009) is NOT mapped
        // per family: it is registered ONCE at host level because the route is identical across families —
        // a per-family Map would collide (AmbiguousMatchException) the moment a second family is hosted.
    }
}
