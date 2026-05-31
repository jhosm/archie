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
    [InlineData("term_deposit.DepositConstitutionFailed")]
    [InlineData("term_deposit.InterestPaid")]
    [InlineData("term_deposit.DepositRenewed")]
    [InlineData("term_deposit.DepositTerminatedEarly")]
    [InlineData("term_deposit.DepositPartiallyWithdrawn")]
    [InlineData("term_deposit.DepositCorrected")]
    [InlineData("term_deposit.DepositTransferredToHeirs")]
    public void Registry_resolves_each_family_event_type(string eventType)
        => Assert.True(Registry.TryResolve(eventType, out _));

    [Fact]
    public void Family_module_declares_the_full_eleven_event_taxonomy()
        => Assert.Equal(11, new TermDepositFamilyModule().Handlers.Count);

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

    // --- F.2: the seven remaining events (babelstone-5czr) -----------------------------------

    /// <summary>The seven new events as concrete instances, used by the codec round-trip and
    /// the resolve checks. Built once so every case shares the same canonical fixtures.</summary>
    public static TheoryData<DomainEvent> NewEvents() =>
    [
        new DepositConstitutionFailed(DepositId, "RATE_SHEET_NOT_FOUND", "no rate sheet pinned for pt-deposits-2026.1"),
        new InterestPaid(DepositId, new Money(30_417), new Money(8_517), new Money(21_900), Maturity),
        new DepositRenewed(DepositId, new("0bbe5f4e-1f5a-4f6e-9b2a-1d4c7e8a9f02"), new Money(PrincipalCents),
            "pt-deposits-2027.1", NewTanBasisPoints: 325, NewTermDays: 365, RenewalDate: Maturity,
            NewMaturityDate: new(2028, 1, 15)),
        new DepositTerminatedEarly(DepositId, PrincipalReturned: new Money(PrincipalCents),
            PenaltyAmount: new Money(5_000), NetSettlementAmount: new Money(995_000),
            TerminatedOn: new(2026, 6, 30), "CUSTOMER_REQUEST"),
        new DepositPartiallyWithdrawn(DepositId, WithdrawnAmount: new Money(200_000),
            RemainingPrincipal: new Money(800_000), WithdrawnOn: new(2026, 6, 30)),
        new DepositCorrected(DepositId, CorrectionId: "corr-001", CorrectedField: "TanBasisPoints",
            PreviousValueRef: "ref://prev/300", CorrectedValueRef: "ref://new/305",
            EffectiveFrom: Start, CorrectionReason: "RATE_SHEET_RESOLUTION_FIX"),
        new DepositTransferredToHeirs(DepositId, HeirCaseRef: "succession-case-7741",
            TransferredBalance: new Money(1_021_900), TransferDate: new(2026, 9, 1)),
    ];

    [Theory]
    [MemberData(nameof(NewEvents))]
    public void New_event_survives_a_codec_round_trip(DomainEvent @event)
    {
        var codec = new JsonEventSerializer();

        var encoded = codec.Encode(@event);
        var decoded = codec.Decode(encoded.Bytes, @event.GetType());

        Assert.Equal(@event, decoded); // structural record equality after encode→decode
    }

    [Fact]
    public void Constitution_failed_folds_to_failed()
    {
        var position = Sim().ProjectFromScratch(
        [
            new DepositConstitutionFailed(DepositId, "RATE_SHEET_NOT_FOUND", "no rate sheet pinned"),
        ]);

        Assert.Equal(DepositId, position.DepositId);
        Assert.Equal(DepositLifecycle.Failed, position.Lifecycle);
    }

    [Fact]
    public void Early_termination_folds_to_terminated_early_with_the_net_settlement()
    {
        var position = Sim().ProjectFromScratch(
        [
            new DepositConstituted(DepositId, new Money(PrincipalCents), TanBps, "pt-deposits-2026.1",
                TermDays: 365, Start, Maturity, "AT_MATURITY", "NONE"),
            new DepositTerminatedEarly(DepositId, PrincipalReturned: new Money(PrincipalCents),
                PenaltyAmount: new Money(5_000), NetSettlementAmount: new Money(995_000),
                TerminatedOn: new(2026, 6, 30), "CUSTOMER_REQUEST"),
        ]);

        Assert.Equal(DepositLifecycle.TerminatedEarly, position.Lifecycle);
        Assert.Equal(new Money(995_000), position.SettlementAmount);
        // Net = Principal − Penalty, conserved to the cent.
        Assert.Equal(new Money(PrincipalCents) - new Money(5_000), position.SettlementAmount);
    }

    [Fact]
    public void Transfer_to_heirs_folds_to_transferred_to_heirs()
    {
        var position = Sim().ProjectFromScratch(
        [
            new DepositConstituted(DepositId, new Money(PrincipalCents), TanBps, "pt-deposits-2026.1",
                TermDays: 365, Start, Maturity, "AT_MATURITY", "NONE"),
            new DepositTransferredToHeirs(DepositId, HeirCaseRef: "succession-case-7741",
                TransferredBalance: new Money(1_021_900), TransferDate: new(2026, 9, 1)),
        ]);

        Assert.Equal(DepositLifecycle.TransferredToHeirs, position.Lifecycle);
        Assert.Equal(new Money(1_021_900), position.SettlementAmount);
    }

    [Fact]
    public void Partial_withdrawal_folds_to_the_remaining_principal()
    {
        var position = Sim().ProjectFromScratch(
        [
            new DepositConstituted(DepositId, new Money(PrincipalCents), TanBps, "pt-deposits-2026.1",
                TermDays: 365, Start, Maturity, "AT_MATURITY", "NONE"),
            new DepositPartiallyWithdrawn(DepositId, WithdrawnAmount: new Money(200_000),
                RemainingPrincipal: new Money(800_000), WithdrawnOn: new(2026, 6, 30)),
        ]);

        Assert.Equal(DepositLifecycle.Active, position.Lifecycle); // still active, just smaller
        Assert.Equal(new Money(800_000), position.RemainingPrincipal);
    }

    [Fact]
    public void Correction_increments_the_correction_count_only()
    {
        var position = Sim().ProjectFromScratch(
        [
            new DepositConstituted(DepositId, new Money(PrincipalCents), TanBps, "pt-deposits-2026.1",
                TermDays: 365, Start, Maturity, "AT_MATURITY", "NONE"),
            new DepositCorrected(DepositId, "corr-001", "TanBasisPoints", "ref://prev/300",
                "ref://new/305", Start, "RATE_SHEET_RESOLUTION_FIX"),
            new DepositCorrected(DepositId, "corr-002", "TanBasisPoints", "ref://prev/305",
                "ref://new/310", Start, "RATE_SHEET_RESOLUTION_FIX"),
        ]);

        Assert.Equal(2, position.CorrectionCount);
        Assert.Equal(DepositLifecycle.Active, position.Lifecycle); // fold only tallies; D.1/D.2 supersedes
    }

    /// <summary>Structural guard: <see cref="DepositTransferredToHeirs"/> carries NO heir PII —
    /// only the opaque heir-case reference (ADR-PC-004 §P2). Reflecting over its public
    /// surface, the only string field is the case ref; there is no name/NIF/IBAN slot for
    /// identity to leak through, in cleartext or ciphertext.</summary>
    [Fact]
    public void Transfer_to_heirs_event_carries_no_heir_pii_only_an_opaque_reference()
    {
        var stringProps = typeof(DepositTransferredToHeirs)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .ToArray();

        Assert.Equal(["HeirCaseRef"], stringProps); // the ONLY string slot is the opaque reference

        var forbidden = new[] { "Name", "Nif", "Iban", "Holder", "Heir", "FullName", "TaxId" };
        Assert.DoesNotContain(typeof(DepositTransferredToHeirs).GetProperties(),
            p => forbidden.Any(f => p.Name.Equals(f, StringComparison.OrdinalIgnoreCase)));
    }
}
