namespace Babelstone.FinancialMath;

/// <summary>
/// Base-10 numeric primitives shared across the kernel. The single rule these exist to
/// uphold (ADR-PC-010 §P1–§P2): compounding and rate math stay in <see cref="decimal"/>,
/// never <see cref="double"/> — <see cref="System.Math.Pow"/> would route the computation
/// through binary floating point and silently drift the low cents. Every exponent the
/// kernel raises is a whole number of compounding periods, so an exact integer-power
/// algorithm suffices and no transcendental (fractional-exponent) power is offered.
/// </summary>
internal static class DecimalMath
{
    /// <summary>
    /// <paramref name="value"/> raised to a non-negative integer <paramref name="exponent"/>,
    /// by exponentiation by squaring — O(log n) multiplications, all in base-10
    /// <see cref="decimal"/>. <c>Pow(x, 0) == 1</c> for every <c>x</c> (including <c>0</c>),
    /// the empty-product convention the compounding formulas rely on for a zero-period term.
    /// </summary>
    /// <param name="value">The base; may be negative or below 1 (a negative or eroding rate).</param>
    /// <param name="exponent">The power; must be non-negative — the kernel never raises to a
    /// negative whole power (it divides by a positive power instead, keeping one code path).</param>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="exponent"/> is negative.</exception>
    internal static decimal Pow(decimal value, int exponent)
    {
        if (exponent < 0)
            throw new ArgumentOutOfRangeException(
                nameof(exponent), exponent, "Exponent must be non-negative; divide by a positive power instead.");

        decimal result = 1m;
        decimal factor = value;
        int e = exponent;
        while (e > 0)
        {
            if ((e & 1) == 1)
                result *= factor;
            e >>= 1;
            if (e > 0)
                factor *= factor;
        }
        return result;
    }
}
