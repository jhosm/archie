namespace Babelstone.FinancialMath;

/// <summary>
/// Day-count conventions (fin-math §2.2 — one of the three variable dimensions).
/// Pack-parameterised: a regulatory pack selects the convention; the kernel never
/// hardcodes it. Each convention resolves to the <c>(Days, Basis)</c> pair that
/// ADR-PC-010 §P1's accrual formula multiplies by —
/// <c>principal_cents × rate_bps × Days / (Basis × 10000)</c>.
/// </summary>
public enum DayCountConvention
{
    /// <summary>Actual days elapsed over a 360-day year. The PT term-deposit default
    /// (fin-math §5.1) — over a 365-day year it yields a factor &gt; 1.</summary>
    Act360,

    /// <summary>Actual days elapsed over a 365-day year.</summary>
    Act365,

    /// <summary>30E/360 (European): each month counted as 30 days, the year as 360,
    /// with both day-of-month figures capped at 30. The plain "30/360" of fin-math
    /// §2.2 / §9; the European variant carries no US end-of-month or February
    /// special-casing.</summary>
    Thirty360European,
}

/// <summary>
/// The <c>(Days, Basis)</c> an accrual multiplies by:
/// <c>interest = principal × rate × Days / Basis</c>. Both are integers by design
/// (ADR-PC-010 §P1) — the year fraction is never materialised as a <c>decimal</c>, so
/// rounding happens exactly once, downstream, at the Money boundary (§P2).
/// </summary>
/// <param name="Days">Day count under the chosen convention. May be zero (equal dates)
/// or negative (reversed interval) — the primitive is total and additive over
/// adjacent intervals; accrual callers pass <c>start ≤ end</c>.</param>
/// <param name="Basis">Days-in-year denominator: 360 or 365.</param>
public readonly record struct DayCountFactor(int Days, int Basis);

/// <summary>
/// Pure day-count primitives (fin-math §2.2). No clock, no I/O — dates are explicit
/// inputs, satisfying the determinism discipline (ADR-PC-010 §P5).
/// </summary>
public static class DayCount
{
    /// <summary>
    /// The <see cref="DayCountFactor"/> between two dates under <paramref name="convention"/>.
    /// </summary>
    public static DayCountFactor Between(DateOnly start, DateOnly end, DayCountConvention convention) =>
        convention switch
        {
            DayCountConvention.Act360 => new DayCountFactor(ActualDays(start, end), 360),
            DayCountConvention.Act365 => new DayCountFactor(ActualDays(start, end), 365),
            DayCountConvention.Thirty360European => new DayCountFactor(Thirty360EuropeanDays(start, end), 360),
            _ => throw new ArgumentOutOfRangeException(
                nameof(convention), convention, "Unknown day-count convention."),
        };

    /// <summary>Calendar days between dates: <c>end − start</c> via the proleptic
    /// Gregorian day number, so leap days and month lengths are counted exactly.</summary>
    private static int ActualDays(DateOnly start, DateOnly end) => end.DayNumber - start.DayNumber;

    /// <summary>30E/360 (European): <c>360·(Y2−Y1) + 30·(M2−M1) + (min(D2,30) − min(D1,30))</c>.
    /// Capping both day-of-month figures at 30 is the whole rule — no US-style
    /// 31st→30th adjustment chain, no February end-of-month exception.</summary>
    private static int Thirty360EuropeanDays(DateOnly start, DateOnly end)
    {
        int d1 = Math.Min(start.Day, 30);
        int d2 = Math.Min(end.Day, 30);
        return 360 * (end.Year - start.Year)
             + 30 * (end.Month - start.Month)
             + (d2 - d1);
    }
}
