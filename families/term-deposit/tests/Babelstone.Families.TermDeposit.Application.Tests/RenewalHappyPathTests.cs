using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Babelstone.RateSheets;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// F.5 (babelstone-k4yr) auto-renewal end-to-end against real PostgreSQL (Testcontainers): a
/// constituted Active deposit renews into a fresh engine-native instance, emitting
/// <c>DepositMatured</c> → <c>DepositConstituted</c> (new stream) → <c>DepositRenewed</c> in that
/// order across two streams, with the new constitution's <c>causation_id</c> rooted at the closing
/// <c>DepositMatured</c> (02 §2.4.4). Tagged Integration — the Testcontainers lane. The pure
/// policy/rate/link decisions are unit-tested in <c>TermDepositDeciderTests</c>; the guards in
/// <c>RenewalRejectionTests</c>. This class gets its OWN container (IClassFixture instance per class),
/// so it can deploy a later rate sheet without shadowing the other Integration tests' sheets.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RenewalHappyPathTests(ConstitutionFixture fixture)
    : IClassFixture<ConstitutionFixture>
{
    private const string Product = "dpz_pt_12m_juros_venc";

    [Fact]
    public async Task SameTermCurrentRate_matures_constitutes_new_at_current_rate_and_links_in_order()
    {
        // Two sheets effective for the family: the original at 300bps (before constitution) and a later
        // one at 275bps effective at the renewal moment. CURRENT_RATE re-resolves the later sheet, so the
        // renewed instance prices 275bps off pt-deposits-2027.1, not the closing deposit's 300bps.
        await fixture.EnsureRateSheetAsync(SheetAt("pt-deposits-2026.1", new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), 300));
        await fixture.EnsureRateSheetAsync(SheetAt("pt-deposits-2027.1", new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), 275));

        var (runtime, service, settlement) = Compose(fixture.ConnectionString);
        var depositId = Guid.NewGuid();
        var newDepositId = Guid.NewGuid();

        await service.ConstituteAsync(new ConstituteDepositCommand(
            DepositId: depositId, PrincipalCents: 1_000_000, ProductId: Product, Role: "standard",
            TermDays: 365, StartDate: new DateOnly(2026, 1, 15),
            ConstitutedAt: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
            InterestVariant: "AT_MATURITY", AutoRenewalPolicy: "SAME_TERM_CURRENT_RATE",
            FundingAccount: "PT50-DDA-001", Actor: "mcp:dev"));

        await service.RenewAsync(new RenewDepositCommand(
            DepositId: depositId, ProductId: Product, Role: "standard",
            RenewedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero),
            NewDepositId: newDepositId, PayoutAccount: "PT50-DDA-001", FundingAccount: "PT50-DDA-001", Actor: "mcp:dev"));

        // The closing deposit: Active → Renewed (terminal), with its full AT_MATURITY maturity folded.
        var closing = (await runtime.LoadAsync(depositId)).State;
        Assert.Equal(DepositLifecycle.Renewed, closing.Lifecycle);
        Assert.Equal(new Money(1_021_900), closing.TotalPayout); // principal + net of the canonical flow

        // The renewed instance: a fresh Active deposit rolling the principal at the CURRENT 275bps rate
        // resolved off the later sheet, same 365-day term, new start = renewal date.
        var renewed = (await runtime.LoadAsync(newDepositId)).State;
        Assert.Equal(DepositLifecycle.Active, renewed.Lifecycle);
        Assert.Equal(newDepositId, renewed.DepositId);
        Assert.Equal(new Money(1_000_000), renewed.Principal);      // rolled-over principal
        Assert.Equal(275, renewed.TanBasisPoints);                  // the bank's then-current standard rate
        Assert.Equal("pt-deposits-2027.1", renewed.RateSheetVersionId);
        Assert.Equal(365, renewed.TermDays);
        Assert.Equal(new DateOnly(2027, 1, 15), renewed.StartDate); // new start = renewal date
        Assert.Equal(new DateOnly(2028, 1, 15), renewed.MaturityDate); // 2027-01-15 + 365d (2027 is not a leap year)
        Assert.Equal("SAME_TERM_CURRENT_RATE", renewed.AutoRenewalPolicy);

        // Event order (02 §2.4.4): closing stream = Constituted, Accrued, Withheld, Matured, Renewed (5);
        // the new stream = the single Constituted (1). DepositMatured precedes DepositConstituted precedes
        // DepositRenewed across the two streams.
        Assert.Equal(5, await fixture.CountAsync("events", "stream_id", depositId));
        Assert.Equal(1, await fixture.CountAsync("events", "stream_id", newDepositId));
        Assert.Equal(5, await fixture.CountAsync("outbox", "aggregate_id", depositId));
        Assert.Equal(1, await fixture.CountAsync("outbox", "aggregate_id", newDepositId));

        // The causation link (02 §2.4.4 step 2): the new instance's DepositConstituted (sequence 0)
        // roots at the closing DepositMatured's event id.
        var maturedEventId = await fixture.EventIdAsync(depositId, "term_deposit.DepositMatured");
        var newConstitutionCausation = await fixture.FirstEventCausationIdAsync(newDepositId);
        Assert.Equal(maturedEventId, newConstitutionCausation);

        // Settlement legs (bd babelstone-t7o3.4): the standalone CONSTITUTION path is de-settled — its
        // principal debit is now the saga's gated step (ADR-PC-016 §68/§127), so it no longer leads the
        // sequence. The RENEWAL path keeps its eager legs for now (its own saga has not landed): the
        // closing maturity credit (principal+net out) and the rollover debit (the rolled-over principal
        // back into the new instance).
        Assert.DoesNotContain(settlement.Instructions, i => i.Reason == "constitution");
        Assert.Collection(
            settlement.Instructions,
            maturity =>
            {
                Assert.Equal(SettlementDirection.Credit, maturity.Direction);
                Assert.Equal(new Money(1_021_900), maturity.Amount);
                Assert.Equal("maturity", maturity.Reason);
            },
            rollover =>
            {
                Assert.Equal(SettlementDirection.Debit, rollover.Direction);
                Assert.Equal(new Money(1_000_000), rollover.Amount);
                Assert.Equal("renewal_rollover", rollover.Reason);
            });
    }

    [Fact]
    public async Task SameTermSameRate_carries_the_original_rate_forward_ignoring_the_current_sheet()
    {
        // SAME_RATE renews at the ORIGINAL rate. Even with a later 275bps sheet effective at renewal, the
        // renewed instance carries the closing deposit's original 300bps / original version — no re-resolution.
        await fixture.EnsureRateSheetAsync(SheetAt("pt-deposits-2026.1", new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), 300));
        await fixture.EnsureRateSheetAsync(SheetAt("pt-deposits-2027.1", new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), 275));

        var (runtime, service, _) = Compose(fixture.ConnectionString);
        var depositId = Guid.NewGuid();
        var newDepositId = Guid.NewGuid();

        await service.ConstituteAsync(new ConstituteDepositCommand(
            DepositId: depositId, PrincipalCents: 1_000_000, ProductId: Product, Role: "standard",
            TermDays: 365, StartDate: new DateOnly(2026, 1, 15),
            ConstitutedAt: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
            InterestVariant: "AT_MATURITY", AutoRenewalPolicy: "SAME_TERM_SAME_RATE",
            FundingAccount: "PT50-DDA-001", Actor: "mcp:dev"));

        await service.RenewAsync(new RenewDepositCommand(
            DepositId: depositId, ProductId: Product, Role: "standard",
            RenewedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero),
            NewDepositId: newDepositId, PayoutAccount: "PT50-DDA-001", FundingAccount: "PT50-DDA-001", Actor: "mcp:dev"));

        var renewed = (await runtime.LoadAsync(newDepositId)).State;
        Assert.Equal(300, renewed.TanBasisPoints);                  // the ORIGINAL rate, not the current 275
        Assert.Equal("pt-deposits-2026.1", renewed.RateSheetVersionId); // the original version
        Assert.Equal("SAME_TERM_SAME_RATE", renewed.AutoRenewalPolicy);
        Assert.Equal(DepositLifecycle.Active, renewed.Lifecycle);

        // The DepositRenewed link folds the closing deposit terminal.
        Assert.Equal(DepositLifecycle.Renewed, (await runtime.LoadAsync(depositId)).State.Lifecycle);
    }

    private static RateSheet SheetAt(string versionId, DateTimeOffset effectiveFrom, int tanBasisPoints) =>
        TestRateSheets.FlatPriced(versionId, Product, "standard", tanBasisPoints, effectiveFrom);

    private static (AggregateRuntime<DepositPosition> Runtime, TermDepositConstitutionService Service, RecordingSettlementPort Settlement)
        Compose(string connectionString)
    {
        var store = new PostgresEventStore(connectionString);
        var runtime = new AggregateRuntime<DepositPosition>(
            store, new EventStoreSink(store), TermDepositFamilyModule.Registry(),
            new JsonEventSerializer(), new NullPiiProtector(), TimeProvider.System,
            () => DepositPosition.Empty);
        var settlement = new RecordingSettlementPort();
        var service = new TermDepositConstitutionService(
            runtime, new PostgresRateSheetStore(connectionString), settlement, SkeletonPack.LoadPt2026(),
            dayCountPrimitive: "act_360", withholdingPrimitive: "irs_juros");
        return (runtime, service, settlement);
    }
}
