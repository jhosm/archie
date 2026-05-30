using Babelstone.Engine;
using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.TermDeposit.Tests;

/// <summary>
/// Pure dispatch + fold for the term-deposit family (E.1, archie-uqlm) — no I/O, default CI
/// lane. Pins registry resolution, module discovery, the constitute→accrue→withhold→mature
/// fold, determinism, and that the carried event amounts agree with the financial-math
/// kernel. The durable append/load round-trip lands with E.6 (the Testcontainers lane).
/// </summary>
public sealed class TermDepositDispatchTests
{
    private static readonly HandlerRegistry Registry = TermDepositFamilyModule.Registry();

    // Canonical AT_MATURITY instance: EUR 10,000.00 principal, TAN 3.00%, 365d Act/360,
    // IRS withholding 28%. Gross 304.17, tax 85.17, net 219.00, payout 10,219.00.
    private static readonly Guid DepositId = new("0bbe5f4e-1f5a-4f6e-9b2a-1d4c7e8a9f01");
    private static readonly DateOnly Start = new(2026, 1, 15);
    private static readonly DateOnly Maturity = new(2027, 1, 15);
    private const int PrincipalCents = 1_000_000;
    private const int TanBps = 300;
    private const int IrsWithholdingBps = 2800;

    private static DomainEvent[] HappyPath() =>
    [
        new DepositConstituted(DepositId, new Money(PrincipalCents), TanBps, "pt-deposits-2026.1",
            TermDays: 365, Start, Maturity, "AT_MATURITY", "NONE"),
        new InterestAccrued(new Money(30_417), Maturity),
        new WithholdingApplied(Tax: new Money(8_517), Net: new Money(21_900)),
        new DepositMatured(PrincipalReturned: new Money(PrincipalCents), NetInterestPaid: new Money(21_900),
            TotalPayout: new Money(1_021_900), Maturity),
    ];

    private static SimulationRuntime<DepositPosition> Sim() => new(
        store: null!, // ProjectFromScratch never reads the store
        handlers: Registry,
        serializer: new JsonEventSerializer(),
        seedState: () => DepositPosition.Empty);

    [Theory]
    [InlineData("term_deposit.DepositConstituted")]
    [InlineData("term_deposit.InterestAccrued")]
    [InlineData("term_deposit.WithholdingApplied")]
    [InlineData("term_deposit.DepositMatured")]
    public void Registry_resolves_each_family_event_type(string eventType)
        => Assert.True(Registry.TryResolve(eventType, out _));

    [Fact]
    public void Registry_does_not_resolve_an_unknown_event_type()
        => Assert.False(Registry.TryResolve("term_deposit.Unknown", out _));

    [Fact]
    public void Family_module_loader_discovers_term_deposit_in_the_family_assembly()
    {
        // Mirrors how the engine loads families: scan the family assembly (where the module
        // lives), not the executing test assembly. Proves discovery + the public parameterless ctor.
        var modules = new FamilyModuleLoader().LoadAll([typeof(TermDepositFamilyModule).Assembly]);
        Assert.Contains(modules, m => m.FamilyName == "term_deposit");
    }

    [Fact]
    public void Constitute_accrue_withhold_mature_folds_to_the_expected_position()
    {
        var position = Sim().ProjectFromScratch(HappyPath());

        Assert.Equal(DepositId, position.DepositId);
        Assert.Equal(new Money(PrincipalCents), position.Principal);
        Assert.Equal(new Money(30_417), position.AccruedGrossInterest);
        Assert.Equal(new Money(8_517), position.WithholdingToDate);
        Assert.Equal(new Money(21_900), position.NetInterest);
        Assert.Equal(new Money(1_021_900), position.TotalPayout);
        Assert.Equal(DepositLifecycle.Matured, position.Lifecycle);
    }

    [Fact]
    public void Forward_projection_is_a_deterministic_fold()
    {
        var sim = Sim();
        var events = HappyPath();

        var first = sim.ProjectFromScratch(events);
        var second = sim.ProjectFromScratch(events);

        Assert.Equal(first, second); // structural record equality across runs
    }

    [Fact]
    public void Carried_event_amounts_match_the_financial_math_kernel()
    {
        var factor = DayCount.Between(Start, Maturity, DayCountConvention.Act360);
        var gross = Accrual.SimpleInterest(new Money(PrincipalCents), TanBps, factor);
        var withheld = Withholding.Withhold(gross, IrsWithholdingBps);

        Assert.Equal(new Money(30_417), gross);
        Assert.Equal(new Money(8_517), withheld.Tax);
        Assert.Equal(new Money(21_900), withheld.Net);
        Assert.Equal(new Money(PrincipalCents) + withheld.Net, new Money(1_021_900));
    }
}
