using Babelstone.Engine;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// F.5 (babelstone-k4yr) renewal-guard unit tests: the renewal rejections that fire BEFORE any
/// rate-sheet resolve or settlement (the policy branch and the opt-out window). PURE — no Docker:
/// the closing deposit's events are seeded into the in-memory <see cref="IEventStore"/> (reusing
/// <c>LifecycleRejectionTests</c>' helpers) and the service is wired with a <c>NullSink</c> plus a
/// rate-sheet store and settlement port that fail if touched, so a rejection that resolves a sheet
/// or moves money is caught loud. The happy-path renewal (append + fold over real Postgres) is the
/// Integration tier in <c>RenewalHappyPathTests</c>.
/// </summary>
public sealed class RenewalRejectionTests
{
    private static readonly DateOnly Start = new(2026, 1, 15);
    private static readonly DateOnly Maturity = new(2027, 1, 15);

    [Fact]
    public async Task RenewAsync_rejects_a_NONE_policy_deposit()
    {
        // NONE terminates at maturity, never renews (02 §2.4.4). The rejection precedes the maturity
        // leg, so no sheet is resolved and no money moves (the throwing ports prove it).
        var depositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, ActiveStream(depositId, "NONE"));

        var ex = await Assert.ThrowsAsync<DomainRejectedException>(() =>
            service.RenewAsync(RenewCommand(depositId, new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero))));

        Assert.Contains("NONE", ex.Message);
    }

    [Fact]
    public async Task RenewAsync_rejects_renewal_inside_the_pre_maturity_opt_out_window()
    {
        // The opt-out window is the final 14 days before maturity (pt.2026.1 constants). A renewal dated
        // 2027-01-05 (10 days before the 2027-01-15 maturity) is inside the window — the customer still
        // holds the opt-out right, so auto-renewal must not fire. Rejected before any sheet/settlement.
        var depositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, ActiveStream(depositId, "SAME_TERM_CURRENT_RATE"));

        var ex = await Assert.ThrowsAsync<DomainRejectedException>(() =>
            service.RenewAsync(RenewCommand(depositId, new DateTimeOffset(2027, 1, 5, 0, 0, 0, TimeSpan.Zero))));

        Assert.Contains("opt-out window", ex.Message);
        Assert.Contains("14", ex.Message); // names the pack-parameter window length
    }

    [Fact]
    public async Task RenewAsync_rejects_renewal_before_maturity_outside_the_window()
    {
        // Well before the 14-day window (2026-06-01): the term is plainly not up. Still rejected — the
        // opt-out right has not even opened, let alone closed. The message distinguishes this case.
        var depositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, ActiveStream(depositId, "SAME_TERM_SAME_RATE"));

        var ex = await Assert.ThrowsAsync<DomainRejectedException>(() =>
            service.RenewAsync(RenewCommand(depositId, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero))));

        Assert.Contains("before maturity", ex.Message);
    }

    [Fact]
    public async Task RenewAsync_rejects_renewing_a_closed_deposit()
    {
        // The F.3 lifecycle gate: a Renewed (terminal) deposit cannot renew again. Same table the
        // maturity/coupon rejections route through.
        var depositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, RenewedStream(depositId));

        var ex = await Assert.ThrowsAsync<DomainRejectedException>(() =>
            service.RenewAsync(RenewCommand(depositId, new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero))));

        Assert.Contains("Renewed", ex.Message);
        Assert.Contains("Renew", ex.Message);
    }

    [Fact]
    public async Task RenewAsync_rejects_renewing_an_already_matured_deposit()
    {
        // A standalone-Matured (terminal) deposit cannot renew: the F.3 table makes Renew legal only
        // from Active, so the step-1 entry gate rejects it before any maturity leg runs — no second
        // DepositMatured is ever appended on the closed stream. This pins the renewal flow's Mature
        // leg as table-governed too (the maturity leg only proceeds from an Active head); the throwing
        // ports prove no sheet resolves and no money moves on the rejection.
        var depositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, MaturedStream(depositId));

        var ex = await Assert.ThrowsAsync<DomainRejectedException>(() =>
            service.RenewAsync(RenewCommand(depositId, new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero))));

        Assert.Contains("Matured", ex.Message);
        Assert.Contains("Renew", ex.Message); // the illegal transition the closed deposit cannot drive
    }

    // ---- seed streams + command -----------------------------------------------------------------

    private static RenewDepositCommand RenewCommand(Guid depositId, DateTimeOffset renewedAt) =>
        new(
            DepositId: depositId, ProductId: "dpz_pt_12m_juros_venc", Role: "standard",
            RenewedAt: renewedAt, NewDepositId: Guid.NewGuid(),
            PayoutAccount: "PT50-DDA-001", FundingAccount: "PT50-DDA-001", Actor: "test");

    /// <summary>A bare constituted AT_MATURITY deposit with the given policy → folds to Active.</summary>
    private static DomainEvent[] ActiveStream(Guid depositId, string policy) =>
    [
        new DepositConstituted(
            depositId, new Money(1_000_000), 300, "pt-deposits-2026.1", 365, Start, Maturity, "AT_MATURITY", policy),
    ];

    /// <summary>A constituted + matured (but NOT renewed) deposit → folds to the terminal Matured state.</summary>
    private static DomainEvent[] MaturedStream(Guid depositId) =>
    [
        new DepositConstituted(
            depositId, new Money(1_000_000), 300, "pt-deposits-2026.1", 365, Start, Maturity, "AT_MATURITY", "SAME_TERM_CURRENT_RATE"),
        new InterestAccrued(new Money(30_417), Maturity),
        new WithholdingApplied(new Money(8_517), new Money(21_900)),
        new DepositMatured(new Money(1_000_000), new Money(21_900), new Money(1_021_900), Maturity),
    ];

    /// <summary>A constituted + matured + renewed deposit → folds to the terminal Renewed state.</summary>
    private static DomainEvent[] RenewedStream(Guid depositId) =>
    [
        new DepositConstituted(
            depositId, new Money(1_000_000), 300, "pt-deposits-2026.1", 365, Start, Maturity, "AT_MATURITY", "SAME_TERM_CURRENT_RATE"),
        new InterestAccrued(new Money(30_417), Maturity),
        new WithholdingApplied(new Money(8_517), new Money(21_900)),
        new DepositMatured(new Money(1_000_000), new Money(21_900), new Money(1_021_900), Maturity),
        new DepositRenewed(depositId, Guid.NewGuid(), new Money(1_000_000), "pt-deposits-2026.1", 300, 365, Maturity, Maturity.AddDays(365)),
    ];

    /// <summary>Compose the durable runtime over an in-memory store seeded with <paramref name="stream"/>,
    /// a discard sink, and rate-sheet/settlement ports that fail if touched (these rejections must precede
    /// both). Reuses the <c>LifecycleRejectionTests</c> internal helpers (same assembly).</summary>
    private static TermDepositConstitutionService ServiceOverStream(Guid depositId, DomainEvent[] stream)
    {
        var serializer = new JsonEventSerializer();
        var registry = TermDepositFamilyModule.Registry();
        var store = new InMemoryEventStore(depositId, stream, serializer, registry);
        var runtime = new AggregateRuntime<DepositPosition>(
            store, new NullSink(), registry, serializer, new NullPiiProtector(), TimeProvider.System,
            () => DepositPosition.Empty);

        return new TermDepositConstitutionService(
            runtime, new ThrowingRateSheetStore(), new ThrowingSettlementPort(failOnSettle: true),
            SkeletonPack.LoadPt2026(), dayCountPrimitive: "act_360", withholdingPrimitive: "irs_juros");
    }
}
