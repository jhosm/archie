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
        // Deploy the shared family rate sheet pricing all three variants' products (300/325/300 bps),
        // effective before any constitution. One sheet for the whole family because the engine resolves
        // the latest sheet effective for the FAMILY (not by product) and these tests share a container.
        await fixture.EnsureRateSheetAsync(SharedSheet);

        var (runtime, service, settlement) = Compose(fixture.ConnectionString);
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

    [Fact]
    public async Task Periodic_pays_coupons_out_then_matures_with_principal_plus_final_coupon()
    {
        // The k6r8.1 fixture: EUR 499,000.00, TAN 3.25%, monthly (12 coupons), Act/360, IRS 28%,
        // 2026-01-01 → 2027-01-01. Flow-by-flow withholding makes Σ per-coupon net ≠ the aggregate
        // rate-scaling shortcut gross_agg × (1 − 0.28). The shared family sheet prices the monthly
        // product (dpz_pt_12m_juros_mensais) at 325 bps.
        await fixture.EnsureRateSheetAsync(SharedSheet);

        var (runtime, service, settlement) = Compose(fixture.ConnectionString);
        var depositId = Guid.NewGuid();

        await service.ConstituteAsync(new ConstituteDepositCommand(
            DepositId: depositId, PrincipalCents: 49_900_000, ProductId: "dpz_pt_12m_juros_mensais", Role: "standard",
            TermDays: 365, StartDate: new DateOnly(2026, 1, 1),
            ConstitutedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            InterestVariant: "PERIODIC", AutoRenewalPolicy: "NONE", FundingAccount: "PT50-DDA-001",
            Actor: "mcp:dev", PaymentPeriodMonths: 1));

        // Pay the 11 intermediate coupons (the 12th rides with the principal at maturity). The
        // service derives each window from CouponsPaid — the caller just triggers the coupon.
        for (var i = 0; i < 11; i++)
        {
            await service.PayInterestAsync(new PayInterestCommand(
                DepositId: depositId, PaidAt: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(i),
                PayoutAccount: "PT50-DDA-001", Actor: "mcp:dev"));
        }

        await service.MatureAsync(new MatureDepositCommand(
            DepositId: depositId, MaturedAt: new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
            PayoutAccount: "PT50-DDA-001", Actor: "mcp:dev"));

        // The durable projection folds the per-coupon tallies summed flow-by-flow.
        var hydrated = await runtime.LoadAsync(depositId);
        var position = hydrated.State;
        Assert.Equal("PERIODIC", position.InterestVariant);
        Assert.Equal(1, position.PaymentPeriodMonths);
        Assert.Equal(new Money(49_900_000), position.Principal); // principal constant — coupons paid OUT, no capitalisation
        // CouponsPaid counts the 11 InterestPaid events; the 12th (final) coupon rides at maturity
        // as InterestAccrued+WithholdingApplied+DepositMatured (no standalone InterestPaid), so it is
        // NOT counted here — but its interest IS in the accrual tallies below.
        Assert.Equal(11, position.CouponsPaid);
        // Σ over all 12 independently-rounded coupon flows (the 11 coupons' + the final's accrual).
        Assert.Equal(new Money(1_644_277), position.AccruedGrossInterest);
        Assert.Equal(new Money(460_396), position.WithholdingToDate);
        Assert.Equal(new Money(1_183_881), position.NetInterest);
        // Maturity payout = principal + the FINAL coupon's net only (not the whole term's interest).
        Assert.Equal(new Money(49_900_000 + 100_549), position.TotalPayout);
        Assert.Equal(DepositLifecycle.Matured, position.Lifecycle);

        // k6r8.1: the engine's flow-by-flow net is NOT the aggregate rate-scaling shortcut.
        // gross_agg over the full 365 days = 1,644,274; × (1 − 0.28) = 1,183,877 ≠ 1,183,881.
        Assert.NotEqual(1_183_877L, position.NetInterest.Cents);
        Assert.Equal(4L, position.NetInterest.Cents - 1_183_877L);

        // Event/outbox count: 1 constituted + 11 coupons × 1 InterestPaid + 1 maturity × 3
        // (Accrued+Withheld+Matured) = 15 events, each paired. A coupon is a single self-contained
        // InterestPaid (no Accrued+Withheld pair) so it does not double-count in the fold.
        Assert.Equal(15, await fixture.CountAsync("events", "stream_id", depositId));
        Assert.Equal(15, await fixture.CountAsync("outbox", "aggregate_id", depositId));

        // Settlement legs: debit principal, 11 coupon credits (each the coupon net), then the maturity credit.
        Assert.Equal(SettlementDirection.Debit, settlement.Instructions[0].Direction);
        Assert.Equal(new Money(49_900_000), settlement.Instructions[0].Amount);
        Assert.Equal("constitution", settlement.Instructions[0].Reason);
        Assert.Equal(13, settlement.Instructions.Count); // 1 debit + 11 coupon credits + 1 maturity credit
        Assert.All(settlement.Instructions.Skip(1).Take(11), c =>
        {
            Assert.Equal(SettlementDirection.Credit, c.Direction);
            Assert.Equal("coupon", c.Reason);
        });
        Assert.Equal("maturity", settlement.Instructions[^1].Reason);
        Assert.Equal(new Money(49_900_000 + 100_549), settlement.Instructions[^1].Amount);

        // The 11 paid-out coupon nets sum to the running net minus the final-at-maturity coupon (100,549).
        var couponCreditTotal = settlement.Instructions.Skip(1).Take(11).Sum(c => c.Amount.Cents);
        Assert.Equal(1_183_881L - 100_549L, couponCreditTotal);
    }

    [Fact]
    public async Task Advance_pays_interest_up_front_then_matures_principal_only()
    {
        // ADVANCE: full-term interest paid at t=0 (CF(0) = -C + J), principal alone at maturity.
        // 1,000,000 @ 300bps, 365d Act/360, IRS 28% → same nominal interest as AT_MATURITY (no PV
        // discount). The shared family sheet prices the advance product (dpz_pt_12m_juros_antecip) at 300 bps.
        await fixture.EnsureRateSheetAsync(SharedSheet);

        var (runtime, service, settlement) = Compose(fixture.ConnectionString);
        var depositId = Guid.NewGuid();

        await service.ConstituteAsync(new ConstituteDepositCommand(
            DepositId: depositId, PrincipalCents: 1_000_000, ProductId: "dpz_pt_12m_juros_antecip", Role: "standard",
            TermDays: 365, StartDate: new DateOnly(2026, 1, 15),
            ConstitutedAt: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
            InterestVariant: "ADVANCE", AutoRenewalPolicy: "NONE", FundingAccount: "PT50-DDA-001", Actor: "mcp:dev"));

        // After constitution alone, the upfront interest is already folded (paid at t=0).
        var afterConstitution = (await runtime.LoadAsync(depositId)).State;
        Assert.Equal("ADVANCE", afterConstitution.InterestVariant);
        Assert.Equal(new Money(30_417), afterConstitution.AccruedGrossInterest);
        Assert.Equal(new Money(8_517), afterConstitution.WithholdingToDate);
        Assert.Equal(new Money(21_900), afterConstitution.NetInterest);
        Assert.Equal(DepositLifecycle.Active, afterConstitution.Lifecycle);

        await service.MatureAsync(new MatureDepositCommand(
            DepositId: depositId, MaturedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero),
            PayoutAccount: "PT50-DDA-001", Actor: "mcp:dev"));

        var position = (await runtime.LoadAsync(depositId)).State;
        // Maturity returns the PRINCIPAL only — interest was paid at t=0, never re-accrued.
        Assert.Equal(new Money(1_000_000), position.TotalPayout);
        Assert.Equal(new Money(30_417), position.AccruedGrossInterest); // unchanged from constitution
        Assert.Equal(new Money(21_900), position.NetInterest);          // unchanged
        Assert.Equal(DepositLifecycle.Matured, position.Lifecycle);

        // constituted(1) + upfront InterestPaid(1) + maturity DepositMatured(1) = 3 events. ADVANCE's
        // upfront interest is a single self-contained InterestPaid (no Accrued+Withheld pair).
        Assert.Equal(3, await fixture.CountAsync("events", "stream_id", depositId));
        Assert.Equal(3, await fixture.CountAsync("outbox", "aggregate_id", depositId));

        // Settlement: debit principal, credit the upfront interest net at t=0, credit the principal at maturity.
        Assert.Collection(
            settlement.Instructions,
            debit =>
            {
                Assert.Equal(SettlementDirection.Debit, debit.Direction);
                Assert.Equal(new Money(1_000_000), debit.Amount);
                Assert.Equal("constitution", debit.Reason);
            },
            advance =>
            {
                Assert.Equal(SettlementDirection.Credit, advance.Direction);
                Assert.Equal(new Money(21_900), advance.Amount);
                Assert.Equal("advance_interest", advance.Reason);
            },
            maturity =>
            {
                Assert.Equal(SettlementDirection.Credit, maturity.Direction);
                Assert.Equal(new Money(1_000_000), maturity.Amount);
                Assert.Equal("maturity", maturity.Reason);
            });
    }

    /// <summary>
    /// The one rate sheet the three Integration tests share (they share a Postgres container, and the
    /// engine resolves the latest sheet effective for the FAMILY, not by product). It is effective
    /// 2025-01-01 — before every constitution — and prices each variant's product: AT_MATURITY
    /// (dpz_pt_12m_juros_venc) @ 300 bps, PERIODIC (dpz_pt_12m_juros_mensais) @ 325 bps, ADVANCE
    /// (dpz_pt_12m_juros_antecip) @ 300 bps. Its version id is the canonical pt-deposits-2026.1 the
    /// AT_MATURITY test asserts the stamped position carries.
    /// </summary>
    private static RateSheet SharedSheet => TestRateSheets.MultiPriced(
        versionId: "pt-deposits-2026.1",
        effectiveFrom: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
        ("dpz_pt_12m_juros_venc", "standard", 300),
        ("dpz_pt_12m_juros_mensais", "standard", 325),
        ("dpz_pt_12m_juros_antecip", "standard", 300));

    /// <summary>Compose the durable runtime + decider over the term-deposit family with the in-memory
    /// settlement stub — the same composition the AT_MATURITY happy-path test uses (E.3 §D4).</summary>
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
