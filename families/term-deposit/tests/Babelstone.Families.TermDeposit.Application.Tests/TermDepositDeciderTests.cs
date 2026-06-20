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

        var constituted = TermDepositDecider.DecideConstitution(
            command, tanBasisPoints: TanBps, rateSheetVersionId: "pt-deposits-2026.1",
            partialWithdrawalPolicy: PartialWithdrawalPolicy.Unrestricted);

        Assert.Equal(new Money(PrincipalCents), constituted.Principal);
        Assert.Equal(TanBps, constituted.TanBasisPoints);
        Assert.Equal("pt-deposits-2026.1", constituted.RateSheetVersionId);
        Assert.Equal(Maturity, constituted.MaturityDate); // derived from start + term, not recomputed downstream
        Assert.Equal("AT_MATURITY", constituted.InterestVariant);
        // bd babelstone-v794: the catalogue product_code is stamped from the already-available
        // command.ProductId (no new command input) so the D.4 read model can denormalize it.
        Assert.Equal("dpz_pt_12m_juros_venc", constituted.ProductCode);
        // Unrestricted policy ⇒ the three F.12 gates stamp as 0 (bd k6r8.8/qze9).
        Assert.Equal(0, constituted.MinWithdrawalCents);
        Assert.Equal(0, constituted.MinRemainingBalanceCents);
        Assert.Equal(0, constituted.CarenciaDays);
    }

    [Fact]
    public void DecideConstitution_pins_the_resolved_partial_withdrawal_policy_on_the_event()
    {
        // bd k6r8.8/qze9: the F.12 policy is resolved from the product config and PINNED on
        // DepositConstituted at constitution (like the rate), so a later config edit cannot change a
        // live deposit's withdrawal rights (ADR-PC-009). The decider stamps the passed policy verbatim.
        var command = new ConstituteDepositCommand(
            DepositId: Guid.NewGuid(), PrincipalCents: PrincipalCents, ProductId: "dpz_pt_12m_resgate_parcial",
            Role: "standard", TermDays: 365, StartDate: Start,
            ConstitutedAt: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
            InterestVariant: "AT_MATURITY", AutoRenewalPolicy: "NONE", FundingAccount: "PT50-DDA-001", Actor: "test");

        var constituted = TermDepositDecider.DecideConstitution(
            command, tanBasisPoints: TanBps, rateSheetVersionId: "pt-deposits-2026.1",
            partialWithdrawalPolicy: new PartialWithdrawalPolicy(50_000, 100_000, 90));

        Assert.Equal(50_000, constituted.MinWithdrawalCents);
        Assert.Equal(100_000, constituted.MinRemainingBalanceCents);
        Assert.Equal(90, constituted.CarenciaDays);
    }

    // ---- commercial-eligibility preconditions (ADR-PC-024, F.9 babelstone-k6r8.2) --------------
    //
    // CheckPreconditions is the pure heart of CONSTITUTION_PRECONDITION_REFUSAL (commitment-catalogue
    // row 16): a required precondition absent or satisfied:false yields DepositConstitutionFailed,
    // computed entirely from the command's verdicts — no upstream call, no clock, no in-engine
    // evaluation (ADR-PC-024 §3–§5). The verdict's evaluated_at is upstream-supplied data on the
    // command, never read from a clock here.

    private static readonly DateTimeOffset EvaluatedAt = new(2026, 1, 14, 9, 0, 0, TimeSpan.Zero);

    private static PreconditionVerdict Verdict(bool satisfied) =>
        new(satisfied, EvidenceRef: "verdict-ref-001", EvaluatedAt: EvaluatedAt);

    [Fact]
    public void CheckPreconditions_ungated_product_never_refuses()
    {
        // v1 launch products declare no required_preconditions (02 §4) — the fast path returns null
        // even when no verdicts ride on the command.
        var refusal = TermDepositDecider.CheckPreconditions(
            Guid.NewGuid(), requiredPreconditions: Array.Empty<string>(), verdicts: null);

        Assert.Null(refusal);
    }

    [Fact]
    public void CheckPreconditions_all_required_verdicts_satisfied_proceeds()
    {
        var depositId = Guid.NewGuid();
        var verdicts = new Dictionary<string, PreconditionVerdict>
        {
            [TermDepositDecider.PreconditionIsNewMoney] = Verdict(satisfied: true),
            [TermDepositDecider.PreconditionSalaryDomiciled] = Verdict(satisfied: true),
        };

        var refusal = TermDepositDecider.CheckPreconditions(
            depositId,
            [TermDepositDecider.PreconditionIsNewMoney, TermDepositDecider.PreconditionSalaryDomiciled],
            verdicts);

        Assert.Null(refusal); // every required precondition present and satisfied ⇒ constitute proceeds
    }

    [Fact]
    public void CheckPreconditions_unsatisfied_verdict_refuses_with_eligibility_not_met()
    {
        var depositId = Guid.NewGuid();
        var verdicts = new Dictionary<string, PreconditionVerdict>
        {
            [TermDepositDecider.PreconditionIsNewMoney] = Verdict(satisfied: true),
            [TermDepositDecider.PreconditionSalaryDomiciled] = Verdict(satisfied: false), // upstream said NO
        };

        var refusal = TermDepositDecider.CheckPreconditions(
            depositId,
            [TermDepositDecider.PreconditionIsNewMoney, TermDepositDecider.PreconditionSalaryDomiciled],
            verdicts);

        var failed = Assert.IsType<DepositConstitutionFailed>(refusal);
        Assert.Equal(depositId, failed.DepositId);
        Assert.Equal(TermDepositDecider.EligibilityNotMetReason, failed.FailureReason);
        Assert.Equal("ELIGIBILITY_NOT_MET", failed.FailureReason);
        // Detail names the unmet KEY only (structural, never PII / never the evidence_ref).
        Assert.Contains("salary_domiciled", failed.FailureDetail);
        Assert.DoesNotContain("verdict-ref-001", failed.FailureDetail);
        Assert.DoesNotContain("is_new_money", failed.FailureDetail); // the satisfied one is not listed

        // The full resolved verdicts are recorded on the event for AUDIT LINEAGE (ADR-PC-024 §1),
        // ordered by key (Ordinal) so the record is replay-identical. Both verdicts ride — the
        // satisfied one too — so the trail shows which drove the refusal and on what (referenced) evidence.
        Assert.NotNull(failed.Preconditions);
        Assert.Collection(failed.Preconditions!,
            v => { Assert.Equal("is_new_money", v.Key); Assert.True(v.Satisfied); Assert.Equal("verdict-ref-001", v.EvidenceRef); },
            v => { Assert.Equal("salary_domiciled", v.Key); Assert.False(v.Satisfied); Assert.Equal(EvaluatedAt, v.EvaluatedAt); });
    }

    [Fact]
    public void CheckPreconditions_absent_required_verdict_refuses()
    {
        var depositId = Guid.NewGuid();
        // The saga failed to resolve salary_domiciled at all — an ABSENT verdict is a refusal, not a pass.
        var verdicts = new Dictionary<string, PreconditionVerdict>
        {
            [TermDepositDecider.PreconditionIsNewMoney] = Verdict(satisfied: true),
        };

        var refusal = TermDepositDecider.CheckPreconditions(
            depositId,
            [TermDepositDecider.PreconditionIsNewMoney, TermDepositDecider.PreconditionSalaryDomiciled],
            verdicts);

        var failed = Assert.IsType<DepositConstitutionFailed>(refusal);
        Assert.Equal(TermDepositDecider.EligibilityNotMetReason, failed.FailureReason);
        Assert.Contains("salary_domiciled", failed.FailureDetail);
    }

    [Fact]
    public void CheckPreconditions_no_verdicts_at_all_refuses_every_required_key()
    {
        var depositId = Guid.NewGuid();

        // A gated product whose command carries NO verdicts (null map) refuses on all required keys,
        // listed deterministically (Ordinal-sorted) so the recorded detail is replay-identical.
        var refusal = TermDepositDecider.CheckPreconditions(
            depositId,
            [TermDepositDecider.PreconditionIsNewMoney, TermDepositDecider.PreconditionSalaryDomiciled],
            verdicts: null);

        var failed = Assert.IsType<DepositConstitutionFailed>(refusal);
        Assert.Equal(TermDepositDecider.EligibilityNotMetReason, failed.FailureReason);
        // Ordinal order: is_new_money < salary_domiciled.
        Assert.Contains("is_new_money, salary_domiciled", failed.FailureDetail);
    }

    [Fact]
    public void CheckPreconditions_is_a_deterministic_pure_function()
    {
        var depositId = Guid.NewGuid();
        var verdicts = new Dictionary<string, PreconditionVerdict>
        {
            [TermDepositDecider.PreconditionIsNewMoney] = Verdict(satisfied: false),
        };
        IReadOnlyCollection<string> required = [TermDepositDecider.PreconditionIsNewMoney];

        var first = TermDepositDecider.CheckPreconditions(depositId, required, verdicts)!;
        var second = TermDepositDecider.CheckPreconditions(depositId, required, verdicts)!;

        // Replay re-derives the identical outcome (ADR-PC-024 §4): same scalar fields and a
        // CONTENT-identical recorded verdict lineage. (The lineage is a list, so equality is
        // element-wise content, not array reference identity — what "identical outcome" means.)
        Assert.Equal(first.DepositId, second.DepositId);
        Assert.Equal(first.FailureReason, second.FailureReason);
        Assert.Equal(first.FailureDetail, second.FailureDetail);
        Assert.Equal(first.Preconditions, second.Preconditions); // IReadOnlyList<record> ⇒ element-wise record equality
    }

    [Fact]
    public void DecideMaturity_reproduces_the_canonical_at_maturity_flow()
    {
        var position = (DepositPosition.Empty with
        {
            Principal = new Money(PrincipalCents),
            TanBasisPoints = TanBps,
            StartDate = Start,
            MaturityDate = Maturity,
            Lifecycle = DepositLifecycle.Active,
        }).AsFreshlyConstituted();

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
        var position = (DepositPosition.Empty with
        {
            Principal = new Money(PrincipalCents),
            TanBasisPoints = TanBps,
            StartDate = Start,
            MaturityDate = Maturity,
            Lifecycle = DepositLifecycle.Active,
        }).AsFreshlyConstituted();

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

    private static DepositPosition PeriodicPosition(int couponsPaid) => (DepositPosition.Empty with
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
    }).AsFreshlyConstituted();

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
        var position = (DepositPosition.Empty with
        {
            DepositId = Guid.NewGuid(),
            Principal = new Money(PrincipalCents),
            TanBasisPoints = TanBps,
            StartDate = Start,
            MaturityDate = Maturity,
            InterestVariant = "ADVANCE",
            Lifecycle = DepositLifecycle.Active,
        }).AsFreshlyConstituted();

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
        var position = (DepositPosition.Empty with
        {
            DepositId = Guid.NewGuid(),
            Principal = new Money(PrincipalCents),
            TanBasisPoints = TanBps,
            StartDate = Start,
            MaturityDate = Maturity,
            InterestVariant = "ADVANCE",
            Lifecycle = DepositLifecycle.Active,
        }).AsFreshlyConstituted();

        var events = TermDepositDecider.DecideMaturity(position, DayCountConvention.Act360, IrsBps);

        // No accrual/withholding at maturity — just the principal-only DepositMatured.
        var matured = Assert.IsType<DepositMatured>(Assert.Single(events));
        Assert.Equal(new Money(PrincipalCents), matured.PrincipalReturned);
        Assert.Equal(Money.Zero, matured.NetInterestPaid);
        Assert.Equal(new Money(PrincipalCents), matured.TotalPayout);
        Assert.Equal(Maturity, matured.MaturedOn);
    }

    // ---- F.12 re-base after a partial withdrawal (bd babelstone-emtr) ----------------------------
    //
    // The regression the depth-5 sim could not catch (its withdrawal leg stops at the withdrawal):
    // a deposit that partially withdraws mid-term and then runs ON to a coupon / to maturity. The fix
    // is that interest and the maturity principal-return follow the principal ACTUALLY held over time,
    // priced piecewise via the position's PrincipalTimeline — never the original constituted principal.

    [Fact]
    public void DecideMaturity_after_a_partial_withdrawal_returns_the_reduced_principal_and_piecewise_interest()
    {
        // €10,000 held for 120 days, then €7,000 after a €3,000 withdrawal on day 120 (365-day Act/360,
        // TAN 3%). Piecewise gross: €10,000×3%×120/360 (=10,000.00c) + €7,000×3%×245/360 (=14,291.67c),
        // summed un-rounded → 24,292c. Principal returned is the €7,000 still on deposit — the old code
        // returned the full €10,000, double-paying the withdrawn €3,000.
        var withdrawalOn = Start.AddDays(120);
        var position = DepositPosition.Empty with
        {
            Principal = new Money(PrincipalCents),
            TanBasisPoints = TanBps,
            StartDate = Start,
            MaturityDate = Maturity,
            InterestVariant = "AT_MATURITY",
            RemainingPrincipal = new Money(700_000),
            PrincipalTimeline =
            [
                new PrincipalSegment(Start, new Money(PrincipalCents)),
                new PrincipalSegment(withdrawalOn, new Money(700_000)),
            ],
            Lifecycle = DepositLifecycle.Active,
        };

        var events = TermDepositDecider.DecideMaturity(position, DayCountConvention.Act360, IrsBps);
        var accrued = Assert.IsType<InterestAccrued>(events[0]);
        var matured = Assert.IsType<DepositMatured>(events[2]);

        Assert.Equal(new Money(24_292), accrued.GrossInterest);
        // The load-bearing fix: maturity returns the principal still ON DEPOSIT, not the original.
        Assert.Equal(new Money(700_000), matured.PrincipalReturned);
        Assert.Equal(matured.PrincipalReturned + matured.NetInterestPaid, matured.TotalPayout);
        // Strictly between the two wrong answers it replaces: whole term on €7,000 (21,292c, the naive
        // re-base) and whole term on €10,000 (30,417c, the over-accrual this fix removes).
        Assert.True(accrued.GrossInterest.Cents > 21_292);
        Assert.True(accrued.GrossInterest.Cents < 30_417);
    }

    [Fact]
    public void DecideInterestPayment_after_a_partial_withdrawal_accrues_the_coupon_on_the_reduced_principal()
    {
        // A €100,000 withdrawal on day 20 (inside the already-paid first coupon window). The SECOND
        // coupon window (Feb 1 → Mar 1, 28 days) opens entirely after it, so it accrues on the reduced
        // €399,000 — not the original €499,000. 39,900,000 × 325 × 28 / (360×10000) = 100,858.33 → 100,858c.
        var withdrawalOn = KStart.AddDays(20);
        var position = PeriodicPosition(couponsPaid: 1) with
        {
            RemainingPrincipal = new Money(KPrincipalCents - 10_000_000),
            PrincipalTimeline =
            [
                new PrincipalSegment(KStart, new Money(KPrincipalCents)),
                new PrincipalSegment(withdrawalOn, new Money(KPrincipalCents - 10_000_000)),
            ],
        };

        var events = TermDepositDecider.DecideInterestPayment(
            position, new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 1), DayCountConvention.Act360, IrsBps);
        var paid = Assert.IsType<InterestPaid>(Assert.Single(events));

        Assert.Equal(new Money(100_858), paid.GrossInterest);
        // Less than the same coupon on the un-reduced €499,000 (126,136c) — the withdrawal lowered the base.
        Assert.True(paid.GrossInterest.Cents < 126_136);
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
        // The product / role / funding the constitution persisted (bd babelstone-mtto.5) — the SOURCE
        // a renewal recovers from. Non-default values so the chain-preservation assertions are meaningful.
        ProductCode = "dpz_pt_12m_juros_venc",
        Role = "standard",
        FundingAccount = "PT50-DDA-001",
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
            tanBasisPoints: 275, rateSheetVersionId: "pt-deposits-2027.1", renewalDate: renewalDate,
            role: TermDepositDecider.EffectiveRenewalRole(closing), fundingAccount: closing.FundingAccount);

        Assert.Equal(newDepositId, renewed.DepositId);
        Assert.Equal(new Money(PrincipalCents), renewed.Principal);   // rolled-over principal
        Assert.Equal(275, renewed.TanBasisPoints);                    // the policy-resolved new rate
        Assert.Equal("pt-deposits-2027.1", renewed.RateSheetVersionId);
        Assert.Equal(365, renewed.TermDays);                          // SAME term
        Assert.Equal(renewalDate, renewed.StartDate);                 // new start = renewal date
        Assert.Equal(renewalDate.AddDays(365), renewed.MaturityDate); // new maturity derived, not recomputed downstream
        Assert.Equal("AT_MATURITY", renewed.InterestVariant);         // same variant
        Assert.Equal("SAME_TERM_CURRENT_RATE", renewed.AutoRenewalPolicy); // same policy
        // product / role / funding carried forward from the closing deposit (chain preservation, mtto.5).
        Assert.Equal("dpz_pt_12m_juros_venc", renewed.ProductCode);
        Assert.Equal("standard", renewed.Role);
        Assert.Equal("PT50-DDA-001", renewed.FundingAccount);
    }

    [Fact]
    public void EffectiveRenewalRole_carries_the_closing_role_forward_and_defaults_an_empty_one_to_standard()
    {
        // A deposit constituted WITH a role carries it forward unchanged.
        var withRole = ClosingPosition("SAME_TERM_CURRENT_RATE") with { Role = "premium" };
        Assert.Equal("premium", TermDepositDecider.EffectiveRenewalRole(withRole));

        // The pre-field-deposit fallback (bd babelstone-mtto.5): a deposit constituted BEFORE role was
        // persisted folds to Role == "" (the Avro default); the renewal defaults it to standard (the v1
        // default role) so the (product, role) re-resolution still works rather than failing on "".
        var preField = ClosingPosition("SAME_TERM_CURRENT_RATE") with { Role = "" };
        Assert.Equal(TermDepositDecider.DefaultRole, TermDepositDecider.EffectiveRenewalRole(preField));
        Assert.Equal("standard", TermDepositDecider.EffectiveRenewalRole(preField));
    }

    [Fact]
    public void DecideRenewalLink_carries_the_old_and_new_ids_and_the_new_pinned_facts()
    {
        var closing = ClosingPosition("SAME_TERM_CURRENT_RATE");
        var newDepositId = Guid.NewGuid();
        var renewed = TermDepositDecider.DecideRenewalConstitution(
            closing, newDepositId, rolloverPrincipal: new Money(PrincipalCents),
            tanBasisPoints: 275, rateSheetVersionId: "pt-deposits-2027.1", renewalDate: Maturity,
            role: TermDepositDecider.EffectiveRenewalRole(closing), fundingAccount: closing.FundingAccount);

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
