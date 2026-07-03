using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// The pure early-termination decision core (F.4, babelstone-nbip; ADR-PC-021 §P3) — no I/O, no
/// clock, default CI lane. Pins the flat + banded penalty policy (02 §2.5): band first-match against
/// the elapsed term, the penalty basis (accrued interest / principal / both), and floor enforcement —
/// all with FLOW-BY-FLOW withholding on the one accrued flow (fin-math §5.4), never rate-scaled, and
/// every amount rounded once at the Money boundary (ADR-PC-010 §P1–§P2).
/// </summary>
public sealed class EarlyTerminationDeciderTests
{
    // Canonical instance: EUR 10,000.00, TAN 3.00%, Act/360, IRS 28% — the same shape the AT_MATURITY
    // decider tests use, so the accrued-interest sub-flow is directly comparable.
    private static readonly DateOnly Start = new(2026, 1, 15);
    private const long PrincipalCents = 1_000_000;
    private const int TanBps = 300;
    private const int IrsBps = 2800;
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
        var accrued = Assert.IsType<InterestAccrued>(events[0]);
        var withheld = Assert.IsType<WithholdingApplied>(events[1]);
        var terminated = Assert.IsType<DepositTerminatedEarly>(events[2]);
        // The withholding leg is stamped with its paired accrual's date — the same-date invariant the
        // ledger's pre-field attribution (PendingAccrual) rests on; asserted for every termination shape.
        Assert.Equal(accrued.AsOf, withheld.WithheldOn);
        return (accrued, withheld, terminated);
    }

    // ---- flat policy (a degenerate one-band schedule) ------------------------------------------

    [Fact]
    public void Flat_policy_loses_all_accrued_interest_and_withholds_the_one_accrued_flow()
    {
        // Flat: 100% of accrued interest, no floor. Terminate at day 100.
        var policy = EarlyTerminationPolicy.Flat(penaltyBasisPoints: 10_000, PenaltyBasis.AccruedInterest);
        var terminationDate = Start.AddDays(100);

        var events = TermDepositDecider.DecideEarlyTermination(
            ActivePosition(), terminationDate, policy, DayCountConvention.Act360, IrsBps, Reason);
        var (accrued, withheld, terminated) = Decode(events);

        // gross = 1,000,000 × 300bps × 100 / (360×10000) = 8,333.33 → 8,333 (one HALF_EVEN boundary).
        Assert.Equal(new Money(8_333), accrued.GrossInterest);
        Assert.Equal(terminationDate, accrued.AsOf);
        // Withhold that ONE flow (flow-by-flow, fin-math §5.4): tax = 8,333 × 28% = 2,333.24 → 2,333.
        Assert.Equal(new Money(2_333), withheld.Tax);
        Assert.Equal(new Money(6_000), withheld.Net);                 // gross − tax, conserved
        // Penalty = 100% of GROSS accrued = 8,333 (priced on the headline, not the post-tax net).
        Assert.Equal(new Money(8_333), terminated.PenaltyAmount);
        Assert.Equal(new Money(PrincipalCents), terminated.PrincipalReturned);
        // Settlement = principal + net − penalty = 1,000,000 + 6,000 − 8,333 = 997,667.
        Assert.Equal(new Money(997_667), terminated.NetSettlementAmount);
        Assert.Equal(terminationDate, terminated.TerminatedOn);
        Assert.Equal(Reason, terminated.TerminationReason);
    }

    [Fact]
    public void Settlement_conserves_principal_plus_net_minus_penalty_to_the_cent()
    {
        var policy = EarlyTerminationPolicy.Flat(penaltyBasisPoints: 10_000, PenaltyBasis.AccruedInterest);
        var events = TermDepositDecider.DecideEarlyTermination(
            ActivePosition(), Start.AddDays(100), policy, DayCountConvention.Act360, IrsBps, Reason);
        var (_, withheld, terminated) = Decode(events);

        // The whole point of withholding flow-by-flow and pricing on gross: the three legs reconcile
        // exactly — settlement = principal + net − penalty, no hidden cent.
        Assert.Equal(
            terminated.PrincipalReturned + withheld.Net - terminated.PenaltyAmount,
            terminated.NetSettlementAmount);
    }

    [Fact]
    public void DecideEarlyTermination_is_a_deterministic_pure_function()
    {
        var policy = EarlyTerminationPolicy.Flat(penaltyBasisPoints: 5_000, PenaltyBasis.AccruedInterest);
        var position = ActivePosition();
        var date = Start.AddDays(123);

        var first = TermDepositDecider.DecideEarlyTermination(position, date, policy, DayCountConvention.Act360, IrsBps, Reason);
        var second = TermDepositDecider.DecideEarlyTermination(position, date, policy, DayCountConvention.Act360, IrsBps, Reason);

        Assert.Equal(first, second); // record-equal events across runs — no clock, no randomness
    }

    // ---- banded schedule across multiple windows (first-match against elapsed term) ------------

    // The §2.5 worked example: [≤30d → 100%, ≤90d → 50%, null → 25%] of accrued interest.
    private static EarlyTerminationPolicy WorkedExample() => EarlyTerminationPolicy.Banded(
    [
        new EarlyTerminationBand(UpToDays: 30, PenaltyBasisPoints: 10_000, PenaltyBasis.AccruedInterest),
        new EarlyTerminationBand(UpToDays: 90, PenaltyBasisPoints: 5_000, PenaltyBasis.AccruedInterest),
        new EarlyTerminationBand(UpToDays: null, PenaltyBasisPoints: 2_500, PenaltyBasis.AccruedInterest),
    ]);

    [Theory]
    // day 20 → first band (≤30d, 100%): gross 1,667, net 1,200, penalty 1,667, settle 999,533.
    [InlineData(20, 1_667, 1_200, 1_667, 999_533)]
    // day 60 → second band (≤90d, 50%): gross 5,000, net 3,600, penalty 2,500, settle 1,001,100.
    [InlineData(60, 5_000, 3_600, 2_500, 1_001_100)]
    // day 200 → open tail (25%): gross 16,667, net 12,000, penalty 4,167, settle 1,007,833.
    [InlineData(200, 16_667, 12_000, 4_167, 1_007_833)]
    public void Banded_schedule_first_matches_the_window_and_prices_each_band(
        int elapsedDays, long expectedGross, long expectedNet, long expectedPenalty, long expectedSettlement)
    {
        var terminationDate = Start.AddDays(elapsedDays);

        var events = TermDepositDecider.DecideEarlyTermination(
            ActivePosition(), terminationDate, WorkedExample(), DayCountConvention.Act360, IrsBps, Reason);
        var (accrued, withheld, terminated) = Decode(events);

        Assert.Equal(new Money(expectedGross), accrued.GrossInterest);
        Assert.Equal(new Money(expectedNet), withheld.Net);
        Assert.Equal(new Money(expectedPenalty), terminated.PenaltyAmount);
        Assert.Equal(new Money(expectedSettlement), terminated.NetSettlementAmount);
    }

    [Fact]
    public void Banded_picks_the_first_band_whose_window_is_not_yet_exceeded_on_the_boundary_day()
    {
        // Exactly day 30 still falls in the first band (≤30 is inclusive); day 31 falls to the second.
        var atBoundary = TermDepositDecider.DecideEarlyTermination(
            ActivePosition(), Start.AddDays(30), WorkedExample(), DayCountConvention.Act360, IrsBps, Reason);
        var pastBoundary = TermDepositDecider.DecideEarlyTermination(
            ActivePosition(), Start.AddDays(31), WorkedExample(), DayCountConvention.Act360, IrsBps, Reason);

        var (accruedAt, _, terminatedAt) = Decode(atBoundary);
        var (_, _, terminatedPast) = Decode(pastBoundary);

        // Day 30: 100% of accrued → penalty == gross (the whole accrued interest is taken).
        Assert.Equal(accruedAt.GrossInterest, terminatedAt.PenaltyAmount);
        // Day 31: 50% band → penalty is a STRICT fraction of accrued, less than the gross.
        var pastAccrued = ((InterestAccrued)pastBoundary[0]).GrossInterest;
        Assert.True(terminatedPast.PenaltyAmount.Cents < pastAccrued.Cents);
    }

    [Fact]
    public void ResolveBand_fails_loud_when_no_band_covers_the_elapsed_term()
    {
        // A malformed schedule: bounded windows only, no open (null) tail. An elapsed term past the
        // last window matches nothing — fail loud rather than settle at a silent zero penalty.
        var policy = EarlyTerminationPolicy.Banded(
        [
            new EarlyTerminationBand(UpToDays: 30, PenaltyBasisPoints: 10_000, PenaltyBasis.AccruedInterest),
            new EarlyTerminationBand(UpToDays: 90, PenaltyBasisPoints: 5_000, PenaltyBasis.AccruedInterest),
        ]);

        Assert.Throws<InvalidOperationException>(() => policy.ResolveBand(elapsedDays: 200));
    }

    // ---- basis selection (accrued interest / principal / both) ---------------------------------

    [Fact]
    public void Basis_accrued_interest_prices_the_penalty_on_the_gross_accrued_flow()
    {
        // 100% of accrued at day 100: penalty == gross accrued (8,333).
        var policy = EarlyTerminationPolicy.Flat(penaltyBasisPoints: 10_000, PenaltyBasis.AccruedInterest);
        var events = TermDepositDecider.DecideEarlyTermination(
            ActivePosition(), Start.AddDays(100), policy, DayCountConvention.Act360, IrsBps, Reason);
        var (accrued, _, terminated) = Decode(events);

        Assert.Equal(accrued.GrossInterest, terminated.PenaltyAmount); // 8,333
        Assert.Equal(new Money(997_667), terminated.NetSettlementAmount);
    }

    [Fact]
    public void Basis_principal_prices_the_penalty_on_the_principal_not_the_interest()
    {
        // 1% (100bps) of PRINCIPAL at day 100: penalty = 1,000,000 × 100bps / 10000 = 10,000.
        var policy = EarlyTerminationPolicy.Flat(penaltyBasisPoints: 100, PenaltyBasis.Principal);
        var events = TermDepositDecider.DecideEarlyTermination(
            ActivePosition(), Start.AddDays(100), policy, DayCountConvention.Act360, IrsBps, Reason);
        var (_, _, terminated) = Decode(events);

        Assert.Equal(new Money(10_000), terminated.PenaltyAmount);
        // settle = 1,000,000 + 6,000 − 10,000 = 996,000.
        Assert.Equal(new Money(996_000), terminated.NetSettlementAmount);
    }

    [Fact]
    public void Basis_both_prices_the_penalty_on_principal_plus_accrued_interest()
    {
        // 1% (100bps) of (principal + gross accrued) at day 100: basis = 1,000,000 + 8,333 = 1,008,333,
        // penalty = 1,008,333 × 100bps / 10000 = 10,083.33 → 10,083 (one boundary).
        var policy = EarlyTerminationPolicy.Flat(penaltyBasisPoints: 100, PenaltyBasis.Both);
        var events = TermDepositDecider.DecideEarlyTermination(
            ActivePosition(), Start.AddDays(100), policy, DayCountConvention.Act360, IrsBps, Reason);
        var (_, _, terminated) = Decode(events);

        Assert.Equal(new Money(10_083), terminated.PenaltyAmount);
        // settle = 1,000,000 + 6,000 − 10,083 = 995,917.
        Assert.Equal(new Money(995_917), terminated.NetSettlementAmount);
    }

    // ---- floor enforcement ---------------------------------------------------------------------

    [Fact]
    public void Floor_caps_the_settlement_and_reduces_the_effective_penalty_when_it_binds()
    {
        // 50% of PRINCIPAL at day 100 would take 500,000 (settle 506,000), but a floor at the full
        // principal (1,000,000) lifts the payout. The depositor's net never falls below the floor.
        var policy = EarlyTerminationPolicy.Flat(
            penaltyBasisPoints: 5_000, PenaltyBasis.Principal, floorCents: PrincipalCents);
        var events = TermDepositDecider.DecideEarlyTermination(
            ActivePosition(), Start.AddDays(100), policy, DayCountConvention.Act360, IrsBps, Reason);
        var (_, withheld, terminated) = Decode(events);

        // Settlement is lifted to the floor exactly.
        Assert.Equal(new Money(PrincipalCents), terminated.NetSettlementAmount);
        // The EFFECTIVE penalty recorded is what brings (principal + net) down to the floor:
        // 1,000,000 + 6,000 − 1,000,000 = 6,000 — NOT the 500,000 headline.
        Assert.Equal(new Money(6_000), terminated.PenaltyAmount);
        // Conservation still holds against the recorded (effective) penalty.
        Assert.Equal(
            terminated.PrincipalReturned + withheld.Net - terminated.PenaltyAmount,
            terminated.NetSettlementAmount);
    }

    [Fact]
    public void Floor_does_not_bind_when_the_settlement_already_clears_it()
    {
        // A floor below the natural settlement is inert: the headline penalty and settlement pass
        // through unchanged. 100% of accrued (8,333) at day 100 settles 997,667 > floor 900,000.
        var policy = EarlyTerminationPolicy.Flat(
            penaltyBasisPoints: 10_000, PenaltyBasis.AccruedInterest, floorCents: 900_000);
        var events = TermDepositDecider.DecideEarlyTermination(
            ActivePosition(), Start.AddDays(100), policy, DayCountConvention.Act360, IrsBps, Reason);
        var (accrued, _, terminated) = Decode(events);

        Assert.Equal(accrued.GrossInterest, terminated.PenaltyAmount); // 8,333, the headline
        Assert.Equal(new Money(997_667), terminated.NetSettlementAmount);
    }

    [Fact]
    public void Floor_above_the_natural_maximum_payout_fails_loud_rather_than_recording_a_negative_penalty()
    {
        // A misconfigured floor (02 §2.5 frames the floor as a principal-protection MINIMUM,
        // floor <= principal <= principal + net). At day 100 the natural max payout is
        // principal + net = 1,000,000 + 6,000 = 1,006,000. A floor ABOVE that (here 1,100,000)
        // could only be honoured by a negative penalty — inventing money — so the decider fails loud
        // instead of emitting a non-conforming negative PenaltyAmount.
        var policy = EarlyTerminationPolicy.Flat(
            penaltyBasisPoints: 10_000, PenaltyBasis.AccruedInterest, floorCents: 1_100_000);

        var ex = Assert.Throws<InvalidOperationException>(() => TermDepositDecider.DecideEarlyTermination(
            ActivePosition(), Start.AddDays(100), policy, DayCountConvention.Act360, IrsBps, Reason));
        Assert.Contains("negative penalty", ex.Message);
    }

    [Fact]
    public void Floor_exactly_at_the_natural_maximum_payout_binds_with_a_zero_penalty()
    {
        // The boundary case: a floor exactly at principal + net (1,006,000) is the largest floor that
        // does NOT require inventing money. It binds (the natural settlement after any positive penalty
        // is below it), lifting the payout to the full principal + net with a ZERO effective penalty —
        // the penalty is exactly absorbed, never driven negative.
        var policy = EarlyTerminationPolicy.Flat(
            penaltyBasisPoints: 10_000, PenaltyBasis.AccruedInterest, floorCents: 1_006_000);
        var events = TermDepositDecider.DecideEarlyTermination(
            ActivePosition(), Start.AddDays(100), policy, DayCountConvention.Act360, IrsBps, Reason);
        var (_, withheld, terminated) = Decode(events);

        Assert.Equal(new Money(1_006_000), terminated.NetSettlementAmount);   // principal + net
        Assert.Equal(Money.Zero, terminated.PenaltyAmount);                   // exactly absorbed, non-negative
        // Conservation still holds against the recorded (zero) penalty.
        Assert.Equal(
            terminated.PrincipalReturned + withheld.Net - terminated.PenaltyAmount,
            terminated.NetSettlementAmount);
    }

    [Fact]
    public void Penalty_is_priced_on_gross_accrued_not_the_rate_scaled_post_tax_net()
    {
        // Guard against the classic bug: a 100%-of-accrued penalty must take the GROSS accrued
        // interest (8,333), so the depositor keeps only the principal less the withholding-driven
        // gap — NOT a penalty computed off the post-tax net (which would be 6,000 and silently
        // wrong). gross (8,333) ≠ net (6,000), so the two paths are distinguishable.
        var policy = EarlyTerminationPolicy.Flat(penaltyBasisPoints: 10_000, PenaltyBasis.AccruedInterest);
        var events = TermDepositDecider.DecideEarlyTermination(
            ActivePosition(), Start.AddDays(100), policy, DayCountConvention.Act360, IrsBps, Reason);
        var (accrued, withheld, terminated) = Decode(events);

        Assert.Equal(accrued.GrossInterest, terminated.PenaltyAmount);
        Assert.NotEqual(withheld.Net, terminated.PenaltyAmount); // priced on gross, not net
    }
}
