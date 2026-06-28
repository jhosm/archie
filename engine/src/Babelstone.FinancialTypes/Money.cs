namespace Babelstone.FinancialTypes;

/// <summary>
/// EUR money as a signed-integer count of cents (ADR-PC-010). Cents is the
/// storage and arithmetic substrate; <see cref="decimal"/> is a boundary-only
/// computation type that enters exactly one place — <see cref="FromCents(decimal)"/> —
/// where it is rounded HALF_EVEN to cents (ADR-PC-010). The type intentionally exposes no
/// operator that returns <see cref="decimal"/> from <see cref="Money"/> inputs.
/// </summary>
public readonly record struct Money(long Cents)
{
    public static readonly Money Zero = new(0L);

    /// <summary>
    /// The single Decimal→Cents rounding boundary (ADR-PC-010): round the
    /// fully-computed decimal amount exactly once, HALF_EVEN. Callers compute the
    /// whole expression in full precision and only cross this boundary at the end —
    /// never round intermediate steps (accumulating roundings drifts).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">If the rounded amount falls outside the
    /// <see cref="long"/> cent range. A bare <c>(long)</c> cast already throws
    /// <see cref="OverflowException"/> here (decimal→integral is always range-checked), but
    /// that exception names neither the operand nor this boundary; this guard reports the
    /// offending value and where it overflowed, since accrual products can drive it.</exception>
    public static Money FromCents(decimal cents)
    {
        decimal rounded = Math.Round(cents, 0, MidpointRounding.ToEven);
        if (rounded < long.MinValue || rounded > long.MaxValue)
            throw new ArgumentOutOfRangeException(
                nameof(cents), cents, "Rounded amount is outside the Money (Int64) cent range.");
        return new((long)rounded);
    }

    public static Money operator +(Money a, Money b) => new(checked(a.Cents + b.Cents));

    public static Money operator -(Money a, Money b) => new(checked(a.Cents - b.Cents));

    public static Money operator -(Money a) => new(checked(-a.Cents));

    /// <summary>Read-only display/report projection (no money math) — euros as decimal.</summary>
    public decimal ToDecimal() => Cents / 100m;

    public override string ToString() =>
        (Cents / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}
