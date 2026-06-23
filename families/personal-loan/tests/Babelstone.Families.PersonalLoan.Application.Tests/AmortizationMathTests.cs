using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.PersonalLoan.Application.Tests;

/// <summary>
/// The French-system (constant-installment) amortization kernel — the financial-math
/// heart of the personal_loan family (fin-math §3–§4, §7.4–§7.5). These pin the worked example from the
/// financial-concepts doc to the cent, the conservation invariants (capital legs sum to principal; balance
/// reaches zero), the integer-cent rounding discipline (ADR-PC-010 §P1–§P2), and the capped early-repayment
/// commission. Pure / Docker-free — every input is explicit (ADR-PC-010 §P5).
/// </summary>
public sealed class AmortizationMathTests
{
    [Fact]
    public void LevelInstallment_matches_the_financial_concepts_worked_example()
    {
        // fin-math §4.1: C = €10,000, TAN = 6% (monthly r = 0.5% = 50 bps), n = 12 ⇒ P = €860.66.
        var installment = Amortization.LevelInstallment(new Money(1_000_000), periodicRateBps: 50, periods: 12);
        Assert.Equal(new Money(86_066), installment); // €860.66
    }

    [Fact]
    public void Schedule_matches_the_worked_example_first_rows_to_the_cent()
    {
        // fin-math §4.1 amortization schedule: month 1 interest €50.00 / capital €810.66; month 2 opening
        // €9,189.34, interest €45.95 / capital €814.71; month 3 opening €8,374.63, interest €41.87.
        var schedule = Amortization.Schedule(new Money(1_000_000), periodicRateBps: 50, periods: 12);

        Assert.Equal(12, schedule.Count);

        Assert.Equal(new Money(1_000_000), schedule[0].OpeningBalance);
        Assert.Equal(new Money(5_000), schedule[0].Interest);   // €50.00
        Assert.Equal(new Money(81_066), schedule[0].Capital);   // €810.66
        Assert.Equal(new Money(918_934), schedule[0].ClosingBalance); // €9,189.34

        Assert.Equal(new Money(918_934), schedule[1].OpeningBalance);
        Assert.Equal(new Money(4_595), schedule[1].Interest);   // €45.95
        Assert.Equal(new Money(81_471), schedule[1].Capital);   // €814.71

        Assert.Equal(new Money(837_463), schedule[2].OpeningBalance); // €8,374.63
        Assert.Equal(new Money(4_187), schedule[2].Interest);   // €41.87
    }

    [Fact]
    public void Schedule_conserves_capital_to_the_principal_and_zeroes_the_balance()
    {
        var principal = new Money(1_000_000);
        var schedule = Amortization.Schedule(principal, periodicRateBps: 50, periods: 12);

        // The capital legs sum EXACTLY to the principal (integer-cent conservation, ADR-PC-010 §P2).
        var totalCapital = schedule.Aggregate(Money.Zero, (acc, row) => acc + row.Capital);
        Assert.Equal(principal, totalCapital);

        // The final balance is exactly zero — the balancing final row absorbs accumulated rounding.
        Assert.Equal(Money.Zero, schedule[^1].ClosingBalance);

        // Each closing balance equals the previous opening minus that row's capital (the §3 recurrence).
        for (var i = 0; i < schedule.Count; i++)
        {
            Assert.Equal(schedule[i].OpeningBalance - schedule[i].Capital, schedule[i].ClosingBalance);
            Assert.Equal(schedule[i].Interest + schedule[i].Capital, schedule[i].Installment);
        }
    }

    [Theory]
    // Golden conservation fixtures with AWKWARD inputs where the per-period capital does NOT divide
    // evenly, so the balancing final row carries a NON-trivial residual (unlike the tidy €10,000/50bps/12m
    // case whose drift is a benign +4c). These lock the balancing-final-row invariant
    // (CREDITO_PESSOAL_AMORTIZATION_MATH, ADR-PC-031 §D3 / commitment-catalogue CP-1) against a rounding
    // regression: whatever the inputs, the capital legs sum EXACTLY to the principal and S(n) == 0 exactly.
    [InlineData(1_000_001L, 50, 12)]   // odd principal (€10,000.01) — the cent that doesn't split cleanly
    [InlineData(1_000_001L, 71, 60)]   // odd principal + 60m term + an odd 0.71% periodic rate
    [InlineData(2_345_679L, 83, 72)]   // awkward principal/rate/term (€23,456.79 / 0.83% / 72m)
    [InlineData(999_999L, 29, 36)]     // just-under-round principal, low rate, 36m
    [InlineData(5_000_000L, 0, 60)]    // 0% promo over 60m (5,000,000 / 60 has a non-integer-cent quotient)
    public void Schedule_conserves_to_the_cent_for_awkward_inputs(long principalCents, int periodicRateBps, int periods)
    {
        var principal = new Money(principalCents);
        var schedule = Amortization.Schedule(principal, periodicRateBps, periods);

        Assert.Equal(periods, schedule.Count);

        // Capital legs sum EXACTLY to the principal — no cent created or lost across the whole schedule.
        var totalCapital = schedule.Aggregate(Money.Zero, (acc, row) => acc + row.Capital);
        Assert.Equal(principal, totalCapital);

        // The balance reaches EXACTLY zero — the balancing final row absorbs the accumulated penny
        // rounding regardless of how awkward the inputs are.
        Assert.Equal(Money.Zero, schedule[^1].ClosingBalance);

        // And the §3 recurrence + the installment identity hold on every row, including the balancing one.
        for (var i = 0; i < schedule.Count; i++)
        {
            Assert.Equal(schedule[i].OpeningBalance - schedule[i].Capital, schedule[i].ClosingBalance);
            Assert.Equal(schedule[i].Interest + schedule[i].Capital, schedule[i].Installment);
            // Capital never goes negative and never exceeds the opening balance (a row cannot over-amortize).
            Assert.True(schedule[i].Capital.Cents >= 0, $"row {i + 1} capital is negative");
            Assert.True(schedule[i].Capital.Cents <= schedule[i].OpeningBalance.Cents, $"row {i + 1} over-amortizes");
        }
    }

    [Fact]
    public void Schedule_interest_decreases_and_capital_increases_over_the_term()
    {
        // The characteristic of the French system (fin-math §4.1): interest shrinks, capital grows, the
        // installment stays level (except the balancing final row).
        var schedule = Amortization.Schedule(new Money(1_000_000), periodicRateBps: 50, periods: 12);

        for (var i = 1; i < schedule.Count; i++)
        {
            Assert.True(schedule[i].Interest.Cents <= schedule[i - 1].Interest.Cents,
                $"interest must not increase: row {i}");
            Assert.True(schedule[i].Capital.Cents >= schedule[i - 1].Capital.Cents,
                $"capital must not decrease: row {i}");
        }
    }

    [Fact]
    public void Zero_rate_loan_amortizes_capital_evenly_with_no_interest()
    {
        // A 0%-TAN promotional loan: the installment degenerates to C / n with no interest leg.
        var schedule = Amortization.Schedule(new Money(1_200_000), periodicRateBps: 0, periods: 12);

        Assert.All(schedule, row => Assert.Equal(Money.Zero, row.Interest));
        Assert.Equal(new Money(100_000), schedule[0].Capital); // €12,000 / 12 = €1,000
        Assert.Equal(Money.Zero, schedule[^1].ClosingBalance);
        var totalCapital = schedule.Aggregate(Money.Zero, (acc, row) => acc + row.Capital);
        Assert.Equal(new Money(1_200_000), totalCapital);
    }

    [Fact]
    public void OutstandingBalanceAfter_tracks_the_schedule_closing_balance()
    {
        // The closed-form balance (fin-math §7.4) should be close to the integer-cent schedule's closing
        // balance at the same point (they differ by at most the accumulated rounding the schedule absorbs).
        var principal = new Money(1_000_000);
        var schedule = Amortization.Schedule(principal, periodicRateBps: 50, periods: 12);

        var closedForm = Amortization.OutstandingBalanceAfter(principal, periodicRateBps: 50, periods: 12, paid: 3);
        var fromSchedule = schedule[2].ClosingBalance;

        Assert.True(Math.Abs(closedForm.Cents - fromSchedule.Cents) <= 5,
            $"closed-form {closedForm.Cents}c vs schedule {fromSchedule.Cents}c should agree within rounding");

        // After all installments, both are zero.
        Assert.Equal(Money.Zero, Amortization.OutstandingBalanceAfter(principal, 50, 12, 12));
    }

    [Fact]
    public void EarlyRepaymentCommission_caps_at_the_statutory_ceiling()
    {
        // The bank charges 0.50% (50 bps); the statutory cap for >1y remaining is also 50 bps ⇒ the
        // commission is 0.50% of the repaid capital. €5,000 repaid ⇒ €25.00 commission.
        var commission = Amortization.EarlyRepaymentCommission(
            capitalRepaid: new Money(500_000), commissionBps: 50, capBps: 50,
            lostInterestCeiling: new Money(1_000_000));
        Assert.Equal(new Money(2_500), commission); // €25.00
    }

    [Fact]
    public void EarlyRepaymentCommission_clamps_a_charged_rate_above_the_statutory_cap()
    {
        // A misconfigured product charging 1.00% (100 bps) is CLAMPED to the ≤1y statutory cap of 0.25%
        // (25 bps): €500,000 repaid × 0.25% = €1,250, NOT €5,000.
        var commission = Amortization.EarlyRepaymentCommission(
            capitalRepaid: new Money(50_000_000), commissionBps: 100, capBps: 25,
            lostInterestCeiling: new Money(100_000_000));
        Assert.Equal(new Money(125_000), commission); // €1,250.00 = 0.25% of €500,000
    }

    [Fact]
    public void EarlyRepaymentCommission_never_exceeds_the_lost_interest_ceiling()
    {
        // The §7.5 ceiling: the commission may never exceed the interest the borrower would still have
        // paid. A 0.50% commission on €500,000 (= €2,500) is clamped DOWN to a €1,000 lost-interest ceiling.
        var commission = Amortization.EarlyRepaymentCommission(
            capitalRepaid: new Money(50_000_000), commissionBps: 50, capBps: 50,
            lostInterestCeiling: new Money(100_000)); // €1,000 ceiling
        Assert.Equal(new Money(100_000), commission); // clamped to the €1,000 ceiling
    }
}
