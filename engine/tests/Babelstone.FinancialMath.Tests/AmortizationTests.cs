using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.FinancialMath.Tests;

/// <summary>
/// The closed-end (personal-loan) French-system amortization kernel (fin-math §3–§4, §7.4–§7.5).
/// Pure, Docker-free, period-grid (no clock, no day-count). These tests are the B.10 mutation
/// backstop for <see cref="Amortization"/>: the family/decider suites never exercise the loan math
/// directly, so every boundary guard, the level-installment formula, the integer-cent schedule, the
/// closed-form outstanding balance, and the legally-capped early-repayment commission are pinned here.
/// The headline invariants — the schedule's capital legs sum to the principal to the cent and the
/// final balance clears to zero — hold by construction of the balancing final row, so the formula
/// itself is additionally pinned against EXACT HALF_EVEN cent fixtures (a wrong installment would
/// otherwise hide behind the self-balancing final row).
/// </summary>
public class AmortizationTests
{
    // Worked example A (fin-math §4.1): C = 1000.00, periodic rate 1.00%/period (100 bps), n = 12.
    private static readonly Money PrincipalA = new(100_000L);
    private const int RateBpsA = 100;
    private const int PeriodsA = 12;

    // --- LevelInstallment: the Price formula P = C·r / (1 − (1+r)^−n) -----------------------------

    [Fact]
    public void LevelInstallment_matches_the_Price_formula_to_the_cent()
    {
        // Computed in decimal at full precision, rounded ONCE HALF_EVEN at the Money boundary.
        Assert.Equal(new Money(8_885L), Amortization.LevelInstallment(PrincipalA, RateBpsA, PeriodsA));
    }

    [Fact]
    public void LevelInstallment_at_zero_rate_degenerates_to_principal_over_periods()
    {
        // r → 0 limit of the Price formula: P = C / n, no interest leg (a 0%-TAN promotional loan).
        Assert.Equal(new Money(10_000L), Amortization.LevelInstallment(new Money(120_000L), 0, 12));
    }

    // --- Schedule: n rows, exact integer-cent arithmetic, balancing final row --------------------

    [Fact]
    public void Schedule_has_one_row_per_period()
    {
        Assert.Equal(PeriodsA, Amortization.Schedule(PrincipalA, RateBpsA, PeriodsA).Count);
    }

    [Fact]
    public void Schedule_first_row_splits_the_level_installment_into_interest_and_capital()
    {
        var first = Amortization.Schedule(PrincipalA, RateBpsA, PeriodsA)[0];

        Assert.Equal(1, first.Period);
        Assert.Equal(PrincipalA, first.OpeningBalance);
        Assert.Equal(new Money(1_000L), first.Interest);        // J(1) = S0 · r = 100000 · 0.01
        Assert.Equal(new Money(7_885L), first.Capital);         // A(1) = P − J(1) = 8885 − 1000
        Assert.Equal(new Money(8_885L), first.Installment);     // the level installment
        Assert.Equal(new Money(92_115L), first.ClosingBalance); // S(1) = S0 − A(1)
    }

    [Fact]
    public void Schedule_final_row_is_the_balancing_row_and_clears_the_balance_exactly()
    {
        var rows = Amortization.Schedule(PrincipalA, RateBpsA, PeriodsA);
        var last = rows[^1];

        Assert.Equal(PeriodsA, last.Period);
        // The balancing row amortizes whatever capital remains and clears the balance to zero. Pinned to
        // concrete cents so the balancing arithmetic is actually checked (not a tautology): the final
        // installment is interest + the whole remaining opening balance — 88 + 8796 = 8884, which is NOT
        // the level installment (8885), absorbing the accumulated rounding drift.
        Assert.Equal(new Money(8_796L), last.OpeningBalance);
        Assert.Equal(new Money(88L), last.Interest);
        Assert.Equal(new Money(8_796L), last.Capital);     // capital == the whole remaining balance
        Assert.Equal(new Money(8_884L), last.Installment); // interest + opening, the balancing installment
        Assert.Equal(Money.Zero, last.ClosingBalance);
        Assert.NotEqual(Amortization.LevelInstallment(PrincipalA, RateBpsA, PeriodsA), last.Installment);
    }

    [Fact]
    public void Schedule_capital_legs_sum_to_the_principal_to_the_cent()
    {
        var rows = Amortization.Schedule(PrincipalA, RateBpsA, PeriodsA);

        var totalCapital = rows.Aggregate(Money.Zero, (acc, r) => acc + r.Capital);
        Assert.Equal(PrincipalA, totalCapital);
    }

    [Fact]
    public void Schedule_each_row_closes_at_opening_minus_capital_and_chains_to_the_next_opening()
    {
        var rows = Amortization.Schedule(PrincipalA, RateBpsA, PeriodsA);

        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Equal(rows[i].OpeningBalance - rows[i].Capital, rows[i].ClosingBalance);
            Assert.Equal(rows[i].Interest + rows[i].Capital, rows[i].Installment);
            if (i > 0)
                Assert.Equal(rows[i - 1].ClosingBalance, rows[i].OpeningBalance);
        }
    }

    [Fact]
    public void Schedule_at_zero_rate_amortizes_pure_capital_with_no_interest()
    {
        var rows = Amortization.Schedule(new Money(120_000L), 0, 12);

        Assert.All(rows, r => Assert.Equal(Money.Zero, r.Interest));
        Assert.All(rows, r => Assert.Equal(new Money(10_000L), r.Capital));
        Assert.Equal(Money.Zero, rows[^1].ClosingBalance);
    }

    // --- PeriodInterest: J = S · r ---------------------------------------------------------------

    [Fact]
    public void PeriodInterest_is_balance_times_periodic_rate_rounded_once()
    {
        Assert.Equal(new Money(1_000L), Amortization.PeriodInterest(new Money(100_000L), 100));
        Assert.Equal(Money.Zero, Amortization.PeriodInterest(new Money(100_000L), 0));
    }

    [Fact]
    public void PeriodInterest_rejects_a_negative_rate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Amortization.PeriodInterest(new Money(100_000L), -1));
    }

    // --- OutstandingBalanceAfter: the closed-form S(m) -------------------------------------------

    [Fact]
    public void OutstandingBalanceAfter_zero_payments_returns_the_full_principal()
    {
        Assert.Equal(PrincipalA, Amortization.OutstandingBalanceAfter(PrincipalA, RateBpsA, PeriodsA, 0));
    }

    [Fact]
    public void OutstandingBalanceAfter_all_payments_clears_the_balance()
    {
        Assert.Equal(Money.Zero, Amortization.OutstandingBalanceAfter(PrincipalA, RateBpsA, PeriodsA, PeriodsA));
    }

    [Fact]
    public void OutstandingBalanceAfter_partway_matches_the_closed_form_to_the_cent()
    {
        // S(3) of the §7.4 closed form C·(1+r)^m − P·[(1+r)^m − 1]/r.
        Assert.Equal(new Money(76_108L), Amortization.OutstandingBalanceAfter(PrincipalA, RateBpsA, PeriodsA, 3));
    }

    [Fact]
    public void OutstandingBalanceAfter_at_zero_rate_is_principal_minus_paid_capital()
    {
        // r → 0: each installment amortizes C/n; m of them leave C − m·(C/n). 120000 − 3·10000 = 90000.
        Assert.Equal(new Money(90_000L), Amortization.OutstandingBalanceAfter(new Money(120_000L), 0, 12, 3));
    }

    [Theory]
    [InlineData(-1)]   // paid < 0
    [InlineData(13)]   // paid > periods (n = 12)
    public void OutstandingBalanceAfter_rejects_a_paid_count_outside_zero_to_periods(int paid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Amortization.OutstandingBalanceAfter(PrincipalA, RateBpsA, PeriodsA, paid));
    }

    // --- Validation is enforced at EVERY public entry point (kills the per-method Validate removal) -

    public static TheoryData<string, Money, int, int> InvalidInputs() => new()
    {
        { "negative principal", new Money(-1L), 100, 12 },
        { "negative rate",      new Money(100_000L), -1, 12 },
        { "zero periods",       new Money(100_000L), 100, 0 },
        { "negative periods",   new Money(100_000L), 100, -1 },
    };

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public void LevelInstallment_validates_its_inputs(string _, Money principal, int rateBps, int periods)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Amortization.LevelInstallment(principal, rateBps, periods));
    }

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public void Schedule_validates_its_inputs(string _, Money principal, int rateBps, int periods)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Amortization.Schedule(principal, rateBps, periods));
    }

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public void OutstandingBalanceAfter_validates_its_inputs(string _, Money principal, int rateBps, int periods)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Amortization.OutstandingBalanceAfter(principal, rateBps, periods, 0));
    }

    // --- EarlyRepaymentCommission: min(charged, statutory) capped, then the §7.5 lost-interest ceiling -

    private static readonly Money NoCeiling = new(100_000_000L); // a value the commission never reaches

    [Fact]
    public void EarlyRepaymentCommission_charges_the_headline_rate_when_below_the_statutory_cap()
    {
        // charged 10 bps < cap 50 bps → 50000 · 0.0010 = 50.
        Assert.Equal(new Money(50L), Amortization.EarlyRepaymentCommission(new Money(50_000L), 10, 50, NoCeiling));
    }

    [Fact]
    public void EarlyRepaymentCommission_caps_the_charged_rate_at_the_statutory_ceiling()
    {
        // charged 50 bps > cap 25 bps → uses 25 bps: 50000 · 0.0025 = 125 (NOT 250 at the charged rate).
        Assert.Equal(new Money(125L), Amortization.EarlyRepaymentCommission(new Money(50_000L), 50, 25, NoCeiling));
    }

    [Fact]
    public void EarlyRepaymentCommission_is_clamped_down_to_the_lost_interest_ceiling()
    {
        // commission would be 125, but the borrower would only have paid 100 more in interest → 100.
        Assert.Equal(new Money(100L), Amortization.EarlyRepaymentCommission(new Money(50_000L), 50, 25, new Money(100L)));
    }

    [Fact]
    public void EarlyRepaymentCommission_keeps_the_commission_when_it_does_not_exceed_the_ceiling()
    {
        // commission 125 with the ceiling also 125 → the commission is NOT above the ceiling, so it
        // stands. (With the clamp test this pins the ceiling comparison; the exact > vs >= boundary AT
        // equality is an equivalent mutant — both directions return 125 — so it is not separately killable.)
        Assert.Equal(new Money(125L), Amortization.EarlyRepaymentCommission(new Money(50_000L), 50, 25, new Money(125L)));
    }

    [Theory]
    [InlineData(-1, 50, 25)]   // negative capital repaid
    [InlineData(50_000, -1, 25)] // negative commission rate
    [InlineData(50_000, 50, -1)] // negative statutory cap
    public void EarlyRepaymentCommission_rejects_negative_inputs(long capitalRepaidCents, int commissionBps, int statutoryCapBps)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Amortization.EarlyRepaymentCommission(new Money(capitalRepaidCents), commissionBps, statutoryCapBps, NoCeiling));
    }

    [Theory]
    [InlineData(0, 50, 25)]      // capitalRepaid == 0 is valid → zero commission
    [InlineData(50_000, 0, 25)]  // commissionBps == 0 is valid → zero commission
    [InlineData(50_000, 50, 0)]  // statutoryCapBps == 0 is valid → caps the commission to zero
    public void EarlyRepaymentCommission_accepts_zero_inputs_as_a_zero_commission(long capitalRepaidCents, int commissionBps, int statutoryCapBps)
    {
        // The guards reject NEGATIVE inputs, not non-positive: zero capital, a zero charged rate, or a
        // zero statutory cap are all legal and each yields a zero commission — never an exception. (Pins
        // the < 0 boundary so a < 0 → <= 0 mutant, which would wrongly throw on a valid 0, is killed.)
        Assert.Equal(Money.Zero,
            Amortization.EarlyRepaymentCommission(new Money(capitalRepaidCents), commissionBps, statutoryCapBps, NoCeiling));
    }
}
