using Babelstone.Engine;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.TermDeposit.Tests;

/// <summary>
/// B.10 mutation backstop for the DepositPosition folds (Handlers.cs). The projection/dispatch suites
/// exercise the AT_MATURITY happy path (constitute → accrue → withhold → mature) and the accrual/
/// maturity/withholding LEDGER folds, but they leave the rest of the lifecycle folds — and the
/// "replace-a-sum-with-one-operand" arithmetic mutants — unpinned, so a wrong fold would survive
/// mutation. These pure fold assertions close that gap: each applies one event handler through the
/// family registry and pins the field(s) it sets.
///
/// The accumulating folds (interest, withholding, coupons, corrections) are driven from a NON-zero
/// prior state so that mutating <c>state.X + event.Y</c> to either operand (<c>state.X</c> or
/// <c>event.Y</c>) changes the result and is killed — folding once from Empty would let the
/// "= event.Y" mutant survive (0 + Y == Y).
/// </summary>
public sealed class HandlerFoldTests
{
    private static DepositPosition Fold(DepositPosition state, DomainEvent @event)
    {
        var registry = TermDepositFamilyModule.Registry();
        var eventType = $"term_deposit.{@event.GetType().Name}";
        Assert.True(registry.TryResolve(eventType, out var handler), $"no handler for {eventType}");
        return (DepositPosition)handler.ApplyBoxed(state, @event).NewState;
    }

    [Fact]
    public void DepositConstituted_seeds_the_active_position_and_opening_principal_segment()
    {
        var depositId = Guid.NewGuid();
        var position = Fold(DepositPosition.Empty, new DepositConstituted(
            DepositId: depositId, Principal: new Money(1_000_000), TanBasisPoints: 300,
            RateSheetVersionId: "rs-1", TermDays: 365, StartDate: new DateOnly(2026, 1, 1),
            MaturityDate: new DateOnly(2027, 1, 1), InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE", PaymentPeriodMonths: 3, ProductCode: "dpz_pt_12m",
            Role: "standard", FundingAccount: "acct-ref-1",
            MinWithdrawalCents: 10_000, MinRemainingBalanceCents: 50_000, LockupPeriodDays: 30));

        Assert.Equal(DepositLifecycle.Active, position.Lifecycle);
        Assert.Equal(depositId, position.DepositId);
        Assert.Equal(new Money(1_000_000), position.Principal);
        Assert.Equal(new Money(1_000_000), position.RemainingPrincipal);
        Assert.Equal(300, position.TanBasisPoints);
        Assert.Equal("rs-1", position.RateSheetVersionId);
        Assert.Equal(365, position.TermDays);
        Assert.Equal(new DateOnly(2026, 1, 1), position.StartDate);
        Assert.Equal(new DateOnly(2027, 1, 1), position.MaturityDate);
        Assert.Equal("AT_MATURITY", position.InterestVariant);
        Assert.Equal("NONE", position.AutoRenewalPolicy);
        Assert.Equal(3, position.PaymentPeriodMonths);
        Assert.Equal("dpz_pt_12m", position.ProductCode);
        Assert.Equal("standard", position.Role);
        Assert.Equal("acct-ref-1", position.FundingAccount);
        Assert.Equal(10_000, position.MinWithdrawalCents);
        Assert.Equal(50_000, position.MinRemainingBalanceCents);
        Assert.Equal(30, position.LockupPeriodDays);
        // The opening segment is (start, full principal) — the single-segment timeline a deposit that
        // never partially withdraws keeps.
        var segment = Assert.Single(position.PrincipalTimeline);
        Assert.Equal(new DateOnly(2026, 1, 1), segment.From);
        Assert.Equal(new Money(1_000_000), segment.Principal);
    }

    [Fact]
    public void InterestAccrued_accumulates_gross_interest_across_flows()
    {
        var position = Fold(DepositPosition.Empty, new InterestAccrued(new Money(10_000), new DateOnly(2026, 6, 1)));
        position = Fold(position, new InterestAccrued(new Money(5_001), new DateOnly(2026, 7, 1)));

        // The SUM, not either operand: 10_000 + 5_001 = 15_001 (kills + → −, and + → state/event).
        Assert.Equal(new Money(15_001), position.AccruedGrossInterest);
    }

    [Fact]
    public void WithholdingApplied_accumulates_tax_and_net_across_flows()
    {
        var position = Fold(DepositPosition.Empty, new WithholdingApplied(new Money(2_800), new Money(7_200)));
        position = Fold(position, new WithholdingApplied(new Money(1_400), new Money(3_600)));

        Assert.Equal(new Money(4_200), position.WithholdingToDate);
        Assert.Equal(new Money(10_800), position.NetInterest);
    }

    [Fact]
    public void DepositMatured_labels_matured_and_records_the_total_payout()
    {
        var position = Fold(DepositPosition.Empty, new DepositMatured(
            PrincipalReturned: new Money(1_000_000), NetInterestPaid: new Money(21_900),
            TotalPayout: new Money(1_021_900), MaturedOn: new DateOnly(2027, 1, 1)));

        Assert.Equal(DepositLifecycle.Matured, position.Lifecycle);
        Assert.Equal(new Money(1_021_900), position.TotalPayout);
    }

    [Fact]
    public void DepositConstitutionFailed_labels_failed_and_keeps_the_deposit_id()
    {
        var depositId = Guid.NewGuid();
        var position = Fold(DepositPosition.Empty, new DepositConstitutionFailed(
            depositId, "RATE_SHEET_NOT_FOUND", "no sheet pinned for term_deposit"));

        Assert.Equal(DepositLifecycle.Failed, position.Lifecycle);
        Assert.Equal(depositId, position.DepositId);
    }

    [Fact]
    public void InterestPaid_accumulates_the_three_tallies_and_counts_the_coupon()
    {
        var depositId = Guid.NewGuid();
        var position = Fold(DepositPosition.Empty, new InterestPaid(depositId, new Money(7_500), new Money(2_100), new Money(5_400), new DateOnly(2026, 4, 1)));
        position = Fold(position, new InterestPaid(depositId, new Money(7_600), new Money(2_128), new Money(5_472), new DateOnly(2026, 7, 1)));

        Assert.Equal(new Money(15_100), position.AccruedGrossInterest);
        Assert.Equal(new Money(4_228), position.WithholdingToDate);
        Assert.Equal(new Money(10_872), position.NetInterest);
        // Two coupons counted (kills CouponsPaid + 1 → − 1 and the statement-removal that leaves 0).
        Assert.Equal(2, position.CouponsPaid);
    }

    [Fact]
    public void DepositRenewed_labels_renewed()
    {
        var position = Fold(DepositPosition.Empty, new DepositRenewed(
            DepositId: Guid.NewGuid(), NewDepositId: Guid.NewGuid(), RolloverPrincipal: new Money(1_000_000),
            NewRateSheetVersionId: "rs-2", NewTanBasisPoints: 300, NewTermDays: 365,
            RenewalDate: new DateOnly(2027, 1, 1), NewMaturityDate: new DateOnly(2028, 1, 1)));

        Assert.Equal(DepositLifecycle.Renewed, position.Lifecycle);
    }

    [Fact]
    public void DepositTerminatedEarly_labels_terminated_and_records_the_net_settlement()
    {
        var position = Fold(DepositPosition.Empty, new DepositTerminatedEarly(
            DepositId: Guid.NewGuid(), PrincipalReturned: new Money(1_000_000), PenaltyAmount: new Money(1_500),
            NetSettlementAmount: new Money(1_018_400), TerminatedOn: new DateOnly(2026, 6, 1),
            TerminationReason: "CUSTOMER_REQUEST"));

        Assert.Equal(DepositLifecycle.TerminatedEarly, position.Lifecycle);
        Assert.Equal(new Money(1_018_400), position.SettlementAmount);
    }

    [Fact]
    public void DepositPartiallyWithdrawn_records_remaining_principal_and_appends_a_timeline_segment()
    {
        // Seed with a constitution so there is an opening segment to append to.
        var position = Fold(DepositPosition.Empty, new DepositConstituted(
            DepositId: Guid.NewGuid(), Principal: new Money(1_000_000), TanBasisPoints: 300,
            RateSheetVersionId: "rs-1", TermDays: 365, StartDate: new DateOnly(2026, 1, 1),
            MaturityDate: new DateOnly(2027, 1, 1), InterestVariant: "AT_MATURITY", AutoRenewalPolicy: "NONE"));

        position = Fold(position, new DepositPartiallyWithdrawn(
            DepositId: Guid.NewGuid(), WithdrawnAmount: new Money(200_000),
            RemainingPrincipal: new Money(800_000), WithdrawnOn: new DateOnly(2026, 5, 1)));

        Assert.Equal(new Money(800_000), position.RemainingPrincipal);
        // Opening segment PLUS the withdrawal segment, in order (kills the spread → single-element mutants).
        Assert.Equal(2, position.PrincipalTimeline.Count);
        Assert.Equal(new DateOnly(2026, 1, 1), position.PrincipalTimeline[0].From);
        Assert.Equal(new Money(1_000_000), position.PrincipalTimeline[0].Principal);
        Assert.Equal(new DateOnly(2026, 5, 1), position.PrincipalTimeline[1].From);
        Assert.Equal(new Money(800_000), position.PrincipalTimeline[1].Principal);
    }

    [Fact]
    public void DepositCorrected_substitutes_the_typed_value_and_increments_the_correction_count()
    {
        var depositId = Guid.NewGuid();
        var position = Fold(DepositPosition.Empty, new DepositCorrected(
            depositId, "corr-1", "principal", new Money(10_000_000L), null, null, null,
            new DateOnly(2026, 6, 1), "TYPO"));
        position = Fold(position, new DepositCorrected(
            depositId, "corr-2", "rate", null, 350, null, null,
            new DateOnly(2026, 7, 1), "RATE_FIX"));

        // Two corrections counted (kills + 1 → − 1 and the statement-removal that leaves 0).
        Assert.Equal(2, position.CorrectionCount);
        // Typed inline value substitution (bd babelstone-j7mm.2): the corrected principal AND rate read back.
        Assert.Equal(new Money(10_000_000L), position.Principal);
        Assert.Equal(350, position.TanBasisPoints);
    }

    [Fact]
    public void DepositTransferredToHeirs_labels_transferred_and_records_the_balance()
    {
        var position = Fold(DepositPosition.Empty, new DepositTransferredToHeirs(
            DepositId: Guid.NewGuid(), HeirCaseRef: "succ-case-1",
            TransferredBalance: new Money(1_021_900), TransferDate: new DateOnly(2026, 8, 1)));

        Assert.Equal(DepositLifecycle.TransferredToHeirs, position.Lifecycle);
        Assert.Equal(new Money(1_021_900), position.SettlementAmount);
    }

    [Fact]
    public void WithErased_labels_the_position_erased_leaving_structural_fields_intact()
    {
        // GDPR Article 17 terminal transition (ADR-PC-004 §P3/A4): only the lifecycle flips; the
        // non-personal structural fields stay queryable.
        var constituted = Fold(DepositPosition.Empty, new DepositConstituted(
            DepositId: Guid.NewGuid(), Principal: new Money(1_000_000), TanBasisPoints: 300,
            RateSheetVersionId: "rs-1", TermDays: 365, StartDate: new DateOnly(2026, 1, 1),
            MaturityDate: new DateOnly(2027, 1, 1), InterestVariant: "AT_MATURITY", AutoRenewalPolicy: "NONE"));

        var erased = constituted.WithErased();

        Assert.Equal(DepositLifecycle.Erased, erased.Lifecycle);
        Assert.Equal(constituted.Principal, erased.Principal);
        Assert.Equal(constituted.RateSheetVersionId, erased.RateSheetVersionId);
    }
}
