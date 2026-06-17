using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// The penalty-by-rate-reduction early-termination basis (F.11, bd k6r8.4): the early-break penalty
/// is modelled as a REDUCTION of the applied rate — the elapsed interest is recomputed at a lower
/// "penalty rate" and the penalty is the forfeited <c>J(original) − J(reduced)</c>, NOT a flat fee
/// or a basis-point share. Pure, Docker-free.
/// <para>
/// The load-bearing financial-correctness guard: withholding is FLOW-BY-FLOW on the REAL
/// (original-rate) gross the deposit earned (fin-math §5.4), never on the reduced figure — the rate
/// reduction is a penalty haircut on the gross that composes with the SAME settlement conservation
/// (<c>settlement = principal + net − penalty</c>) as every other basis. The depositor is taxed on
/// what they actually earned, and the penalty is what they forfeit.
/// </para>
/// </summary>
public sealed class RateReductionPenaltyTests
{
    private static readonly DateOnly Start = new(2026, 1, 15);
    private const long PrincipalCents = 1_000_000; // EUR 10,000.00
    private const int TanBps = 300;                // 3.00% contractual
    private const int IrsBps = 2800;               // 28% IRS withholding
    private const string Reason = "CUSTOMER_REQUEST";

    private static DepositPosition ActivePosition() => DepositPosition.Empty with
    {
        DepositId = Guid.NewGuid(),
        Principal = new Money(PrincipalCents),
        TanBasisPoints = TanBps,
        TermDays = 365,
        StartDate = Start,
        MaturityDate = Start.AddDays(365),
        InterestVariant = "AT_MATURITY",
        RemainingPrincipal = new Money(PrincipalCents),
        Lifecycle = DepositLifecycle.Active,
    };

    private static (InterestAccrued Accrued, WithholdingApplied Withheld, DepositTerminatedEarly Terminated) Decode(
        IReadOnlyList<Babelstone.Engine.DomainEvent> events)
    {
        Assert.Equal(3, events.Count);
        return (
            Assert.IsType<InterestAccrued>(events[0]),
            Assert.IsType<WithholdingApplied>(events[1]),
            Assert.IsType<DepositTerminatedEarly>(events[2]));
    }

    [Fact]
    public void RateReduction_penalty_is_the_forfeited_interest_difference_J_original_minus_J_reduced()
    {
        // Break at day 100. Original 3% accrual over 100 days, Act/360:
        //   J(orig) = 1,000,000 × 300 × 100 / (360×10000) = 8,333.33 → 8,333.
        // Penalty rate 1% (a 200bps reduction): J(reduced) = 1,000,000 × 100 × 100 / (360×10000)
        //   = 2,777.77 → 2,778.
        // Penalty = J(orig) − J(reduced) = 8,333 − 2,778 = 5,555.
        var policy = EarlyTerminationPolicy.FlatRateReduction(reducedRateBasisPoints: 100);
        var events = TermDepositDecider.DecideEarlyTermination(
            ActivePosition(), Start.AddDays(100), policy, DayCountConvention.Act360, IrsBps, Reason);
        var (accrued, withheld, terminated) = Decode(events);

        Assert.Equal(new Money(8_333), accrued.GrossInterest);   // the REAL accrued at the original rate
        Assert.Equal(new Money(5_555), terminated.PenaltyAmount); // J(orig) − J(reduced)

        // Cross-check J(reduced) independently.
        var reduced = Accrual.SimpleInterest(
            new Money(PrincipalCents), 100, DayCount.Between(Start, Start.AddDays(100), DayCountConvention.Act360));
        Assert.Equal(accrued.GrossInterest - reduced, terminated.PenaltyAmount);

        // Settlement conservation holds against the (effective) penalty.
        Assert.Equal(
            terminated.PrincipalReturned + withheld.Net - terminated.PenaltyAmount,
            terminated.NetSettlementAmount);
    }

    [Fact]
    public void Withholding_is_on_the_real_gross_not_the_rate_reduced_figure()
    {
        // The classic financial-correctness bug F.11 must avoid: withholding the REDUCED interest
        // would silently under-tax. Tax MUST be 28% of the REAL gross (8,333), = 2,333 — the same
        // tax a non-penalised termination at day 100 would withhold (the penalty changes the
        // SETTLEMENT, never the tax base).
        var policy = EarlyTerminationPolicy.FlatRateReduction(reducedRateBasisPoints: 100);
        var events = TermDepositDecider.DecideEarlyTermination(
            ActivePosition(), Start.AddDays(100), policy, DayCountConvention.Act360, IrsBps, Reason);
        var (accrued, withheld, _) = Decode(events);

        // Tax computed flow-by-flow on the REAL gross (8,333 × 28% = 2,333.24 → 2,333).
        var expectedTax = Withholding.Withhold(accrued.GrossInterest, IrsBps).Tax;
        Assert.Equal(expectedTax, withheld.Tax);
        Assert.Equal(new Money(2_333), withheld.Tax);
        Assert.Equal(new Money(6_000), withheld.Net); // gross − tax, conserved
    }

    [Fact]
    public void RateReduction_to_zero_forfeits_all_accrued_interest_like_a_100pct_accrued_penalty()
    {
        // A reduced rate of 0% means the depositor earns nothing for the elapsed period — penalty =
        // J(orig) − 0 = the whole accrued. This must equal a 100%-of-ACCRUED_INTEREST penalty.
        var rateReduction = EarlyTerminationPolicy.FlatRateReduction(reducedRateBasisPoints: 0);
        var shareOfAccrued = EarlyTerminationPolicy.Flat(
            penaltyBasisPoints: 10_000, PenaltyBasis.AccruedInterest);

        var viaReduction = Decode(TermDepositDecider.DecideEarlyTermination(
            ActivePosition(), Start.AddDays(100), rateReduction, DayCountConvention.Act360, IrsBps, Reason));
        var viaShare = Decode(TermDepositDecider.DecideEarlyTermination(
            ActivePosition(), Start.AddDays(100), shareOfAccrued, DayCountConvention.Act360, IrsBps, Reason));

        Assert.Equal(viaShare.Terminated.PenaltyAmount, viaReduction.Terminated.PenaltyAmount);
        Assert.Equal(viaShare.Terminated.NetSettlementAmount, viaReduction.Terminated.NetSettlementAmount);
    }

    [Fact]
    public void RateReduction_can_band_so_the_penalty_softens_as_the_deposit_ages()
    {
        // F.11 extends the F.4 flat/banded shape: a banded rate-reduction schedule reprices to a
        // harsher (lower) rate for an early break and a milder one later. ≤90d → reprice to 0%;
        // ≤365d → reprice to 1%. Break at day 60 forfeits ALL accrued; break at day 200 keeps the 1%.
        var policy = EarlyTerminationPolicy.Banded(
        [
            new EarlyTerminationBand(UpToDays: 90, PenaltyBasisPoints: 0, PenaltyBasis.RateReduction, ReducedRateBasisPoints: 0),
            new EarlyTerminationBand(UpToDays: null, PenaltyBasisPoints: 0, PenaltyBasis.RateReduction, ReducedRateBasisPoints: 100),
        ]);

        var early = Decode(TermDepositDecider.DecideEarlyTermination(
            ActivePosition(), Start.AddDays(60), policy, DayCountConvention.Act360, IrsBps, Reason));
        var late = Decode(TermDepositDecider.DecideEarlyTermination(
            ActivePosition(), Start.AddDays(200), policy, DayCountConvention.Act360, IrsBps, Reason));

        // Early: 0% reprice ⇒ penalty == the whole accrued (depositor forfeits all interest).
        Assert.Equal(early.Accrued.GrossInterest, early.Terminated.PenaltyAmount);
        // Late: 1% reprice ⇒ penalty is a STRICT fraction of the accrued (keeps the reduced interest).
        Assert.True(late.Terminated.PenaltyAmount.Cents < late.Accrued.GrossInterest.Cents);
        Assert.True(late.Terminated.PenaltyAmount.Cents > 0);
    }

    [Fact]
    public void RateReduction_band_without_a_reduced_rate_fails_loud()
    {
        // A RATE_REDUCTION band MUST carry a reduced rate — the decider never invents one.
        var policy = EarlyTerminationPolicy.Banded(
        [
            new EarlyTerminationBand(UpToDays: null, PenaltyBasisPoints: 0, PenaltyBasis.RateReduction, ReducedRateBasisPoints: null),
        ]);

        var ex = Assert.Throws<InvalidOperationException>(() => TermDepositDecider.DecideEarlyTermination(
            ActivePosition(), Start.AddDays(100), policy, DayCountConvention.Act360, IrsBps, Reason));
        Assert.Contains("reduced_rate_basis_points", ex.Message);
    }

    [Fact]
    public void RateReduction_above_the_original_rate_fails_loud_rather_than_recording_a_bonus()
    {
        // A "reduction" to a HIGHER rate (4% on a 3% deposit) would make J(reduced) > J(original) and
        // drive the penalty negative — a bonus, which the non-negative PenaltyAmount contract forbids.
        var policy = EarlyTerminationPolicy.FlatRateReduction(reducedRateBasisPoints: 400);

        var ex = Assert.Throws<InvalidOperationException>(() => TermDepositDecider.DecideEarlyTermination(
            ActivePosition(), Start.AddDays(100), policy, DayCountConvention.Act360, IrsBps, Reason));
        Assert.Contains("negative", ex.Message);
    }

    [Fact]
    public void RateReduction_is_a_deterministic_pure_function()
    {
        var policy = EarlyTerminationPolicy.FlatRateReduction(reducedRateBasisPoints: 150);
        var date = Start.AddDays(123);
        var position = ActivePosition(); // one fixed position — the decider must be deterministic on it

        var first = TermDepositDecider.DecideEarlyTermination(
            position, date, policy, DayCountConvention.Act360, IrsBps, Reason);
        var second = TermDepositDecider.DecideEarlyTermination(
            position, date, policy, DayCountConvention.Act360, IrsBps, Reason);

        Assert.Equal(first, second);
    }
}
