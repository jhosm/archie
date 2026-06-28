using Babelstone.FinancialTypes;

namespace Babelstone.FinancialMath;

/// <summary>
/// One row of a French-system (constant-installment) amortization
/// schedule — the amortization table a closed-end loan repays against (fin-math §3, §4.1).
/// Every monetary leg is integer cents (<see cref="Money"/>, ADR-PC-010 §P1): the period interest
/// <see cref="Interest"/>, the capital amortized <see cref="Capital"/>, the level installment
/// <see cref="Installment"/> (Interest + Capital), and the <see cref="ClosingBalance"/> left after it.
/// </summary>
/// <param name="Period">The 1-based installment number (1 … n).</param>
/// <param name="OpeningBalance">Outstanding capital at the START of the period (<c>S(t-1)</c>).</param>
/// <param name="Interest">Interest for the period: <c>J(t) = S(t-1) × r</c> (fin-math §3).</param>
/// <param name="Capital">Capital amortized in the period: <c>A(t) = P − J(t)</c> (fin-math §3).</param>
/// <param name="Installment">The period's installment <c>P(t) = J(t) + A(t)</c> — level under Price,
/// except the final row, which is adjusted to clear the balance exactly (see <see cref="Amortization"/>).</param>
/// <param name="ClosingBalance">Outstanding capital at the END of the period: <c>S(t) = S(t-1) − A(t)</c>.</param>
public sealed record AmortizationRow(
    int Period,
    Money OpeningBalance,
    Money Interest,
    Money Capital,
    Money Installment,
    Money ClosingBalance);

/// <summary>
/// Pure constant-installment (French / Price) amortization primitives (fin-math §3–§4, §7.4–§7.5).
/// A closed-end personal loan (<c>personal_loan</c>) repays a fixed principal in <c>n</c> equal
/// installments at a periodic rate <c>r = TAN / m</c> (the PT proportional-rate convention,
/// fin-math §2.2): the headline installment is constant, each one splitting into a shrinking
/// interest leg and a growing capital leg until the balance reaches zero (fin-math §4.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Rounding discipline (ADR-PC-010 §P1–§P2).</b> The level installment is computed in
/// <see cref="decimal"/> at full precision and crossed to <see cref="Money"/> exactly once via
/// <see cref="Money.FromCents(decimal)"/> (HALF_EVEN). The schedule is then built by EXACT integer-cent
/// arithmetic off that one rounded installment: each period's interest is rounded once, capital is the
/// residual <c>installment − interest</c> (no second rounding), and the balance is reduced by that
/// integer capital. This conserves cents period-to-period — a schedule never drifts, and the sum of the
/// capital legs equals the principal to the cent. The <b>final</b> installment is adjusted to whatever
/// clears the remaining balance exactly (<c>capital = openingBalance</c>, <c>installment = interest +
/// openingBalance</c>), absorbing the accumulated penny rounding — the standard amortization-table convention.
/// </para>
/// <para>
/// <b>Periodic rate, not annual.</b> Callers pass the PERIODIC rate in basis points
/// (<c>periodicRateBps = TAN_bps / m</c>, e.g. a 6% TAN with monthly installments is
/// <c>600 / 12 = 50</c> bps). The PT retail-credit convention is the PROPORTIONAL rate (fin-math §2.2),
/// so the caller does the <c>TAN / m</c> division at the rate level; this kernel never sees an annual
/// TAN or a day-count — a closed-end loan amortizes on a period grid, not on actual days.
/// </para>
/// <para>
/// <b>No clock, no I/O (ADR-PC-010 §P5).</b> Every input is explicit; the schedule is a deterministic
/// function of <c>(principal, periodicRateBps, periods)</c> and rebuilds byte-identically on replay.
/// A zero periodic rate is supported (a 0%-TAN promotional loan): the installment degenerates to
/// <c>principal / n</c> with no interest leg.
/// </para>
/// </remarks>
public static class Amortization
{
    // The per-unit basis-point scale (100% = 10,000 bps) is the shared kernel constant
    // Rate.BasisPointsPerUnit — the same scale Accrual uses; promoted to
    // decimal inside each boundary expression. Kept as int (BMNY002 bans stored decimal state, §P1).

    /// <summary>
    /// The level (constant) installment of a French-system loan (fin-math §4.1):
    /// <c>P = C × r / (1 − (1 + r)^−n)</c>, with the periodic rate <c>r = periodicRateBps / 10000</c>.
    /// Computed wholly in <see cref="decimal"/> (the <c>(1 + r)^−n</c> term is
    /// <c>1 / DecimalMath.Pow(1 + r, n)</c> — integer-power decimal, never <see cref="Math.Pow"/>) and
    /// rounded ONCE at the <see cref="Money"/> boundary. A zero rate degenerates to <c>C / n</c> (no
    /// interest), the limit of the Price formula as <c>r → 0</c>.
    /// </summary>
    /// <param name="principal">The disbursed capital <c>C</c> (must be ≥ 0).</param>
    /// <param name="periodicRateBps">The PERIODIC rate <c>r</c> in basis points (<c>TAN_bps / m</c>);
    /// must be ≥ 0 — a loan is never priced at a negative rate.</param>
    /// <param name="periods">The number of installments <c>n</c> (must be ≥ 1).</param>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="periods"/> &lt; 1,
    /// <paramref name="periodicRateBps"/> &lt; 0, or <paramref name="principal"/> is negative.</exception>
    public static Money LevelInstallment(Money principal, int periodicRateBps, int periods)
    {
        Validate(principal, periodicRateBps, periods);

        if (periodicRateBps == 0)
        {
            // r → 0 limit of the Price formula: P = C / n (pure capital, no interest).
            return Money.FromCents((decimal)principal.Cents / periods);
        }

        decimal r = periodicRateBps / (decimal)Rate.BasisPointsPerUnit;
        // discount = 1 − (1 + r)^−n = 1 − 1 / (1 + r)^n. Integer-power decimal (DecimalMath.Pow),
        // never Math.Pow — money math stays out of binary double (ADR-PC-010 §P1).
        decimal growth = DecimalMath.Pow(1m + r, periods);
        decimal discount = 1m - (1m / growth);
        decimal installment = (decimal)principal.Cents * r / discount;
        return Money.FromCents(installment);
    }

    /// <summary>
    /// The full French-system amortization schedule (fin-math §4.1):
    /// <c>n</c> rows, each carrying the opening balance, the period interest <c>J(t) = S(t-1) × r</c>,
    /// the capital amortized <c>A(t) = P − J(t)</c>, the installment, and the closing balance
    /// <c>S(t) = S(t-1) − A(t)</c>. The headline installment is <see cref="LevelInstallment"/>; the
    /// schedule is built by EXACT integer-cent arithmetic off that one rounded figure (see the type
    /// remarks), so the capital legs sum to the principal to the cent and <c>S(n) = 0</c> exactly.
    /// </summary>
    /// <remarks>
    /// The FINAL row is the balancing row: rather than re-apply the level installment (which would
    /// leave a few stray cents from accumulated rounding), it amortizes whatever capital remains
    /// (<c>capital = openingBalance</c>) and its installment is <c>interest + openingBalance</c>. This
    /// is the universal amortization-table convention and the reason the schedule conserves to zero.
    /// </remarks>
    /// <param name="principal">The disbursed capital <c>C</c>.</param>
    /// <param name="periodicRateBps">The PERIODIC rate <c>r</c> in basis points (<c>TAN_bps / m</c>).</param>
    /// <param name="periods">The number of installments <c>n</c>.</param>
    public static IReadOnlyList<AmortizationRow> Schedule(Money principal, int periodicRateBps, int periods)
    {
        Validate(principal, periodicRateBps, periods);

        var installment = LevelInstallment(principal, periodicRateBps, periods);
        var rows = new List<AmortizationRow>(periods);

        var balance = principal;
        for (var period = 1; period <= periods; period++)
        {
            var opening = balance;

            Money interest;
            Money capital;
            Money payment;

            if (period == periods)
            {
                // Balancing final row: clear the residual balance exactly. interest accrues on the
                // opening balance as usual; capital is the whole remaining balance; the installment
                // is whatever pays both — absorbing the accumulated penny rounding so S(n) = 0.
                interest = PeriodInterest(opening, periodicRateBps);
                capital = opening;
                payment = interest + capital;
            }
            else
            {
                interest = PeriodInterest(opening, periodicRateBps);
                // Capital is the residual of the LEVEL installment minus interest — one rounding
                // (the interest), then exact integer subtraction (no second rounding, ADR-PC-010 §P2).
                capital = installment - interest;
                payment = installment;
            }

            var closing = opening - capital;
            rows.Add(new AmortizationRow(period, opening, interest, capital, payment, closing));
            balance = closing;
        }

        return rows;
    }

    /// <summary>
    /// The interest for one period on an outstanding balance: <c>J = S × r</c> (fin-math §3), with
    /// <c>r = periodicRateBps / 10000</c>, rounded once at the <see cref="Money"/> boundary. The
    /// per-row interest leg the schedule and a partial early repayment both use.
    /// </summary>
    /// <param name="outstandingBalance">The capital the interest accrues on (<c>S(t-1)</c>).</param>
    /// <param name="periodicRateBps">The PERIODIC rate in basis points.</param>
    public static Money PeriodInterest(Money outstandingBalance, int periodicRateBps)
    {
        if (periodicRateBps < 0)
            throw new ArgumentOutOfRangeException(
                nameof(periodicRateBps), periodicRateBps, "Periodic rate must be non-negative.");

        decimal interest = (decimal)outstandingBalance.Cents * periodicRateBps / Rate.BasisPointsPerUnit;
        return Money.FromCents(interest);
    }

    /// <summary>
    /// The outstanding capital after <paramref name="paid"/> installments of a French-system loan
    /// (the early-repayment / variable-rate-reset balance, fin-math §7.4):
    /// <c>S(m) = C × (1 + r)^m − P × [(1 + r)^m − 1] / r</c>, where <c>P</c> is the level installment.
    /// This is the CLOSED-FORM balance from the amortization formula. v1 derives the same figure from
    /// the integer-cent <see cref="Schedule"/> instead (which conserves to the cent and absorbs rounding
    /// in the final row) — this method is the analytic cross-check. A zero rate degenerates to
    /// <c>C − m × (C / n)</c>.
    /// </summary>
    /// <param name="principal">The original disbursed capital <c>C</c>.</param>
    /// <param name="periodicRateBps">The PERIODIC rate in basis points.</param>
    /// <param name="periods">The total number of installments <c>n</c>.</param>
    /// <param name="paid">The number of installments already paid <c>m</c> (0 ≤ m ≤ n).</param>
    public static Money OutstandingBalanceAfter(Money principal, int periodicRateBps, int periods, int paid)
    {
        Validate(principal, periodicRateBps, periods);
        if (paid < 0 || paid > periods)
            throw new ArgumentOutOfRangeException(
                nameof(paid), paid, "Paid-installment count must be in [0, periods].");

        if (paid == 0)
        {
            return principal;
        }

        if (periodicRateBps == 0)
        {
            // r → 0: each installment amortizes C / n of capital; m of them leave C − m·(C/n).
            decimal perPeriodCapital = (decimal)principal.Cents / periods;
            return Money.FromCents((decimal)principal.Cents - (paid * perPeriodCapital));
        }

        decimal r = periodicRateBps / (decimal)Rate.BasisPointsPerUnit;
        // Use the UN-rounded level installment here so the closed form is internally consistent in
        // full precision; the single rounding is at the final Money boundary (ADR-PC-010 §P2).
        decimal growthN = DecimalMath.Pow(1m + r, periods);
        decimal levelInstallment = (decimal)principal.Cents * r / (1m - (1m / growthN));

        decimal growthM = DecimalMath.Pow(1m + r, paid);
        decimal balance = ((decimal)principal.Cents * growthM) - (levelInstallment * (growthM - 1m) / r);
        return Money.FromCents(balance);
    }

    /// <summary>
    /// The legally-capped early-repayment commission (fin-math
    /// §7.5; PT Decreto-Lei n.º 133/2009 art. 19, consumer credit): a percentage of the capital repaid,
    /// CAPPED at the statutory ceiling, and additionally never exceeding the interest the borrower would
    /// otherwise have paid over the remaining term (the §7.5 ceiling). The borrower pays back the capital
    /// repaid PLUS this commission; the rest of the schedule is settled.
    /// </summary>
    /// <remarks>
    /// The PT consumer-credit statutory ceiling is rate-by-remaining-term: <b>0.50%</b> of the capital
    /// repaid when more than one year of term remains, <b>0.25%</b> when one year or less remains
    /// (research/personal-loan/02 §2). Both the headline percentage the product charges and the
    /// statutory ceiling are passed in basis points so the policy/pack owns the numbers and this kernel
    /// stays declarative: the commission is <c>min(charged_bps, ceiling_bps) × capitalRepaid</c>, then
    /// floored to the lost-interest ceiling. Computed in <see cref="decimal"/>, rounded ONCE at the
    /// <see cref="Money"/> boundary. Pure — no clock, no I/O.
    /// </remarks>
    /// <param name="capitalRepaid">The capital being repaid early (the outstanding balance for a full
    /// settlement, or the partial amount for a partial repayment).</param>
    /// <param name="commissionBps">The commission the product charges, in basis points (e.g. 50 = 0.50%).</param>
    /// <param name="capBps">The ceiling in basis points the commission may never exceed — a policy/regulatory
    /// cap the caller supplies (e.g. the PT consumer-credit statutory ceiling: 50 = 0.50% when &gt;1y of term
    /// remains, 25 = 0.25% when ≤1y), so any family or jurisdiction passes its own cap.</param>
    /// <param name="lostInterestCeiling">The interest the borrower would still have paid over the remaining
    /// term — the §7.5 absolute ceiling: the commission may never exceed it. Pass the remaining-schedule
    /// interest sum; <see cref="Money.Zero"/> would force a zero commission (use a large value to disable).</param>
    /// <exception cref="ArgumentOutOfRangeException">If any rate is negative, or <paramref name="capitalRepaid"/>
    /// is negative.</exception>
    public static Money EarlyRepaymentCommission(
        Money capitalRepaid, int commissionBps, int capBps, Money lostInterestCeiling)
    {
        if (capitalRepaid.Cents < 0)
            throw new ArgumentOutOfRangeException(
                nameof(capitalRepaid), capitalRepaid.Cents, "Capital repaid must be non-negative.");
        if (commissionBps < 0)
            throw new ArgumentOutOfRangeException(
                nameof(commissionBps), commissionBps, "Commission rate must be non-negative.");
        if (capBps < 0)
            throw new ArgumentOutOfRangeException(
                nameof(capBps), capBps, "Rate cap must be non-negative.");

        // 1. Cap the charged rate at the supplied ceiling — the product may charge LESS, never more.
        var effectiveBps = Math.Min(commissionBps, capBps);

        // 2. commission = capitalRepaid × effectiveBps / 10000, rounded once at the Money boundary.
        decimal commissionRaw = (decimal)capitalRepaid.Cents * effectiveBps / Rate.BasisPointsPerUnit;
        var commission = Money.FromCents(commissionRaw);

        // 3. The lost-interest ceiling (§7.5): the commission may never exceed the interest the borrower
        //    would still have paid. A commission above it is clamped DOWN to it (never up).
        return commission.Cents > lostInterestCeiling.Cents ? lostInterestCeiling : commission;
    }

    private static void Validate(Money principal, int periodicRateBps, int periods)
    {
        if (principal.Cents < 0)
            throw new ArgumentOutOfRangeException(
                nameof(principal), principal.Cents, "Principal must be non-negative.");
        if (periodicRateBps < 0)
            throw new ArgumentOutOfRangeException(
                nameof(periodicRateBps), periodicRateBps, "Periodic rate must be non-negative.");
        if (periods < 1)
            throw new ArgumentOutOfRangeException(
                nameof(periods), periods, "A schedule must have at least one installment.");
    }
}
