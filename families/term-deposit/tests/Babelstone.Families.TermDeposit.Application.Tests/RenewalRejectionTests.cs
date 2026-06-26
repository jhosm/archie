using Babelstone.Engine;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// Renewal-guard unit tests (bd babelstone-mtto PR B), the semantic shift from the retired monolith.
/// In the monolith <c>RenewAsync</c> ran on an ACTIVE deposit and matured it inline, so its guards
/// rejected NONE-policy, pre-maturity (opt-out-window) and closed deposits. The renewal saga now runs
/// AFTER the autonomous maturity leg, so <c>ConstituteRenewalAsync</c>'s precondition is the OPPOSITE:
/// the closing deposit MUST already be <see cref="DepositLifecycle.Matured"/>. The guards therefore
/// reframe:
/// <list type="bullet">
/// <item>NONE policy is still rejected (the new rejection path) — KEPT, retargeted to ConstituteRenewal.</item>
/// <item>The old "pre-maturity opt-out window" rejections become the Matured-PRECONDITION guard: an
/// Active (not-yet-matured) closing deposit is rejected, because maturity precedes the saga and
/// pre-maturity renewal is structurally impossible on the saga path.</item>
/// <item>An already-Renewed closing deposit is still rejected — KEPT, retargeted to ConstituteRenewal.</item>
/// <item>The old "rejects renewing an already-matured deposit" INVERTS: a Matured closing deposit is now
/// the HAPPY-PATH precondition (asserted positively in <c>RenewalHappyPathTests</c>), so that rejection
/// is deleted.</item>
/// </list>
/// PURE — no Docker: the closing deposit's events are seeded into the in-memory event store and the
/// service is wired with rate-sheet/settlement ports that FAIL if touched, so a rejection that resolves
/// a sheet or moves money is caught loud (every rejection here fires before either). The happy-path
/// renewal over real Postgres is the Integration tier in <c>RenewalHappyPathTests</c>.
/// </summary>
public sealed class RenewalRejectionTests
{
    private static readonly DateOnly Start = new(2026, 1, 15);
    private static readonly DateOnly Maturity = new(2027, 1, 15);
    private static readonly DateTimeOffset RenewedAt = new(2027, 1, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConstituteRenewalAsync_rejects_a_NONE_policy_deposit()
    {
        // NONE terminates at maturity, never renews (02 §2.4.4). The saga's header filter never starts a
        // NONE-policy saga, but a direct call is still rejected — fail-loud, not a silent fall-through. The
        // rejection precedes the rate resolve, so no sheet is resolved and no money moves (the throwing
        // ports prove it). The closing deposit is Matured (the saga precondition), policy NONE.
        var depositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, MaturedStream(depositId, "NONE"));

        var ex = await Assert.ThrowsAsync<DomainRejectedException>(() =>
            service.ConstituteRenewalAsync(ConstituteRenewalCommand(depositId)));

        Assert.Contains("NONE", ex.Message);
    }

    [Fact]
    public async Task ConstituteRenewalAsync_rejects_an_Active_not_yet_matured_closing_deposit()
    {
        // The Matured-PRECONDITION guard (the reframed opt-out-window rejection). Maturity is autonomous
        // and precedes the renewal saga, so a closing deposit that is still ACTIVE cannot constitute a
        // renewal — pre-maturity renewal is structurally impossible on the saga path. The F.3 table has no
        // Renew-from-Matured row, so this is asserted directly. Rejected before any sheet/settlement.
        var depositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, ActiveStream(depositId, "SAME_TERM_CURRENT_RATE"));

        var ex = await Assert.ThrowsAsync<DomainRejectedException>(() =>
            service.ConstituteRenewalAsync(ConstituteRenewalCommand(depositId)));

        Assert.Contains("Active", ex.Message);
        Assert.Contains("Matured", ex.Message); // names the precondition the Active deposit fails
    }

    [Fact]
    public async Task ConstituteRenewalAsync_rejects_an_already_Renewed_closing_deposit()
    {
        // A Renewed (terminal) closing deposit cannot constitute a second renewal: it is not Matured, so
        // the Matured-precondition assertion rejects it (the F.3 terminal model, no Renew-from-Matured row).
        var depositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, RenewedStream(depositId));

        var ex = await Assert.ThrowsAsync<DomainRejectedException>(() =>
            service.ConstituteRenewalAsync(ConstituteRenewalCommand(depositId)));

        Assert.Contains("Renewed", ex.Message);
        Assert.Contains("Matured", ex.Message); // the precondition the closed deposit fails
    }

    [Fact]
    public async Task LinkRenewalAsync_rejects_an_Active_not_yet_matured_closing_deposit()
    {
        // The link step folds Matured → Renewed, so it too requires a Matured closing head. An Active
        // closing deposit is rejected before any append (the new stream is never even loaded).
        var depositId = Guid.NewGuid();
        var newDepositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, ActiveStream(depositId, "SAME_TERM_CURRENT_RATE"));

        var ex = await Assert.ThrowsAsync<DomainRejectedException>(() =>
            service.LinkRenewalAsync(new LinkRenewalCommand(
                DepositId: depositId, NewDepositId: newDepositId, RenewedAt: RenewedAt, Actor: "test")));

        Assert.Contains("Active", ex.Message);
        Assert.Contains("Matured", ex.Message);
    }

    [Theory]
    // Inside the 14-day pre-maturity opt-out window (7 days before maturity): the message names the window.
    [InlineData("2027-01-08", "opt-out window")]
    // Well before maturity (outside the window too): the message says "before maturity".
    [InlineData("2026-06-01", "before maturity")]
    public async Task ConstituteRenewalAsync_rejects_a_renewal_fired_before_the_opt_out_window_closes(
        string renewedOn, string expectedReason)
    {
        // The PRE-MATURITY OPT-OUT-WINDOW saga-start gate (bd babelstone-mtto.3; 02 §2.4.4 / ADR-PC-023 §P).
        // The opt-out right closes at the maturity date, so auto-renewal must NOT fire before maturity —
        // a renewal whose RenewedAt is before the closing deposit's maturity date is rejected, the message
        // distinguishing "within the N-day opt-out window" from "before maturity". The closing deposit is
        // Matured + non-NONE, so the NONE and Matured-precondition guards pass and THIS gate is what fires —
        // BEFORE the funding/rate resolve (the throwing ports prove neither is touched). The window length
        // (14 days) is read from the pinned pack's PackParameters (the dead AutoRenewalOptoutWindowDays
        // param is now wired). MaturedStream's maturity date is 2027-01-15.
        var depositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, MaturedStream(depositId, "SAME_TERM_CURRENT_RATE"));
        var renewedAt = new DateTimeOffset(DateOnly.Parse(renewedOn), TimeOnly.MinValue, TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<DomainRejectedException>(() =>
            service.ConstituteRenewalAsync(new ConstituteRenewalCommand(
                DepositId: depositId, NewDepositId: Guid.NewGuid(), RenewedAt: renewedAt, Actor: "test")));

        Assert.Contains(expectedReason, ex.Message);
        // The rejection fires on the window gate, not the empty-funding guard further down.
        Assert.DoesNotContain("funding_account", ex.Message);
    }

    [Fact]
    public async Task ConstituteRenewalAsync_rejects_a_closing_deposit_with_no_funding_account()
    {
        // Safety guard (bd babelstone-mtto.5): the renewal rolls the principal over against the CLOSING
        // deposit's funding token, resolved engine-side. A pre-mtto.5 closing deposit (constituted before
        // funding_account was persisted) folds to an EMPTY funding token; ConstituteRenewalAsync must FAIL
        // LOUD rather than debit an invented/empty account — the engine never invents a funding reference.
        // The closing deposit is Matured + non-NONE, so the NONE and Matured-precondition guards pass and
        // the empty-funding guard is what fires — BEFORE any sheet resolve or settlement (the throwing
        // ports prove neither is touched). MaturedStream constitutes WITHOUT funding_account (the field
        // defaults to ""), exactly the pre-field shape this guard protects against.
        var depositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, MaturedStream(depositId, "SAME_TERM_CURRENT_RATE"));

        var ex = await Assert.ThrowsAsync<DomainRejectedException>(() =>
            service.ConstituteRenewalAsync(ConstituteRenewalCommand(depositId)));

        Assert.Contains("funding_account", ex.Message);
    }

    // ---- seed streams + command -----------------------------------------------------------------

    // The command is MINIMAL (bd babelstone-mtto.5): no product / role / funding. Every rejection here
    // fires (NONE policy, not-yet-Matured, already-Renewed) BEFORE the engine would resolve product /
    // role / funding off the closing deposit, so the dropped fields are irrelevant to these guards.
    private static ConstituteRenewalCommand ConstituteRenewalCommand(Guid depositId) =>
        new(DepositId: depositId, NewDepositId: Guid.NewGuid(), RenewedAt: RenewedAt, Actor: "test");

    /// <summary>A bare constituted AT_MATURITY deposit with the given policy → folds to Active.</summary>
    private static DomainEvent[] ActiveStream(Guid depositId, string policy) =>
    [
        new DepositConstituted(
            depositId, new Money(1_000_000), 300, "pt-deposits-2026.1", 365, Start, Maturity, "AT_MATURITY", policy),
    ];

    /// <summary>A constituted + matured (but NOT renewed) deposit with the given policy → folds to the
    /// terminal Matured state. This is the renewal saga's PRECONDITION head (maturity already ran).</summary>
    private static DomainEvent[] MaturedStream(Guid depositId, string policy) =>
    [
        new DepositConstituted(
            depositId, new Money(1_000_000), 300, "pt-deposits-2026.1", 365, Start, Maturity, "AT_MATURITY", policy),
        new InterestAccrued(new Money(30_417), Maturity),
        new WithholdingApplied(new Money(8_517), new Money(21_900)),
        new DepositMatured(new Money(1_000_000), new Money(21_900), new Money(1_021_900), Maturity, policy),
    ];

    /// <summary>A constituted + matured + renewed deposit → folds to the terminal Renewed state.</summary>
    private static DomainEvent[] RenewedStream(Guid depositId) =>
    [
        new DepositConstituted(
            depositId, new Money(1_000_000), 300, "pt-deposits-2026.1", 365, Start, Maturity, "AT_MATURITY", "SAME_TERM_CURRENT_RATE"),
        new InterestAccrued(new Money(30_417), Maturity),
        new WithholdingApplied(new Money(8_517), new Money(21_900)),
        new DepositMatured(new Money(1_000_000), new Money(21_900), new Money(1_021_900), Maturity, "SAME_TERM_CURRENT_RATE"),
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
            runtime, new ThrowingRateSheetStore(),
            SkeletonPack.LoadPt2026(), dayCountPrimitive: "act_360", withholdingPrimitive: "irs_juros");
    }
}
