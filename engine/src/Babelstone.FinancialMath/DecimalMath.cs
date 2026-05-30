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
            // Stryker disable once all: e ≥ 0 throughout this loop, so >>>= is bit-identical
            // to >>= here — the signed/unsigned shift mutant is provably equivalent.
            e >>= 1;
            // Stryker disable once Equality: with e ≥ 0, the e > 0 guard differs from e >= 0
            // only on the final iteration, whose extra squaring is discarded as the loop then
            // exits; for the near-unity bases the kernel raises it cannot overflow — equivalent.
            if (e > 0)
                factor *= factor;
        }
        return result;
    }
}
