using Babelstone.Engine;
using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// The pure decision core (ADR-PC-021 §P3) — no I/O, default CI lane. Pins that the decider
/// stamps the resolved rate inputs and reproduces the canonical AT_MATURITY flow from the
/// financial-math kernel. The durable resolve→settle→append wiring is the integration tier.
/// </summary>
public sealed class TermDepositDeciderTests
{
    // Canonical instance: EUR 10,000.00, TAN 3.00%, 365d Act/360, IRS 28% (matches the E.1
    // dispatch test and the pt.2026.1 sealed corpus pt_dpz_12m_simple_with_irs).
    private static readonly DateOnly Start = new(2026, 1, 15);
    private static readonly DateOnly Maturity = new(2027, 1, 15);
    private const long PrincipalCents = 1_000_000;
    private const int TanBps = 300;
    private const int IrsBps = 2800;

    [Fact]
    public void DecideConstitution_stamps_the_resolved_tan_and_version_and_derives_maturity()
    {
        var command = new ConstituteDepositCommand(
            DepositId: Guid.NewGuid(), PrincipalCents: PrincipalCents, ProductId: "dpz_pt_12m_juros_venc",
            Role: "standard", TermDays: 365, StartDate: Start,
            ConstitutedAt: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
            InterestVariant: "AT_MATURITY", AutoRenewalPolicy: "NONE", FundingAccount: "PT50-DDA-001", Actor: "test");

        var constituted = TermDepositDecider.DecideConstitution(command, tanBasisPoints: TanBps, rateSheetVersionId: "pt-deposits-2026.1");

        Assert.Equal(new Money(PrincipalCents), constituted.Principal);
        Assert.Equal(TanBps, constituted.TanBasisPoints);
        Assert.Equal("pt-deposits-2026.1", constituted.RateSheetVersionId);
        Assert.Equal(Maturity, constituted.MaturityDate); // derived from start + term, not recomputed downstream
        Assert.Equal("AT_MATURITY", constituted.InterestVariant);
        // bd babelstone-v794: the catalogue product_code is stamped from the already-available
        // command.ProductId (no new command input) so the D.4 read model can denormalize it.
        Assert.Equal("dpz_pt_12m_juros_venc", constituted.ProductCode);
    }

    [Fact]
    public void DecideMaturity_reproduces_the_canonical_at_maturity_flow()
    {
        var position = DepositPosition.Empty with
        {
            Principal = new Money(PrincipalCents),
            TanBasisPoints = TanBps,
            StartDate = Start,
            MaturityDate = Maturity,
            Lifecycle = DepositLifecycle.Active,
        };

        var events = TermDepositDecider.DecideMaturity(position, DayCountConvention.Act360, IrsBps);

        var accrued = Assert.IsType<InterestAccrued>(events[0]);
        var withheld = Assert.IsType<WithholdingApplied>(events[1]);
        var matured = Assert.IsType<DepositMatured>(events[2]);

        Assert.Equal(new Money(30_417), accrued.GrossInterest);
        Assert.Equal(Maturity, accrued.AsOf);
        Assert.Equal(new Money(8_517), withheld.Tax);
        Assert.Equal(new Money(21_900), withheld.Net);          // gross − tax, conserved to the cent
        Assert.Equal(new Money(PrincipalCents), matured.PrincipalReturned);
        Assert.Equal(new Money(21_900), matured.NetInterestPaid);
        Assert.Equal(new Money(1_021_900), matured.TotalPayout); // principal + net
        Assert.Equal(Maturity, matured.MaturedOn);
    }

    [Fact]
    public void DecideMaturity_is_a_deterministic_pure_function()
    {
        var position = DepositPosition.Empty with
        {
            Principal = new Money(PrincipalCents),
            TanBasisPoints = TanBps,
            StartDate = Start,
            MaturityDate = Maturity,
            Lifecycle = DepositLifecycle.Active,
        };

        var first = TermDepositDecider.DecideMaturity(position, DayCountConvention.Act360, IrsBps);
        var second = TermDepositDecider.DecideMaturity(position, DayCountConvention.Act360, IrsBps);

        Assert.Equal(first, second); // record-equal events across runs
    }

    // ---- PERIODIC (02 §2.1: CF(k) = +J_k, coupons paid OUT, principal constant) ----------------

    // The k6r8.1 fixture: EUR 499,000.00, TAN 3.25%, monthly (12 coupons), Act/360, IRS 28%,
    // 2026-01-01 → 2027-01-01. Each coupon is a SEPARATE accrual→withhold flow, so summing the
    // 12 per-coupon nets does NOT equal the aggregate rate-scaling shortcut gross_agg×(1−0.28).
    private static readonly DateOnly KStart = new(2026, 1, 1);
    private static readonly DateOnly KMaturity = new(2027, 1, 1);
    private const long KPrincipalCents = 49_900_000;
    private const int KTanBps = 325;

    private static DepositPosition PeriodicPosition(int couponsPaid) => DepositPosition.Empty with
    {
        DepositId = Guid.NewGuid(),
        Principal = new Money(KPrincipalCents),
        TanBasisPoints = KTanBps,
        StartDate = KStart,
        MaturityDate = KMaturity,
        InterestVariant = "PERIODIC",
        PaymentPeriodMonths = 1,
        CouponsPaid = couponsPaid,
        Lifecycle = DepositLifecycle.Active,
    };

    [Fact]
    public void DecideInterestPayment_accrues_one_coupon_window_and_withholds_that_one_flow()
    {
        var position = PeriodicPosition(couponsPaid: 0);

        // First coupon window: 2026-01-01 → 2026-02-01 (31 days, Act/360).
        var events = TermDepositDecider.DecideInterestPayment(
            position, new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1), DayCountConvention.Act360, IrsBps);

        // A coupon emits ONLY a self-contained InterestPaid (gross+tax+net in one event) — NOT also
        // the AT_MATURITY Accrued+Withheld pair, which would double-count in the fold (both paths
        // accumulate the same tallies).
        var paid = Assert.IsType<InterestPaid>(Assert.Single(events));

        // gross = 49,900,000 × 325bps × 31 / (360 × 10000) = 139,651.39 → 139,651 (HALF_EVEN, one boundary).
        Assert.Equal(new Money(139_651), paid.GrossInterest);
        // tax = 139,651 × 28% = 39,102.28 → 39,102; net = gross − tax (conserved).
        Assert.Equal(new Money(39_102), paid.WithholdingTax);
        Assert.Equal(new Money(100_549), paid.NetInterest);
        Assert.Equal(new DateOnly(2026, 2, 1), paid.PaidOn);
    }

    [Fact]
    public void Periodic_flow_by_flow_net_differs_from_the_aggregate_rate_scaling_shortcut_k6r8_1()
    {
        // Walk all 12 monthly coupons; the last one is paid WITH the principal at maturity, so it
        // comes from the PERIODIC maturity branch. Sum every coupon's net the way the engine does:
        // one Withhold per coupon flow, accumulated.
        var boundaries = new[]
        {
            new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 1),
            new DateOnly(2026, 4, 1), new DateOnly(2026, 5, 1), new DateOnly(2026, 6, 1),
            new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1),
            new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 1),
            new DateOnly(2027, 1, 1),
        };

        long sumGross = 0, sumTax = 0, sumNet = 0;
        for (int k = 0; k < 12; k++)
        {
            var position = PeriodicPosition(couponsPaid: k);
            if (k < 11)
            {
                // An intermediate coupon emits a single InterestPaid carrying gross/tax/net.
                var paid = (InterestPaid)Assert.Single(
                    TermDepositDecider.DecideInterestPayment(
                        position, boundaries[k], boundaries[k + 1], DayCountConvention.Act360, IrsBps));
                sumGross += paid.GrossInterest.Cents;
                sumTax += paid.WithholdingTax.Cents;
                sumNet += paid.NetInterest.Cents;
            }
            else
            {
                // The final coupon rides at maturity as the AT_MATURITY-shaped Accrued+Withheld+Matured.
                var events = TermDepositDecider.DecideMaturity(position, DayCountConvention.Act360, IrsBps);
                var accrued = (InterestAccrued)events[0];
                var withheld = (WithholdingApplied)events[1];
                sumGross += accrued.GrossInterest.Cents;
                sumTax += withheld.Tax.Cents;
                sumNet += withheld.Net.Cents;
            }
        }

        // Flow-by-flow conservation holds across all 12 coupons.
        Assert.Equal(sumGross, sumTax + sumNet);
        Assert.Equal(1_644_277, sumGross); // Σ of 12 independently-rounded coupon grosses
        Assert.Equal(460_396, sumTax);
        Assert.Equal(1_183_881, sumNet);

        // The (WRONG) aggregate shortcut: accrue once over the full term, then rate-scale the gross
        // by (1 − 0.28). This is what an AT_MATURITY single flow does — exact ONLY for one flow.
        var aggFactor = DayCount.Between(KStart, KMaturity, DayCountConvention.Act360);
        var aggGross = Accrual.SimpleInterest(new Money(KPrincipalCents), KTanBps, aggFactor);
        Assert.Equal(1_644_274, aggGross.Cents); // 49,900,000 × 325bps × 365 / (360×10000), one rounding
        // gross_agg × (1 − 0.28) = 1,644,274 × 0.72 = 1,183,877.28 → 1,183,877.
        var shortcutNet = Money.FromCents((decimal)aggGross.Cents * (10_000 - IrsBps) / 10_000);
        Assert.Equal(1_183_877, shortcutNet.Cents);

        // THE POINT (k6r8.1): the engine's flow-by-flow net is NOT the rate-scaling shortcut.
        // The aggregate gross is even 3c LOWER than the summed gross (12 per-coupon roundings vs one),
        // yet the summed NET is 4c HIGHER than the shortcut — the two simplifications disagree.
        Assert.NotEqual(shortcutNet.Cents, sumNet);
        Assert.Equal(4, sumNet - shortcutNet.Cents);   // a real 4-cent gap, summed per coupon
        Assert.Equal(3, sumGross - aggGross.Cents);     // summed gross also differs from the one-shot accrual
    }

    [Fact]
    public void DecideMaturity_periodic_pays_only_the_final_coupon_with_the_principal()
    {
        // 11 coupons already paid; maturity accrues ONLY the final window (2026-12-01 → 2027-01-01,
        // 31 days) and pays principal + that final net — it does NOT re-accrue the whole term.
        var position = PeriodicPosition(couponsPaid: 11);

        var events = TermDepositDecider.DecideMaturity(position, DayCountConvention.Act360, IrsBps);

        var accrued = Assert.IsType<InterestAccrued>(events[0]);
        var withheld = Assert.IsType<WithholdingApplied>(events[1]);
        var matured = Assert.IsType<DepositMatured>(events[2]);

        Assert.Equal(new Money(139_651), accrued.GrossInterest); // the final 31-day coupon, not the whole term
        Assert.Equal(new DateOnly(2027, 1, 1), accrued.AsOf);
        Assert.Equal(new Money(100_549), withheld.Net);
        Assert.Equal(new Money(KPrincipalCents), matured.PrincipalReturned);
        Assert.Equal(new Money(100_549), matured.NetInterestPaid);
        Assert.Equal(new Money(KPrincipalCents + 100_549), matured.TotalPayout);
        Assert.Equal(new DateOnly(2027, 1, 1), matured.MaturedOn);
    }

    [Fact]
    public void CouponBoundary_caps_at_maturity_so_the_final_coupon_is_a_short_stub()
    {
        var position = PeriodicPosition(couponsPaid: 0);

        Assert.Equal(new DateOnly(2026, 1, 1), TermDepositDecider.CouponBoundary(position, 0));
        Assert.Equal(new DateOnly(2026, 7, 1), TermDepositDecider.CouponBoundary(position, 6));
        Assert.Equal(new DateOnly(2026, 12, 1), TermDepositDecider.CouponBoundary(position, 11));
        // Boundary 12 would be 2027-01-01 (the maturity) — capped there, not beyond.
        Assert.Equal(KMaturity, TermDepositDecider.CouponBoundary(position, 12));
        Assert.Equal(KMaturity, TermDepositDecider.CouponBoundary(position, 13));
    }

    // ---- ADVANCE (02 §2.1: CF(0) = -C + J, full-term interest at t=0, principal only at maturity) ----

    [Fact]
    public void DecideAdvance_pays_full_term_interest_up_front_no_present_value_discount()
    {
        var position = DepositPosition.Empty with
        {
            DepositId = Guid.NewGuid(),
            Principal = new Money(PrincipalCents),
            TanBasisPoints = TanBps,
            StartDate = Start,
            MaturityDate = Maturity,
            InterestVariant = "ADVANCE",
            Lifecycle = DepositLifecycle.Active,
        };

        var events = TermDepositDecider.DecideAdvance(position, DayCountConvention.Act360, IrsBps);

        // ADVANCE emits a single InterestPaid dated the start (CF(0)) — the same self-contained event
        // a coupon uses, not the Accrued+Withheld pair (which would double-count in the fold).
        var paid = Assert.IsType<InterestPaid>(Assert.Single(events));

        // The nominal interest is the SAME full-term SimpleInterest as AT_MATURITY — only the timing
        // differs (paid at t=0, no PV discount, fin-math §5.3). 1,000,000 @ 300bps, 365d Act/360.
        Assert.Equal(new Money(30_417), paid.GrossInterest);
        Assert.Equal(new Money(8_517), paid.WithholdingTax);
        Assert.Equal(new Money(21_900), paid.NetInterest);
        Assert.Equal(Start, paid.PaidOn);               // CF(0): interest paid at t=0
    }

    [Fact]
    public void DecideMaturity_advance_returns_principal_only_no_re_accrual()
    {
        // ADVANCE paid its interest at t=0; maturity returns the principal alone (CF(n) = +C).
        var position = DepositPosition.Empty with
        {
            DepositId = Guid.NewGuid(),
            Principal = new Money(PrincipalCents),
            TanBasisPoints = TanBps,
            StartDate = Start,
            MaturityDate = Maturity,
            InterestVariant = "ADVANCE",
            Lifecycle = DepositLifecycle.Active,
        };

        var events = TermDepositDecider.DecideMaturity(position, DayCountConvention.Act360, IrsBps);

        // No accrual/withholding at maturity — just the principal-only DepositMatured.
        var matured = Assert.IsType<DepositMatured>(Assert.Single(events));
        Assert.Equal(new Money(PrincipalCents), matured.PrincipalReturned);
        Assert.Equal(Money.Zero, matured.NetInterestPaid);
        Assert.Equal(new Money(PrincipalCents), matured.TotalPayout);
        Assert.Equal(Maturity, matured.MaturedOn);
    }

    // ---- auto-renewal (02 §2.4.4: NONE / SAME_TERM_CURRENT_RATE / SAME_TERM_SAME_RATE) ----------

    private static DepositPosition ClosingPosition(string policy) => DepositPosition.Empty with
    {
        DepositId = Guid.NewGuid(),
        Principal = new Money(PrincipalCents),
        TanBasisPoints = TanBps,
        RateSheetVersionId = "pt-deposits-2026.1",
        TermDays = 365,
        StartDate = Start,
        MaturityDate = Maturity,
        InterestVariant = "AT_MATURITY",
        AutoRenewalPolicy = policy,
        RemainingPrincipal = new Money(PrincipalCents),
        Lifecycle = DepositLifecycle.Active,
    };

    [Fact]
    public void ResolveRenewalRate_current_rate_takes_the_freshly_resolved_sheet()
    {
        var closing = ClosingPosition("SAME_TERM_CURRENT_RATE");

        // The bank's then-current standard rate moved to 275bps on a new sheet — CURRENT_RATE takes it.
        var (tan, version) = TermDepositDecider.ResolveRenewalRate(closing, currentTanBasisPoints: 275, currentRateSheetVersionId: "pt-deposits-2027.1");

        Assert.Equal(275, tan);
        Assert.Equal("pt-deposits-2027.1", version);
    }

    [Fact]
    public void ResolveRenewalRate_same_rate_carries_the_original_rate_forward_ignoring_the_current_sheet()
    {
        var closing = ClosingPosition("SAME_TERM_SAME_RATE");

        // Even though the current sheet now prices 275bps, SAME_RATE carries the closing deposit's
        // original 300bps / original version forward unchanged (02 §2.4.4 — pack-restricted policy).
        var (tan, version) = TermDepositDecider.ResolveRenewalRate(closing, currentTanBasisPoints: 275, currentRateSheetVersionId: "pt-deposits-2027.1");

        Assert.Equal(TanBps, tan);                       // 300, the original
        Assert.Equal("pt-deposits-2026.1", version);     // the original version, not the current sheet
    }

    [Fact]
    public void DecideRenewalConstitution_rolls_principal_at_resolved_rate_for_the_same_term()
    {
        var closing = ClosingPosition("SAME_TERM_CURRENT_RATE");
        var newDepositId = Guid.NewGuid();
        var renewalDate = Maturity; // renewal fires at maturity

        var renewed = TermDepositDecider.DecideRenewalConstitution(
            closing, newDepositId, rolloverPrincipal: new Money(PrincipalCents),
            tanBasisPoints: 275, rateSheetVersionId: "pt-deposits-2027.1", renewalDate: renewalDate);

        Assert.Equal(newDepositId, renewed.DepositId);
        Assert.Equal(new Money(PrincipalCents), renewed.Principal);   // rolled-over principal
        Assert.Equal(275, renewed.TanBasisPoints);                    // the policy-resolved new rate
        Assert.Equal("pt-deposits-2027.1", renewed.RateSheetVersionId);
        Assert.Equal(365, renewed.TermDays);                          // SAME term
        Assert.Equal(renewalDate, renewed.StartDate);                 // new start = renewal date
        Assert.Equal(renewalDate.AddDays(365), renewed.MaturityDate); // new maturity derived, not recomputed downstream
        Assert.Equal("AT_MATURITY", renewed.InterestVariant);         // same variant
        Assert.Equal("SAME_TERM_CURRENT_RATE", renewed.AutoRenewalPolicy); // same policy
    }

    [Fact]
    public void DecideRenewalLink_carries_the_old_and_new_ids_and_the_new_pinned_facts()
    {
        var closing = ClosingPosition("SAME_TERM_CURRENT_RATE");
        var newDepositId = Guid.NewGuid();
        var renewed = TermDepositDecider.DecideRenewalConstitution(
            closing, newDepositId, rolloverPrincipal: new Money(PrincipalCents),
            tanBasisPoints: 275, rateSheetVersionId: "pt-deposits-2027.1", renewalDate: Maturity);

        var link = TermDepositDecider.DecideRenewalLink(closing, renewed);

        Assert.Equal(closing.DepositId, link.DepositId);
        Assert.Equal(newDepositId, link.NewDepositId);
        Assert.Equal(new Money(PrincipalCents), link.RolloverPrincipal);
        Assert.Equal("pt-deposits-2027.1", link.NewRateSheetVersionId);
        Assert.Equal(275, link.NewTanBasisPoints);
        Assert.Equal(365, link.NewTermDays);
        Assert.Equal(Maturity, link.RenewalDate);
        Assert.Equal(Maturity.AddDays(365), link.NewMaturityDate);
    }
}
