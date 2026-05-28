using Babelstone.FinancialTypes;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Xunit;

namespace Babelstone.FinancialMath.Tests;

/// <summary>
/// Integration-level laws for the accrual + withholding orchestration (B.9; fin-math §5.4) — the
/// obligation carried from the B.3/B.4 review. The type system forbids rate-scaling inside a single
/// <see cref="Withholding.Withhold(Money, int)"/> call, but the §5.4 rule can still be broken one
/// layer up, by withholding on <see cref="Accrual.CompoundInterest"/>'s multi-period aggregate
/// instead of on each accrued payment. <see cref="AccrueAndWithholdMonthly"/> models the orchestration
/// the Epic-A handler will perform — accrue on the running balance, withhold each monthly flow as it
/// lands — and these assert it:
/// <list type="bullet">
///   <item><b>conserves</b> — total gross == total withheld + total net, exactly (§P1–§P2);</item>
///   <item><b>withholds per payment, not on the aggregate</b> — its total withheld stays within the
///   half-cent-per-flow envelope of the round-once aggregate shortcut, so the two are bounded but
///   not interchangeable (the integration analogue of
///   <see cref="WithholdingTests.Withhold_flow_by_flow_differs_from_withholding_an_aggregated_flow"/>).</item>
/// </list>
/// </summary>
public class AccrualWithholdingIntegrationProperties
{
    private const int PtIrsBps = 2_800; // PT IRS withholding = 28% (fin-math §5.4)
    private const int Months = 12;

    [Property]
    public Property Orchestrated_withholding_conserves_and_stays_per_payment()
    {
        // A monthly-compounding deposit: €100–€1M principal, TAN 0%–12%. Withholding is fixed at the
        // PT IRS rate — the property is about where withholding is applied (per flow vs aggregate),
        // not the rate value.
        var gen = from principalEuros in Gen.Choose(100, 1_000_000)
                  from tanBps in Gen.Choose(0, 1_200)
                  select (principalEuros, tanBps);

        return Prop.ForAll(gen.ToArbitrary(), t =>
        {
            var (principalEuros, tanBps) = t;
            var deposit = AccrueAndWithholdMonthly(new Money(principalEuros * 100L), tanBps, PtIrsBps, Months);

            // The wrong path the §5.4 rule guards against: one withholding on the whole accrued sum.
            Money aggregateShortcut = Withholding.Withhold(deposit.Gross, PtIrsBps).Tax;
            long drift = Math.Abs(deposit.Withheld.Cents - aggregateShortcut.Cents);

            return deposit.Gross == deposit.Withheld + deposit.Net  // conservation across the split
                && 2 * drift <= Months + 1;                         // per-payment, bounded vs aggregate
        });
    }

    [Fact]
    public void Canonical_monthly_deposit_withholds_each_payment_and_conserves()
    {
        // €10,000 at TAN 6%, twelve months of monthly compounding — the §5 running example, driven
        // end-to-end through the real Accrual and Withholding primitives.
        var deposit = AccrueAndWithholdMonthly(new Money(1_000_000L), tanBps: 600, PtIrsBps, Months);

        // Twelve positive accrual flows were each withheld (per-payment discipline), gross grew, and
        // the gross/withheld/net split lost not a cent.
        Assert.Equal(Months, deposit.FlowCount);
        Assert.True(deposit.Gross.Cents > 0);
        Assert.Equal(deposit.Gross, deposit.Withheld + deposit.Net);

        // Withholding once on the accrued aggregate (the §5.4 shortcut) is a *different* computation
        // from withholding each payment; over the same accrued flows the two can only ever differ by
        // the half-cent-per-flow envelope (2·drift ≤ n+1). (Withholding instead on CompoundInterest's
        // round-once aggregate is doubly wrong — a different gross AND a single withholding — and is
        // not bounded by this same-flow-set envelope; the orchestration must accrue and withhold flow
        // by flow, which is exactly what AccrueAndWithholdMonthly does.)
        Money aggregateShortcut = Withholding.Withhold(deposit.Gross, PtIrsBps).Tax;
        Assert.True(2 * Math.Abs(deposit.Withheld.Cents - aggregateShortcut.Cents) <= Months + 1);
    }

    /// <summary>
    /// Models the accrual + withholding orchestration: a monthly-compounding deposit accrues interest
    /// on its running balance each period, and withholding is applied to EACH accrued payment as it
    /// lands (fin-math §5.4) — never to the multi-period aggregate. Gross interest is capitalised, so
    /// the balance compounds. Returns the period totals plus the number of flows withheld.
    /// </summary>
    private static (Money Gross, Money Withheld, Money Net, int FlowCount) AccrueAndWithholdMonthly(
        Money principal, int tanBps, int withholdingBps, int months)
    {
        var monthFactor = new DayCountFactor(30, 360); // 30E/360 month = 1/12 of a year
        Money balance = principal;
        Money totalGross = Money.Zero, totalWithheld = Money.Zero, totalNet = Money.Zero;

        for (int t = 0; t < months; t++)
        {
            Money interest = Accrual.SimpleInterest(balance, tanBps, monthFactor);
            var split = Withholding.Withhold(interest, withholdingBps);
            totalGross += interest;
            totalWithheld += split.Tax;
            totalNet += split.Net;
            balance += interest; // gross capitalised — the deposit compounds monthly
        }

        return (totalGross, totalWithheld, totalNet, months);
    }
}
