using Babelstone.EventStore;
using Babelstone.Families.TermDeposit;
using Babelstone.Families.TermDeposit.Application;
using Babelstone.RateSheets;

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
            () => DepositPosition.Empty));

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
            // supplies the fold + the state→row mapper over the generic ReadModelInfra<TRow>.
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
    }

    public void MapEndpoints(IEndpointRouteBuilder app) => DepositsEndpoints.Map(app);
}
