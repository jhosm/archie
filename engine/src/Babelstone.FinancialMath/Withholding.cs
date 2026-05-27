namespace Babelstone.FinancialMath;

// See Accrual.cs: the alias must sit inside the namespace to outrank the like-named
// Babelstone.Money namespace reachable via the enclosing Babelstone.
using Money = global::Babelstone.Money.Money;

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
    // Int, not a decimal field (BMNY002 / ADR-PC-010 §P1); promotes to decimal at the boundary.
    private const int BasisPointsPerUnit = 10_000;

    /// <summary>
    /// Withhold tax from one gross interest flow. Tax is rounded once at the boundary
    /// (HALF_EVEN); net is the residual, conserving cents.
    /// </summary>
    /// <param name="grossInterest">The gross interest flow this payment accrues.</param>
    /// <param name="withholdingRateBps">Withholding rate in basis points (PT IRS = 2800).</param>
    /// <exception cref="ArgumentOutOfRangeException">If the rate is outside [0, 10000].</exception>
    public static WithholdingResult Withhold(Money grossInterest, int withholdingRateBps)
    {
        if (withholdingRateBps is < 0 or > 10_000)
            throw new ArgumentOutOfRangeException(
                nameof(withholdingRateBps), withholdingRateBps, "Withholding rate must be within [0, 10000] bps.");

        Money tax = Money.FromCents((decimal)grossInterest.Cents * withholdingRateBps / BasisPointsPerUnit);
        Money net = grossInterest - tax;
        return new WithholdingResult(grossInterest, tax, net);
    }
}
