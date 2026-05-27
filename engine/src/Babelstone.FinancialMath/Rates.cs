namespace Babelstone.FinancialMath;

/// <summary>
/// Effective-rate metrics (fin-math §5.4, §6). These return a <b>rate</b>, not
/// <see cref="FinancialTypes.Money"/>: a rate is a dimensionless per-unit fraction
/// (<c>0.0617</c> = 6.17%), so it sits outside the §P1–§P2 cents discipline — that
/// discipline governs money <i>amounts</i>, which is why the boundary analysers guard
/// stored <c>decimal</c> state but not a computed rate. The fraction is returned at full
/// <see cref="decimal"/> precision; rounding to a reported figure (DL 133/2009 publishes
/// the TAEG to one decimal place of a percentage) is a presentation concern left to the
/// caller. Pure: rates in basis points, no clock, no I/O (§P5).
/// </summary>
/// <remarks>
/// <c>tanBps</c> may be negative, consistently with <see cref="Accrual"/>: a negative
/// nominal rate annualises to a negative effective rate rather than being rejected. The
/// guard is on the <i>frequency</i> dimension (<c>periodsPerYear</c> must be positive),
/// never the rate sign.
/// </remarks>
public static class Rates
{
    private const int BasisPointsPerUnit = 10_000;

    /// <summary>
    /// Annual effective rate of a periodic rate compounded <paramref name="periodsPerYear"/>
    /// times a year: <c>(1 + r)^m − 1</c> (fin-math §5.4). This is the one annualisation
    /// identity behind both metrics in this class — <see cref="Tae"/> feeds it a nominal
    /// rate split into periods, and the TAEG feeds it a solved per-period IRR — so the
    /// conversion lives in exactly one place.
    /// </summary>
    /// <param name="periodicRate">The rate earned each period, as a per-unit fraction.</param>
    /// <param name="periodsPerYear">Compounding frequency m (e.g. 12 for monthly).</param>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="periodsPerYear"/> is not positive.</exception>
    public static decimal Annualize(decimal periodicRate, int periodsPerYear)
    {
        if (periodsPerYear <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(periodsPerYear), periodsPerYear, "Compounding frequency must be positive.");

        return DecimalMath.Pow(1m + periodicRate, periodsPerYear) - 1m;
    }

    /// <summary>
    /// TAE — effective annual rate of a nominal rate (TAN) compounded
    /// <paramref name="periodsPerYear"/> times a year (fin-math §5.4):
    /// <c>TAE = (1 + TAN/m)^m − 1</c>. The §5.4 worked example: TAN 6% monthly →
    /// <c>(1 + 0.06/12)^12 − 1 ≈ 0.061678</c> (6.17%). For interest paid at maturity with no
    /// intra-period capitalisation (<c>m = 1</c>) this reduces to <c>TAE = TAN</c>; the gap
    /// over TAN is the compounding effect and widens with m.
    /// </summary>
    /// <param name="tanBps">Nominal annual rate (TAN) in basis points (600 = 6%).</param>
    /// <param name="periodsPerYear">Compounding frequency m.</param>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="periodsPerYear"/> is not positive.</exception>
    public static decimal Tae(int tanBps, int periodsPerYear)
    {
        if (periodsPerYear <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(periodsPerYear), periodsPerYear, "Compounding frequency must be positive.");

        // (decimal) cast is load-bearing: periodsPerYear * BasisPointsPerUnit is an int, so
        // tanBps / that would be integer division (600 / 120000 = 0) without it.
        decimal periodicRate = tanBps / (periodsPerYear * (decimal)BasisPointsPerUnit);
        return Annualize(periodicRate, periodsPerYear);
    }
}
