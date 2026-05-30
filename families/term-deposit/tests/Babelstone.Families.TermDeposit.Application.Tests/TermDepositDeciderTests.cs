using Babelstone.Engine;
using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// The pure decision core (ADR-PC-021 §P3) — no I/O, default CI lane. Pins that the decider
/// stamps the resolved rate inputs and reproduces the canonical AT_MATURITY flow from the
/// financial-math kernel. The durable resolve→settle→append wiring is the integration tier.
/// </summary>
public sealed class TermDepositDeciderTests
{
    // Canonical instance: EUR 10,000.00, TAN 3.00%, 365d Act/360, IRS 28% (matches the E.1
    // dispatch test and the pt.2026.1 sealed corpus pt_dpz_12m_simple_with_irs).
    private static readonly DateOnly Start = new(2026, 1, 15);
    private static readonly DateOnly Maturity = new(2027, 1, 15);
    private const long PrincipalCents = 1_000_000;
    private const int TanBps = 300;
    private const int IrsBps = 2800;

    [Fact]
    public void DecideConstitution_stamps_the_resolved_tan_and_version_and_derives_maturity()
    {
        var command = new ConstituteDepositCommand(
            DepositId: Guid.NewGuid(), PrincipalCents: PrincipalCents, ProductId: "dpz_pt_12m_juros_venc",
            Role: "standard", TermDays: 365, StartDate: Start,
            ConstitutedAt: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
            InterestVariant: "AT_MATURITY", AutoRenewalPolicy: "NONE", FundingAccount: "PT50-DDA-001", Actor: "test");

        var constituted = TermDepositDecider.DecideConstitution(command, tanBasisPoints: TanBps, rateSheetVersionId: "pt-deposits-2026.1");

        Assert.Equal(new Money(PrincipalCents), constituted.Principal);
        Assert.Equal(TanBps, constituted.TanBasisPoints);
        Assert.Equal("pt-deposits-2026.1", constituted.RateSheetVersionId);
        Assert.Equal(Maturity, constituted.MaturityDate); // derived from start + term, not recomputed downstream
        Assert.Equal("AT_MATURITY", constituted.InterestVariant);
    }

    [Fact]
    public void DecideMaturity_reproduces_the_canonical_at_maturity_flow()
    {
        var position = DepositPosition.Empty with
        {
            Principal = new Money(PrincipalCents),
            TanBasisPoints = TanBps,
            StartDate = Start,
            MaturityDate = Maturity,
            Lifecycle = DepositLifecycle.Active,
        };

        var events = TermDepositDecider.DecideMaturity(position, DayCountConvention.Act360, IrsBps);

        var accrued = Assert.IsType<InterestAccrued>(events[0]);
        var withheld = Assert.IsType<WithholdingApplied>(events[1]);
        var matured = Assert.IsType<DepositMatured>(events[2]);

        Assert.Equal(new Money(30_417), accrued.GrossInterest);
        Assert.Equal(Maturity, accrued.AsOf);
        Assert.Equal(new Money(8_517), withheld.Tax);
        Assert.Equal(new Money(21_900), withheld.Net);          // gross − tax, conserved to the cent
        Assert.Equal(new Money(PrincipalCents), matured.PrincipalReturned);
        Assert.Equal(new Money(21_900), matured.NetInterestPaid);
        Assert.Equal(new Money(1_021_900), matured.TotalPayout); // principal + net
        Assert.Equal(Maturity, matured.MaturedOn);
    }

    [Fact]
    public void DecideMaturity_is_a_deterministic_pure_function()
    {
        var position = DepositPosition.Empty with
        {
            Principal = new Money(PrincipalCents),
            TanBasisPoints = TanBps,
            StartDate = Start,
            MaturityDate = Maturity,
            Lifecycle = DepositLifecycle.Active,
        };

        var first = TermDepositDecider.DecideMaturity(position, DayCountConvention.Act360, IrsBps);
        var second = TermDepositDecider.DecideMaturity(position, DayCountConvention.Act360, IrsBps);

        Assert.Equal(first, second); // record-equal events across runs
    }
}
