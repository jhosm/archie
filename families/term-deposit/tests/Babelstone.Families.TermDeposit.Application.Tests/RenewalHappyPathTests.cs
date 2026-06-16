using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Babelstone.RateSheets;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// Renewal-saga end-to-end against real PostgreSQL (Testcontainers), decomposed per bd babelstone-mtto
/// PR B. The retired monolithic <c>RenewAsync</c> did mature + constitute + link in one un-idempotent
/// cross-stream call; the saga now drives the SAME postconditions through the autonomous maturity leg
/// followed by two idempotent engine operations:
/// <list type="number">
/// <item>constitute an Active deposit (<c>ConstituteAsync</c>),</item>
/// <item>mature it autonomously (<c>MatureAsync</c>) — this is the maturity leg the monolith folded in,</item>
/// <item>open the renewed instance (<c>ConstituteRenewalAsync</c>), and</item>
/// <item>link the two (<c>LinkRenewalAsync</c>).</item>
/// </list>
/// The closing stream still folds to Renewed (terminal); the new stream is Active at the policy-resolved
/// rate; the <c>DepositConstituted</c> → <c>DepositMatured</c> → <c>DepositRenewed</c> order and the
/// causation root at the closing <c>DepositMatured</c> are unchanged. The ONLY behavioural change is that
/// the settlement legs now SPLIT across two calls — the maturity credit from <c>MatureAsync</c>, the
/// rollover debit from <c>ConstituteRenewalAsync</c>. The financial math is byte-identical to the
/// monolith (same pure deciders: <c>ResolveRenewalRate</c> / <c>DecideRenewalConstitution</c> /
/// <c>DecideRenewalLink</c> / <c>DecideAdvance</c>). Tagged Integration — the Testcontainers lane; this
/// class gets its OWN container so it can deploy a later rate sheet without shadowing other tests' sheets.
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

        await ConstituteActiveAsync(service, depositId, "AT_MATURITY", "SAME_TERM_CURRENT_RATE");

        // Step 1 (autonomous): mature the closing deposit. This is the maturity leg the monolith folded
        // into RenewAsync; it now runs FIRST and independently, leaving the closing stream Matured.
        await MatureAsync(service, depositId);

        // Step 2: open the renewed instance off the Matured closing deposit.
        await service.ConstituteRenewalAsync(new ConstituteRenewalCommand(
            DepositId: depositId, NewDepositId: newDepositId, ProductId: Product, Role: "standard",
            RenewedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero),
            FundingAccount: "PT50-DDA-001", Actor: "saga:renewal", CommandId: Guid.NewGuid()));

        // Step 3: link the renewal, folding the closing stream Matured → Renewed (terminal).
        await service.LinkRenewalAsync(new LinkRenewalCommand(
            DepositId: depositId, NewDepositId: newDepositId,
            RenewedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero),
            Actor: "saga:renewal", CommandId: Guid.NewGuid()));

        // The closing deposit: Active → Matured → Renewed (terminal), with its full AT_MATURITY maturity folded.
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
        // roots at the closing DepositMatured's event id — unchanged from the monolith.
        var maturedEventId = await fixture.EventIdAsync(depositId, "term_deposit.DepositMatured");
        var newConstitutionCausation = await fixture.FirstEventCausationIdAsync(newDepositId);
        Assert.Equal(maturedEventId, newConstitutionCausation);

        // Settlement legs now SPLIT across the two calls (the behavioural change). MatureAsync produces
        // the closing maturity credit (principal + net out); ConstituteRenewalAsync produces the rollover
        // debit (the rolled-over principal back into the new instance). The standalone CONSTITUTION path
        // is de-settled (bd babelstone-t7o3.4), so there is no "constitution" leg.
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

        await ConstituteActiveAsync(service, depositId, "AT_MATURITY", "SAME_TERM_SAME_RATE");
        await MatureAsync(service, depositId);

        await service.ConstituteRenewalAsync(new ConstituteRenewalCommand(
            DepositId: depositId, NewDepositId: newDepositId, ProductId: Product, Role: "standard",
            RenewedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero),
            FundingAccount: "PT50-DDA-001", Actor: "saga:renewal", CommandId: Guid.NewGuid()));

        await service.LinkRenewalAsync(new LinkRenewalCommand(
            DepositId: depositId, NewDepositId: newDepositId,
            RenewedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero),
            Actor: "saga:renewal", CommandId: Guid.NewGuid()));

        var renewed = (await runtime.LoadAsync(newDepositId)).State;
        Assert.Equal(300, renewed.TanBasisPoints);                  // the ORIGINAL rate, not the current 275
        Assert.Equal("pt-deposits-2026.1", renewed.RateSheetVersionId); // the original version
        Assert.Equal("SAME_TERM_SAME_RATE", renewed.AutoRenewalPolicy);
        Assert.Equal(DepositLifecycle.Active, renewed.Lifecycle);

        // The DepositRenewed link folds the closing deposit terminal.
        Assert.Equal(DepositLifecycle.Renewed, (await runtime.LoadAsync(depositId)).State.Lifecycle);
    }

    [Fact]
    public async Task Advance_renewal_pays_the_upfront_interest_on_the_new_stream()
    {
        // The ADVANCE variant recognises full-term interest at t=0 (02 §2.1 CF(0) = -C + J). The retired
        // monolith's step 8b appended the upfront InterestPaid triple onto the NEW stream and settled the
        // advance interest; ConstituteRenewalAsync preserves that exactly (DecideAdvance verbatim).
        await fixture.EnsureRateSheetAsync(SheetAt("pt-deposits-2026.1", new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), 300));
        await fixture.EnsureRateSheetAsync(SheetAt("pt-deposits-2027.1", new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), 275));

        var (runtime, service, settlement) = Compose(fixture.ConnectionString);
        var depositId = Guid.NewGuid();
        var newDepositId = Guid.NewGuid();

        // An ADVANCE deposit pays interest up front at constitution, so its t=0 settlement also runs.
        await ConstituteActiveAsync(service, depositId, "ADVANCE", "SAME_TERM_SAME_RATE");
        await MatureAsync(service, depositId);

        await service.ConstituteRenewalAsync(new ConstituteRenewalCommand(
            DepositId: depositId, NewDepositId: newDepositId, ProductId: Product, Role: "standard",
            RenewedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero),
            FundingAccount: "PT50-DDA-001", Actor: "saga:renewal", CommandId: Guid.NewGuid()));

        await service.LinkRenewalAsync(new LinkRenewalCommand(
            DepositId: depositId, NewDepositId: newDepositId,
            RenewedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero),
            Actor: "saga:renewal", CommandId: Guid.NewGuid()));

        var renewed = (await runtime.LoadAsync(newDepositId)).State;
        Assert.Equal(DepositLifecycle.Active, renewed.Lifecycle);
        Assert.Equal("ADVANCE", renewed.InterestVariant);

        // The new ADVANCE stream carries the upfront interest (DepositConstituted + the single
        // InterestPaid DecideAdvance returns = 2 events), so the upfront interest IS recognised on the new
        // stream — byte-identical to the monolith (its new-stream events were [renewed] + DecideAdvance).
        // The closing stream folds Renewed terminal.
        Assert.Equal(2, await fixture.CountAsync("events", "stream_id", newDepositId));
        Assert.Equal(DepositLifecycle.Renewed, (await runtime.LoadAsync(depositId)).State.Lifecycle);
        Assert.True(renewed.NetInterest.Cents > 0); // upfront interest recognised on the new stream

        // The advance-interest credit settles on the new stream (monolith step 8b), alongside the
        // closing maturity credit and the rollover debit.
        Assert.Contains(settlement.Instructions, i => i.Reason == "advance_interest" && i.Direction == SettlementDirection.Credit);
        Assert.Contains(settlement.Instructions, i => i.Reason == "renewal_rollover" && i.Direction == SettlementDirection.Debit);
    }

    // NOTE: command-id idempotency (ADR-PC-029 slot 4) is exercised at the ENDPOINT level, where the
    // ICommandLog.TryGetAsync pre-check + DuplicateCommandException scaffold lives (mirroring
    // ConstituteAsync) — see ENGINE_COMMAND_IDEMPOTENT_constitute_renewal_replay_* and
    // ENGINE_COMMAND_IDEMPOTENT_renewal_link_replay_* in DepositsApiIntegrationTests. The service method
    // alone does NOT short-circuit a sequential replay (it would ConcurrencyException on the second
    // -1 append / reject the now-Renewed closing deposit) — the endpoint pre-check is the idempotency seam.

    // ---- helpers --------------------------------------------------------------------------------

    private static Task ConstituteActiveAsync(
        TermDepositConstitutionService service, Guid depositId, string variant, string policy) =>
        service.ConstituteAsync(new ConstituteDepositCommand(
            DepositId: depositId, PrincipalCents: 1_000_000, ProductId: Product, Role: "standard",
            TermDays: 365, StartDate: new DateOnly(2026, 1, 15),
            ConstitutedAt: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
            InterestVariant: variant, AutoRenewalPolicy: policy,
            FundingAccount: "PT50-DDA-001", Actor: "mcp:dev", CommandId: Guid.NewGuid()));

    private static Task<long> MatureAsync(TermDepositConstitutionService service, Guid depositId) =>
        service.MatureAsync(new MatureDepositCommand(
            DepositId: depositId,
            MaturedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero),
            PayoutAccount: "PT50-DDA-001", Actor: "mcp:dev"));

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
