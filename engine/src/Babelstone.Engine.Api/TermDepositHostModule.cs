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
            withholdingPrimitive: "irs_juros"));
    }

    public void MapEndpoints(IEndpointRouteBuilder app) => DepositsEndpoints.Map(app);
}
