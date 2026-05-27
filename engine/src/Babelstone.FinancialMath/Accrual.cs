using Babelstone.FinancialTypes;

namespace Babelstone.FinancialMath;

/// <summary>
/// Pure interest-accrual primitives (fin-math §5, §8). All three modes compute the whole
/// amount in <see cref="decimal"/> at full precision and cross to <see cref="Money"/>
/// exactly once, via <see cref="Money.FromCents(decimal)"/> (ADR-PC-010 §P1–§P2). Rates are
/// integer basis points — 1% = 100 bps, so the PT default TAN 6% is <c>600</c>. No clock,
/// no I/O: every time input is an explicit day count (§P5).
/// </summary>
/// <remarks>
/// <c>rateBps</c> may be negative <b>by design</b>: a negative TAN (negative-rate
/// environments — the ECB deposit facility was negative 2014–2022) yields negative
/// interest, and these primitives emit it rather than reject it. The guards cover only
/// the <i>time</i> dimension — non-negative days/periods, positive basis/frequency — never
/// the rate sign. (Withholding, by contrast, bounds its rate to a tax fraction in [0, 1].)
/// </remarks>
public static class Accrual
{
    // 100% = 10,000 bps. Kept as int (not a decimal field — BMNY002 bans stored decimal
    // state per ADR-PC-010 §P1); it promotes to decimal inside each boundary expression.
    private const int BasisPointsPerUnit = 10_000;

    /// <summary>
    /// Simple interest (fin-math §5.1; the PT term-deposit default). Implements the
    /// ADR-PC-010 §P1 accrual form exactly:
    /// <c>interest = principal_cents × rate_bps × Days / (Basis × 10000)</c>.
    /// </summary>
    /// <param name="principal">Capital the interest accrues on.</param>
    /// <param name="rateBps">Annual nominal rate (TAN) in basis points.</param>
    /// <param name="factor">Day-count <see cref="DayCountFactor"/> from <see cref="DayCount.Between"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">If the day count is negative (a reversed
    /// interval) — accrual must never emit negative interest from swapped dates
    /// (carried obligation from the B.2 review).</exception>
    public static Money SimpleInterest(Money principal, int rateBps, DayCountFactor factor)
    {
        RequireForwardInterval(factor);

        decimal interest = (decimal)principal.Cents * rateBps * factor.Days
                         / ((decimal)factor.Basis * BasisPointsPerUnit);
        return Money.FromCents(interest);
    }

    /// <summary>
    /// Compound maturity value (fin-math §5.2): <c>M = C × (1 + TAN/m)^(m·n)</c>, with the
    /// periodic rate <c>r = rateBps / (periodsPerYear × 10000)</c> applied over
    /// <paramref name="totalPeriods"/> compounding periods. The integer-exponent power is
    /// computed by <see cref="decimal"/> exponentiation-by-squaring (<see cref="DecimalMath.Pow"/>),
    /// never <see cref="Math.Pow"/>, which would route money math through binary <c>double</c>.
    /// Rounds once at the boundary.
    /// </summary>
    /// <param name="principal">Initial capital C.</param>
    /// <param name="rateBps">Annual nominal rate (TAN) in basis points.</param>
    /// <param name="periodsPerYear">Compounding frequency m (e.g. 12 for monthly).</param>
    /// <param name="totalPeriods">Number of compounding periods m·n.</param>
    public static Money CompoundMaturity(Money principal, int rateBps, int periodsPerYear, int totalPeriods)
    {
        if (periodsPerYear <= 0)
            throw new ArgumentOutOfRangeException(nameof(periodsPerYear), periodsPerYear, "Compounding frequency must be positive.");
        if (totalPeriods < 0)
            throw new ArgumentOutOfRangeException(nameof(totalPeriods), totalPeriods, "Period count must be non-negative.");

        // (decimal) cast is load-bearing: without it periodsPerYear * BasisPointsPerUnit is
        // an int and rateBps / int would be integer division (600 / 120000 = 0).
        decimal periodicRate = rateBps / (periodsPerYear * (decimal)BasisPointsPerUnit);
        decimal growth = DecimalMath.Pow(1m + periodicRate, totalPeriods);
        return Money.FromCents((decimal)principal.Cents * growth);
    }

    /// <summary>
    /// Compound interest earned: <see cref="CompoundMaturity"/> − principal (fin-math §5.2).
    /// </summary>
    public static Money CompoundInterest(Money principal, int rateBps, int periodsPerYear, int totalPeriods) =>
        CompoundMaturity(principal, rateBps, periodsPerYear, totalPeriods) - principal;

    /// <summary>
    /// Interest on the sum of daily balances (fin-math §8.2): <c>J = (rate/basis) × Σ S(d)</c>,
    /// where <c>Σ S(d)</c> (the "number of capitals") is the sum of each interval's balance
    /// weighted by its day count. The whole numerator is accumulated in <see cref="decimal"/>
    /// and rounded once. Used for demand-deposit / revolving accrual where the balance is a
    /// step function over the period (§8.1).
    /// </summary>
    /// <param name="intervals">(balance held, days held) pairs covering the period.</param>
    /// <param name="rateBps">Annual nominal rate (TAN) in basis points.</param>
    /// <param name="basis">Days-in-year denominator (360 or 365) — the day-count basis.</param>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="basis"/> is not positive
    /// or any interval has a negative day count.</exception>
    public static Money DailyBalanceInterest(
        IEnumerable<(Money Balance, int Days)> intervals, int rateBps, int basis)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        if (basis <= 0)
            throw new ArgumentOutOfRangeException(nameof(basis), basis, "Day-count basis must be positive.");

        decimal numberOfCapitals = 0m; // Σ (balance_cents × days), in cents·days
        foreach (var (balance, days) in intervals)
        {
            if (days < 0)
                throw new ArgumentOutOfRangeException(nameof(intervals), days, "Interval day count must be non-negative.");
            numberOfCapitals += (decimal)balance.Cents * days;
        }

        decimal interest = rateBps * numberOfCapitals / (basis * BasisPointsPerUnit);
        return Money.FromCents(interest);
    }

    private static void RequireForwardInterval(DayCountFactor factor)
    {
        if (factor.Days < 0)
            throw new ArgumentOutOfRangeException(
                nameof(factor), factor.Days, "Day count is negative (reversed interval); accrual requires start ≤ end.");
    }
}
