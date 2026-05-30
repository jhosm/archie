using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Babelstone.RateSheets;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// The E.3 walking-skeleton happy path end-to-end against real PostgreSQL (Testcontainers):
/// command → decider → kernel → rate-sheet + pack resolution → settlement → append+outbox,
/// then a durable rehydrate that folds the canonical AT_MATURITY position. Tagged Integration,
/// so it runs in the Testcontainers lane (wired in E.6), not the default unit lane.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ConstituteAccrueMatureHappyPathTests(ConstitutionFixture fixture)
    : IClassFixture<ConstitutionFixture>
{
    [Fact]
    public async Task Constitute_then_mature_appends_events_and_outbox_and_folds_the_canonical_position()
    {
        var connectionString = fixture.ConnectionString;

        // Deploy a rate sheet pricing (dpz_pt_12m_juros_venc, standard) at 300 bps, effective before constitution.
        var rateSheetStore = new PostgresRateSheetStore(connectionString);
        await rateSheetStore.InsertAsync(TestRateSheets.FlatPriced(
            versionId: "pt-deposits-2026.1", productId: "dpz_pt_12m_juros_venc", role: "standard",
            tanBasisPoints: 300, effectiveFrom: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        // Compose the durable runtime over the term-deposit family + the in-memory settlement stub
        // (the test is E.3's composition root, ADR-PC-021 §D4).
        var store = new PostgresEventStore(connectionString);
        var runtime = new AggregateRuntime<DepositPosition>(
            store, new EventStoreSink(store), TermDepositFamilyModule.Registry(),
            new JsonEventSerializer(), new NullPiiProtector(), TimeProvider.System,
            () => DepositPosition.Empty);
        var settlement = new RecordingSettlementPort();
        var service = new TermDepositConstitutionService(
            runtime, rateSheetStore, settlement, SkeletonPack.LoadPt2026(),
            dayCountPrimitive: "act_360", withholdingPrimitive: "irs_juros");

        var depositId = Guid.NewGuid();

        await service.ConstituteAsync(new ConstituteDepositCommand(
            DepositId: depositId, PrincipalCents: 1_000_000, ProductId: "dpz_pt_12m_juros_venc", Role: "standard",
            TermDays: 365, StartDate: new DateOnly(2026, 1, 15),
            ConstitutedAt: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
            InterestVariant: "AT_MATURITY", AutoRenewalPolicy: "NONE", FundingAccount: "PT50-DDA-001", Actor: "mcp:dev"));

        await service.MatureAsync(new MatureDepositCommand(
            DepositId: depositId, MaturedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero),
            PayoutAccount: "PT50-DDA-001", Actor: "mcp:dev"));

        // The durable projection (rehydrated from the log) folds to the canonical numbers.
        var hydrated = await runtime.LoadAsync(depositId);
        var position = hydrated.State;
        Assert.Equal(3, hydrated.Version); // four events, sequence 0..3
        Assert.Equal(depositId, position.DepositId);
        Assert.Equal(new Money(1_000_000), position.Principal);
        Assert.Equal(300, position.TanBasisPoints);
        Assert.Equal("pt-deposits-2026.1", position.RateSheetVersionId);
        Assert.Equal(new Money(30_417), position.AccruedGrossInterest);
        Assert.Equal(new Money(8_517), position.WithholdingToDate);
        Assert.Equal(new Money(21_900), position.NetInterest);
        Assert.Equal(new Money(1_021_900), position.TotalPayout);
        Assert.Equal(DepositLifecycle.Matured, position.Lifecycle);

        // Every event got its paired outbox row (the ES_ATOMIC_APPEND_OUTBOX pairing E.4 publishes).
        Assert.Equal(4, await fixture.CountAsync("events", "stream_id", depositId));
        Assert.Equal(4, await fixture.CountAsync("outbox", "aggregate_id", depositId));

        // The legacy-settlement legs: debit the principal at constitution, credit the payout at maturity.
        Assert.Collection(
            settlement.Instructions,
            debit =>
            {
                Assert.Equal(SettlementDirection.Debit, debit.Direction);
                Assert.Equal(new Money(1_000_000), debit.Amount);
                Assert.Equal("constitution", debit.Reason);
            },
            credit =>
            {
                Assert.Equal(SettlementDirection.Credit, credit.Direction);
                Assert.Equal(new Money(1_021_900), credit.Amount);
                Assert.Equal("maturity", credit.Reason);
            });
    }
}
