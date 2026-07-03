using Babelstone.Engine;
using Babelstone.Engine.Hosting;
using Babelstone.Families.TermDeposit;
using Babelstone.Lifecycle;
using Xunit;

namespace Babelstone.Families.TermDeposit.Lifecycle.Tests;

/// <summary>
/// The dispatch-mapping FITNESS FUNCTION for the term-deposit maturity (ADR-PC-036 §Decision 7 —
/// "share the dispatch mapping with <c>SimulationRuntime</c> so the forecast is a fitness function").
/// In plain terms: the simulation already forecasts a deposit's maturity as a
/// <see cref="LifecycleMilestone"/>, and production fires the same occurrence as a
/// <see cref="LifecycleCommandDecision"/> — if the two ever disagree on WHAT fires (command kind, occurrence
/// key, due instant, canonical dispatch id), the forecast is lying about production. This test builds each
/// artifact the way its REAL consumer builds it — the production decision through the live
/// <see cref="MaturityRule"/> over a read-model row, the forecast milestone the way the A.8b forward
/// schedule builds it — and fails on any divergence.
/// </summary>
public sealed class LifecycleDispatchFitnessTests
{
    // The engine maturity endpoint's own derivation kind (DepositsEndpoints.MatureCommandKind) — the
    // external anchor, quoted as a literal so the convergence assertions are not circular.
    private const string EngineMatureKind = "mature";

    [Fact]
    public async Task The_forecast_maturity_milestone_and_the_production_command_agree_on_the_occurrence()
    {
        var deposit = Guid.NewGuid();
        var maturity = new DateOnly(2027, 3, 15);

        // PRODUCTION: the driver's real one-shot rule over the deposit read model (the A4a path).
        var rule = new MaturityRule(new SingleDepositStore(Deposit(deposit, maturity)));
        var decision = Assert.Single(await rule.EvaluateAsync(maturity));

        // FORECAST: the milestone exactly as the simulation's forward schedule builds it
        // (SimulationForwardLifecycleTests.BuildForwardSchedule → TermDepositLifecycleDispatch).
        var milestone = TermDepositLifecycleDispatch.MaturityMilestone(
            maturity, (_, _) => Task.CompletedTask);

        // The SAME occurrence identity — kind and number-pinned key — on both sides. A divergence here
        // means a forecast milestone would no longer describe what production fires: FAIL.
        Assert.Equal(decision.CommandKind, milestone.CommandKind);
        Assert.Equal(decision.OccurrenceKey, milestone.OccurrenceKey);

        // The SAME due instant: the milestone falls due exactly when the production body says the
        // business valid_time is (matured_at = the maturity date's UTC midnight).
        Assert.Equal(milestone.DueAt, Assert.IsType<DateTimeOffset>(decision.Body["matured_at"]));
        Assert.Equal(new DateTimeOffset(maturity.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), milestone.DueAt);

        // The SAME canonical dispatch id (LCD-1): deriving the number-pinned key from the FORECAST's
        // identity yields byte-for-byte the id production presents to the engine — and both equal the
        // engine's own derivation kind, so all three parties converge on ONE key per occurrence.
        Assert.NotNull(milestone.CommandKind);
        Assert.NotNull(milestone.OccurrenceKey);
        Assert.Equal(
            LifecycleCommandKey.Derive(deposit, milestone.CommandKind, milestone.OccurrenceKey.Value),
            LifecycleDispatchId.Of(decision));
        Assert.Equal(EngineMatureKind, milestone.CommandKind);
    }

    // --- helpers ---

    private static DepositReadModelRow Deposit(Guid id, DateOnly maturity) =>
        new(
            StreamId: id,
            Sor: "engine",
            PrincipalCents: 0,
            TanBasisPoints: 0,
            RateSheetVersionId: string.Empty,
            ProductCode: string.Empty,
            TermDays: 0,
            StartDate: maturity.AddDays(-365),
            MaturityDate: maturity,
            InterestVariant: string.Empty,
            AutoRenewalPolicy: "NONE",
            PaymentPeriodMonths: 0,
            Lifecycle: nameof(DepositLifecycle.Active),
            AccruedGrossInterestCents: 0,
            WithholdingToDateCents: 0,
            NetInterestCents: 0,
            TotalPayoutCents: 0,
            CouponsPaid: 0,
            Detail: ReadOnlyMemory<byte>.Empty,
            LastSequence: 1,
            LastUpdated: default);

    private sealed class SingleDepositStore(params DepositReadModelRow[] rows) : IDepositReadModelStore
    {
        public Task<IReadOnlyList<DepositReadModelRow>> ListByMaturityAsync(
            DateOnly fromInclusive, DateOnly toExclusive, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DepositReadModelRow>>(
                rows.Where(r => r.MaturityDate >= fromInclusive && r.MaturityDate < toExclusive).ToList());

        public Task<IReadOnlyList<DepositReadModelRow>> ListWithWithholdingAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ListActiveStreamIdsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpsertAsync(DepositReadModelRow row, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DepositReadModelRow?> GetAsync(Guid streamId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task TruncateAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
