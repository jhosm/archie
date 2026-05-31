using Babelstone.Engine;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.TermDeposit.Tests;

/// <summary>
/// E.1 dispatch tests: the family module registers all eleven event types, and the engine
/// folds each through its handler into the deposit position. The canonical AT_MATURITY numbers
/// match the decider + financial-math kernel (the §5.4 withholding split).
/// </summary>
public sealed class TermDepositDispatchTests
{
    private static readonly HandlerRegistry Registry = TermDepositFamilyModule.Registry();

    [Fact]
    public void Module_registers_all_eleven_event_types()
    {
        var module = new TermDepositFamilyModule();
        Assert.Equal(11, module.Handlers.Count);
    }

    [Fact]
    public void Folds_constituted_then_accrued_then_withheld_then_matured_into_the_position()
    {
        var seed = DepositPosition.Empty;

        var afterConstitution = Dispatch(seed, new DepositConstituted(
            DepositId: Guid.NewGuid(),
            Principal: new Money(1_000_000),
            TanBasisPoints: 300,
            RateSheetVersionId: "rs-1",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            MaturityDate: new DateOnly(2027, 1, 15),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE"));

        var afterAccrual = Dispatch(afterConstitution, new InterestAccrued(
            new Money(30_417), new DateOnly(2027, 1, 15)));

        var afterWithholding = Dispatch(afterAccrual, new WithholdingApplied(
            new Money(8_517), new Money(21_900)));

        var afterMaturity = Dispatch(afterWithholding, new DepositMatured(
            PrincipalReturned: new Money(1_000_000),
            NetInterestPaid: new Money(21_900),
            TotalPayout: new Money(1_021_900),
            MaturedOn: new DateOnly(2027, 1, 15)));

        Assert.Equal(new Money(1_000_000), afterMaturity.Principal);
        Assert.Equal(new Money(30_417), afterMaturity.AccruedGrossInterest);
        Assert.Equal(new Money(8_517), afterMaturity.WithholdingToDate);
        Assert.Equal(new Money(21_900), afterMaturity.NetInterest);
        Assert.Equal(new Money(1_021_900), afterMaturity.TotalPayout);
        Assert.Equal(DepositLifecycle.Matured, afterMaturity.Lifecycle);
    }

    [Fact]
    public void Constituted_fold_carries_the_payment_period_for_periodic()
    {
        var seed = DepositPosition.Empty;

        var afterConstitution = Dispatch(seed, new DepositConstituted(
            DepositId: Guid.NewGuid(),
            Principal: new Money(1_000_000),
            TanBasisPoints: 300,
            RateSheetVersionId: "rs-1",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 1),
            MaturityDate: new DateOnly(2027, 1, 1),
            InterestVariant: "PERIODIC",
            AutoRenewalPolicy: "NONE",
            PaymentPeriodMonths: 3));

        Assert.Equal("PERIODIC", afterConstitution.InterestVariant);
        Assert.Equal(3, afterConstitution.PaymentPeriodMonths);
        Assert.Equal(0, afterConstitution.CouponsPaid);
    }

    [Fact]
    public void InterestPaid_folds_accumulate_gross_tax_net_and_coupon_count_across_coupons()
    {
        // Three PERIODIC coupons fold into the running tallies: the InterestPaidHandler
        // accumulates (it never overwrites), and CouponsPaid counts the coupons so the service
        // can derive the next coupon window deterministically (start + cadence × CouponsPaid).
        var state = Dispatch(DepositPosition.Empty, new DepositConstituted(
            DepositId: Guid.NewGuid(), Principal: new Money(1_000_000), TanBasisPoints: 300,
            RateSheetVersionId: "rs-1", TermDays: 365, StartDate: new DateOnly(2026, 1, 1),
            MaturityDate: new DateOnly(2027, 1, 1), InterestVariant: "PERIODIC",
            AutoRenewalPolicy: "NONE", PaymentPeriodMonths: 3));

        // Three distinct coupon flows (gross/tax/net each conserved gross = tax + net).
        var coupons = new[]
        {
            new InterestPaid(state.DepositId, new Money(7_500), new Money(2_100), new Money(5_400), new DateOnly(2026, 4, 1)),
            new InterestPaid(state.DepositId, new Money(7_583), new Money(2_123), new Money(5_460), new DateOnly(2026, 7, 1)),
            new InterestPaid(state.DepositId, new Money(7_667), new Money(2_147), new Money(5_520), new DateOnly(2026, 10, 1)),
        };

        foreach (var coupon in coupons)
        {
            state = Dispatch(state, coupon);
        }

        Assert.Equal(new Money(7_500 + 7_583 + 7_667), state.AccruedGrossInterest);
        Assert.Equal(new Money(2_100 + 2_123 + 2_147), state.WithholdingToDate);
        Assert.Equal(new Money(5_400 + 5_460 + 5_520), state.NetInterest);
        Assert.Equal(3, state.CouponsPaid);
        // Coupons are paid OUT — they do not capitalise the deposit balance (02 §2.1).
        Assert.Equal(new Money(1_000_000), state.Principal);
        Assert.Equal(DepositLifecycle.Active, state.Lifecycle);
    }

    [Fact]
    public void Dispatches_all_eleven_event_types_without_throwing()
    {
        var seed = DepositPosition.Empty;

        // One of each event; folding each must resolve a handler (no missing registration).
        var events = new DomainEvent[]
        {
            new DepositConstituted(Guid.NewGuid(), new Money(1_000_000), 300, "rs-1", 365,
                new DateOnly(2026, 1, 15), new DateOnly(2027, 1, 15), "AT_MATURITY", "NONE"),
            new InterestAccrued(new Money(30_417), new DateOnly(2027, 1, 15)),
            new WithholdingApplied(new Money(8_517), new Money(21_900)),
            new DepositMatured(new Money(1_000_000), new Money(21_900), new Money(1_021_900), new DateOnly(2027, 1, 15)),
            new DepositConstitutionFailed(Guid.NewGuid(), "RATE_SHEET_NOT_FOUND", "no sheet"),
            new InterestPaid(Guid.NewGuid(), new Money(10_000), new Money(2_800), new Money(7_200), new DateOnly(2026, 4, 15)),
            new DepositRenewed(Guid.NewGuid(), Guid.NewGuid(), new Money(1_000_000), "rs-2", 300, 365,
                new DateOnly(2027, 1, 15), new DateOnly(2028, 1, 15)),
            new DepositTerminatedEarly(Guid.NewGuid(), new Money(1_000_000), new Money(5_000), new Money(995_000),
                new DateOnly(2026, 6, 15), "customer_request"),
            new DepositPartiallyWithdrawn(Guid.NewGuid(), new Money(200_000), new Money(800_000), new DateOnly(2026, 6, 15)),
            new DepositCorrected(Guid.NewGuid(), "corr-1", "principal", "ref-old", "ref-new",
                new DateOnly(2026, 6, 15), "typo"),
            new DepositTransferredToHeirs(Guid.NewGuid(), "case-1", new Money(1_021_900), new DateOnly(2026, 6, 15)),
        };

        foreach (var @event in events)
        {
            var state = Dispatch(seed, @event);
            Assert.NotNull(state);
        }
    }

    private static DepositPosition Dispatch(DepositPosition state, DomainEvent @event)
    {
        var eventType = $"term_deposit.{@event.GetType().Name}";
        Assert.True(Registry.TryResolve(eventType, out var handler), $"no handler for {eventType}");
        return (DepositPosition)handler.ApplyBoxed(state, @event).NewState;
    }
}
