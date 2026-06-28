using Babelstone.FinancialTypes;

namespace Babelstone.FinancialMath;

/// <summary>
/// The split of a gross interest flow into withheld tax and net paid (fin-math §5.4).
/// <see cref="Net"/> is computed as the residual <c>Gross − Tax</c>, so
/// <c>Net + Tax == Gross</c> holds exactly to the cent — there is no independent rounding
/// of the net leg that could open a one-cent gap.
/// </summary>
public readonly record struct WithholdingResult(Money Gross, Money Tax, Money Net);

/// <summary>
/// Withholding-tax primitive (fin-math §5.4). Withholding is applied <b>flow-by-flow, to
/// each interest payment as it accrues</b> — never by scaling the rate. The rate-level
/// shortcut <c>TANL = TANB × (1 − 0.28)</c> is exact only for a single-period at-maturity
/// deposit; for multi-period compound deposits the realized net return must be computed
/// per flow (§5.4). This primitive takes a <see cref="Money"/> interest <i>flow</i> and a
/// rate, never a rate to scale, so the wrong path is unrepresentable at the type level.
/// Pure; the pack supplies the rate (PT IRS withholding = 2800 bps).
/// </summary>
public static class Withholding
{
    // The per-unit basis-point scale (100% = 10,000 bps) is the shared kernel constant
    // Rate.BasisPointsPerUnit — int, not a decimal field (BMNY002 / ADR-PC-010); it
    // promotes to decimal at the boundary.

    /// <summary>
    /// Withhold tax from one gross interest flow. Tax is rounded once at the boundary
    /// (HALF_EVEN); net is the residual, conserving cents.
    /// </summary>
    /// <param name="grossInterest">The gross interest flow this payment accrues.</param>
    /// <param name="withholdingRateBps">Withholding rate in basis points (PT IRS = 2800).</param>
    /// <exception cref="ArgumentOutOfRangeException">If the rate is outside [0, 10000].</exception>
    public static WithholdingResult Withhold(Money grossInterest, int withholdingRateBps)
    {
        if (withholdingRateBps < 0 || withholdingRateBps > Rate.BasisPointsPerUnit)
            throw new ArgumentOutOfRangeException(
                nameof(withholdingRateBps), withholdingRateBps, "Withholding rate must be within [0, 10000] bps.");

        Money tax = Money.FromCents((decimal)grossInterest.Cents * withholdingRateBps / Rate.BasisPointsPerUnit);
        Money net = grossInterest - tax;
        return new WithholdingResult(grossInterest, tax, net);
    }
}
