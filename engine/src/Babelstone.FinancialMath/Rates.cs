using Babelstone.FinancialTypes;

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
    /// rate split into periods, and <see cref="Taeg"/> feeds it a solved per-period IRR
    /// (<see cref="InternalRateOfReturn"/>) — so the conversion lives in exactly one place.
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

    /// <summary>
    /// IRR — the per-period rate <c>i</c> that zeroes the present value of a cash-flow vector
    /// (fin-math §6.1): <c>0 = Σ CF(t) / (1 + i)^t</c>. The amounts are exact
    /// <see cref="FinancialTypes.Money"/>; <c>t</c> is an integer period index (0 = now), so
    /// every power is a whole exponent the kernel raises in base-10 <see cref="DecimalMath.Pow"/>
    /// — no <see cref="double"/>. An IRR is generally irrational with no closed form for n ≥ 5
    /// (Abel–Ruffini, §6.2), so it is found numerically: Newton-Raphson from
    /// <paramref name="guess"/>, with a bisection fallback when Newton leaves the domain
    /// (<c>i ≤ −1</c>), stalls on a flat derivative, or overflows. Convergence is on
    /// <c>|PV| &lt; <paramref name="epsilon"/></c> (PV in cents).
    /// </summary>
    /// <remarks>
    /// <b>Envelope.</b> This solves <i>conventional</i> vectors — exactly one sign change, hence
    /// a single IRR — which is every deposit and amortised credit in §5–§6. The pure-decimal
    /// constraint is range, not precision: <c>(1 + i)^t</c> must stay within
    /// <see cref="decimal"/> (~7.9e28), so Newton works near the root for long horizons (where
    /// deposit/credit rates cluster near 0) while the wide-bracket bisection fallback is for
    /// short horizons. A vector whose IRR is unrepresentable for its horizon throws rather than
    /// returning a <c>double</c>-grade approximation. Fractional-time (XIRR / actual-day) is a
    /// deliberate deferral — it would force transcendental powers, hence <c>double</c>.
    /// </remarks>
    /// <param name="cashFlows">(amount, period) pairs; period ≥ 0. Multiple flows may share a period.</param>
    /// <param name="guess">Newton's starting rate (default 0.10 = 10%/period — safe to raise to
    /// the long-horizon limit while staying near typical roots).</param>
    /// <param name="epsilon">Convergence threshold on |PV| in cents (default 1e-6).</param>
    /// <param name="maxIterations">Iteration cap for each method (default 100).</param>
    /// <exception cref="ArgumentException">Fewer than two flows, or no sign change in the amounts
    /// (one-signed vectors have no IRR).</exception>
    /// <exception cref="ArgumentOutOfRangeException">A negative period, non-positive
    /// <paramref name="epsilon"/>/<paramref name="maxIterations"/>, or <paramref name="guess"/> ≤ −1.</exception>
    /// <exception cref="InvalidOperationException">No root could be bracketed within the
    /// searchable, representable rate range (non-conventional vector or out-of-envelope horizon).</exception>
    public static decimal InternalRateOfReturn(
        IReadOnlyList<(Money Amount, int Period)> cashFlows,
        decimal guess = 0.10m,
        decimal epsilon = 0.000001m,
        int maxIterations = 100)
    {
        ArgumentNullException.ThrowIfNull(cashFlows);
        if (cashFlows.Count < 2)
            throw new ArgumentException("IRR needs at least two cash flows.", nameof(cashFlows));
        if (epsilon <= 0m)
            throw new ArgumentOutOfRangeException(nameof(epsilon), epsilon, "Convergence threshold must be positive.");
        if (maxIterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxIterations), maxIterations, "Iteration cap must be positive.");
        if (guess <= -1m)
            throw new ArgumentOutOfRangeException(nameof(guess), guess, "Guess must be greater than −1 (the rate domain is i > −1).");

        bool anyInflow = false, anyOutflow = false;
        foreach (var (amount, period) in cashFlows)
        {
            if (period < 0)
                throw new ArgumentOutOfRangeException(nameof(cashFlows), period, "Cash-flow period index must be non-negative.");
            anyInflow |= amount.Cents > 0;
            anyOutflow |= amount.Cents < 0;
        }
        if (!(anyInflow && anyOutflow))
            throw new ArgumentException(
                "Cash flows must contain both an inflow and an outflow; a single-signed vector has no IRR.", nameof(cashFlows));

        // Newton-Raphson: i ← i − f(i)/f′(i). Quadratic convergence near the root for the
        // smooth, conventional PV functions this targets; hands off to bisection on any of the
        // three ways it can fail (left the domain, flat derivative, overflow far from the root).
        decimal i = guess;
        for (int n = 0; n < maxIterations; n++)
        {
            decimal f, df;
            try { (f, df) = PresentValueAndDerivative(cashFlows, i); }
            catch (OverflowException) { break; }

            if (Math.Abs(f) < epsilon)
                return i;
            if (df == 0m)
                break;

            decimal next = i - f / df;
            if (next <= -1m)
                break;
            i = next;
        }

        return Bisect(cashFlows, epsilon, maxIterations);
    }

    /// <summary>
    /// TAEG (APR) — the annualised IRR of the full borrower-side cash-flow vector, charges
    /// included (fin-math §6.2): <c>TAEG = (1 + i)^m − 1</c> where <c>i</c> is the per-period
    /// IRR from <see cref="InternalRateOfReturn"/>. The §6.2 fee example: a €200 origination fee
    /// netted at disbursement pushes the monthly IRR from ~0.005 to ~0.00817, so the TAEG rises
    /// from ~6.17% to ~10.25% — the charge enters as one more term in the vector, no new maths.
    /// (The doc states ~10.27%; the exact integer-cent vector solves to 10.25% — the doc
    /// pre-rounds i* to 0.00818 and re-compounds. See the test pinning the precise value.)
    /// </summary>
    /// <param name="cashFlows">Full borrower-side (amount, period) vector, including mandatory charges.</param>
    /// <param name="periodsPerYear">Periods per year m for the annualisation (12 for monthly flows).</param>
    /// <param name="guess">Forwarded to the IRR solver.</param>
    /// <param name="epsilon">Forwarded to the IRR solver.</param>
    /// <param name="maxIterations">Forwarded to the IRR solver.</param>
    public static decimal Taeg(
        IReadOnlyList<(Money Amount, int Period)> cashFlows,
        int periodsPerYear,
        decimal guess = 0.10m,
        decimal epsilon = 0.000001m,
        int maxIterations = 100) =>
        Annualize(InternalRateOfReturn(cashFlows, guess, epsilon, maxIterations), periodsPerYear);

    /// <summary>
    /// PV and its first derivative at rate <paramref name="i"/>:
    /// <c>f(i) = Σ CF·(1+i)^−t</c> and <c>f′(i) = −Σ CF·t·(1+i)^−(t+1)</c>. One pass; the
    /// <c>t = 0</c> flow is constant so it contributes nothing to the derivative.
    /// </summary>
    private static (decimal F, decimal Df) PresentValueAndDerivative(
        IReadOnlyList<(Money Amount, int Period)> cashFlows, decimal i)
    {
        decimal onePlusI = 1m + i;
        decimal f = 0m;
        decimal df = 0m;
        foreach (var (amount, t) in cashFlows)
        {
            decimal cents = amount.Cents;
            decimal pow = DecimalMath.Pow(onePlusI, t); // (1+i)^t — t ≥ 0, so one integer power
            f += cents / pow;
            if (t != 0)
                df -= cents * t / (pow * onePlusI); // −CF·t/(1+i)^(t+1)
        }
        return (f, df);
    }

    /// <summary>PV only (bisection needs no derivative).</summary>
    private static decimal PresentValue(IReadOnlyList<(Money Amount, int Period)> cashFlows, decimal i)
    {
        decimal onePlusI = 1m + i;
        decimal f = 0m;
        foreach (var (amount, t) in cashFlows)
            f += (decimal)amount.Cents / DecimalMath.Pow(onePlusI, t);
        return f;
    }

    // Deterministic probe rates, fine near 0 (where conventional IRRs cluster) and coarser
    // outward, used to bracket a sign change for bisection. All are > −1 (in-domain); probes
    // that overflow for a long horizon are skipped, so the usable bracket narrows with horizon.
    private static readonly decimal[] ProbeRates =
    {
        -0.90m, -0.75m, -0.50m, -0.25m, -0.10m, -0.05m, -0.02m, -0.01m, 0m,
        0.01m, 0.02m, 0.05m, 0.10m, 0.20m, 0.50m, 1.00m, 2.00m, 5.00m, 10.00m,
    };

    /// <summary>
    /// Bisection fallback: scan <see cref="ProbeRates"/> for the first adjacent sign change,
    /// then halve until <c>|PV| &lt; <paramref name="epsilon"/></c>. Guaranteed to converge once
    /// a bracket is found; throws if none is (non-conventional vector or out-of-envelope horizon).
    /// </summary>
    private static decimal Bisect(
        IReadOnlyList<(Money Amount, int Period)> cashFlows, decimal epsilon, int maxIterations)
    {
        decimal lo = 0m, hi = 0m, fLo = 0m;
        bool bracketed = false, havePrev = false;
        decimal prevRate = 0m, prevF = 0m;

        foreach (decimal probe in ProbeRates)
        {
            decimal f;
            try { f = PresentValue(cashFlows, probe); }
            catch (OverflowException) { havePrev = false; continue; } // out of decimal range here

            if (f == 0m)
                return probe; // exact root sitting on a probe
            if (havePrev && Math.Sign(prevF) != Math.Sign(f))
            {
                lo = prevRate; fLo = prevF; hi = probe; bracketed = true;
                break;
            }
            prevRate = probe; prevF = f; havePrev = true;
        }

        if (!bracketed)
            throw new InvalidOperationException(
                "Could not bracket an IRR in the searchable rate range: the cash-flow vector may be " +
                "non-conventional (more than one sign change) or its IRR may be unrepresentable for its horizon.");

        for (int n = 0; n < maxIterations; n++)
        {
            decimal mid = (lo + hi) / 2m;
            decimal fMid = PresentValue(cashFlows, mid);
            if (Math.Abs(fMid) < epsilon)
                return mid;
            if (Math.Sign(fMid) == Math.Sign(fLo))
            {
                lo = mid;
                fLo = fMid;
            }
            else
            {
                hi = mid;
            }
        }
        return (lo + hi) / 2m; // tightest bracket midpoint after the iteration cap
    }
}
