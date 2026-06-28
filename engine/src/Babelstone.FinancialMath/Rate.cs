namespace Babelstone.FinancialMath;

/// <summary>
/// Shared rate primitives — the one place the kernel keeps its per-unit basis-point scale.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Interest rates throughout the kernel are integer BASIS POINTS:
/// 1% = 100 bps, so 100% = 10,000 bps. Turning a basis-point rate into a per-unit fraction
/// means dividing by that 10,000 scale — a constant that was previously hand-declared once per
/// kernel file (<see cref="Accrual"/>, <see cref="Amortization"/>, <see cref="Rates"/>,
/// <see cref="Withholding"/>) and re-declared again as a local inside the personal-loan decider.
/// Promoting it to a SINGLE shared constant removes that duplication, so the conversion scale is
/// stated once and consumed everywhere.
/// </para>
/// <para>
/// <b>Kept as <see cref="int"/>, not a stored <see cref="decimal"/> field</b> — the ADR-PC-010
/// boundary discipline (BMNY002) bans stored decimal money state; the constant promotes to
/// <see cref="decimal"/> inside each boundary expression. <see cref="ScaledByBasisPoints"/> is the
/// shared, UN-ROUNDED numerator helper for callers (such as the decider) that must fold the result
/// into a larger single-rounding expression rather than round it per step (ADR-PC-010).
/// </para>
/// </remarks>
public static class Rate
{
    /// <summary>
    /// The per-unit basis-point scale: 100% = 10,000 bps, so dividing a basis-point rate by this
    /// yields the per-unit fraction (e.g. 600 bps / 10,000 = 0.06). The single shared definition
    /// every accrual/amortization/withholding/effective-rate primitive — and the loan decider —
    /// scales by.
    /// </summary>
    public const int BasisPointsPerUnit = 10_000;

    /// <summary>
    /// The UN-ROUNDED numerator <c>cents × rateBps / 10000</c>, computed wholly in
    /// <see cref="decimal"/> and NOT crossed to a money boundary. This is the per-period interest
    /// leg in full precision: callers that must multiply it by a further factor (e.g. a remaining-
    /// installment count) before the single rounding fold it into one expression and cross to money
    /// exactly once themselves (ADR-PC-010) — rounding here and then multiplying would be the
    /// "round each step then combine" shape ADR-PC-010 forbids.
    /// </summary>
    /// <param name="cents">The capital the rate applies to, in integer cents.</param>
    /// <param name="rateBps">The rate in basis points.</param>
    public static decimal ScaledByBasisPoints(long cents, int rateBps) =>
        (decimal)cents * rateBps / BasisPointsPerUnit;
}
