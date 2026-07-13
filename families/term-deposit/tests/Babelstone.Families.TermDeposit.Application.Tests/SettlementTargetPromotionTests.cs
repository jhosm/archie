using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// The term-deposit PRODUCER half of engine-CA settlement (ADR-PC-043 /
/// ADR-IC-018). In plain English: when a deposit matures, pays a coupon, terminates early, or rolls
/// over, the cash leg used to silently default to the LEGACY core. These tests prove the term-deposit
/// service now stamps the settlement COUNTERPARTY explicitly on the money-moving event, so an
/// engine-CA-configured instance promotes the <c>ce_settlementtarget = engine-ca</c> routing header the
/// substrate router keys on — while a default (legacy) instance promotes NO target header and keeps
/// legacy routing byte-identical (UNCHANGED).
/// </summary>
/// <remarks>
/// <para>
/// PURE unit tests (no Docker): the deposit's prior events are seeded into the in-memory
/// <see cref="InMemoryEventStore"/> (reused from <c>LifecycleRejectionTests</c>, same assembly) so the
/// rehydrated position folds to the right lifecycle, and the append is intercepted by a
/// <see cref="CapturingSink"/> that records the <c>integration_headers</c> the runtime derives from each
/// event's <see cref="DomainEvent.IntegrationHeaders"/> getter — the SAME map the relay promotes to the
/// <c>ce_*</c> headers (proven end-to-end in <c>MovementHeaderPromotionTests</c>). We assert the routing
/// header on the outbox row, the wire truth, not just the C# property.
/// </para>
/// <para>
/// The routing selector is HEADER-ONLY (ADR-IC-018): the persistent customer account rides
/// <see cref="Movement.AccountRef"/> (Step A — the payout/funding account), and the counterparty token
/// rides the header (Step B). The substrate never reads <see cref="Movement.AccountRef"/> from the body
/// to route. These tests pin the header selector; the <see cref="Movement.AccountRef"/> value itself is
/// pinned by the happy-path integration tests.
/// </para>
/// </remarks>
public sealed class SettlementTargetPromotionTests
{
    private static readonly DateOnly Start = new(2026, 1, 15);
    private static readonly DateOnly Maturity = new(2027, 1, 15);
    private const string PayoutAccount = "PT50-CA-ENGINE-001";

    // ---- maturity (PayMaturity credit) ----------------------------------------------------------

    [Fact]
    public async Task An_engine_ca_configured_service_promotes_settlementtarget_on_the_maturity_leg()
    {
        var depositId = Guid.NewGuid();
        var (service, sink) = ServiceOverStream(
            depositId, ActiveAtMaturityStream(depositId), SettlementTarget.EngineCa);

        await service.MatureAsync(new MatureDepositCommand(
            depositId, new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero), PayoutAccount, "test"));

        AssertEngineCaTargeted(sink, SettlementDirection.Credit);
    }

    [Fact]
    public async Task A_default_service_promotes_engine_ca_on_the_maturity_leg()
    {
        // The DEFAULT is now EngineCa — a service constructed with no settlementTarget argument routes the
        // maturity leg to the engine-owned current account, promoting ce_settlementtarget = engine-ca. Legacy
        // routing is opt-OUT, proven separately below.
        var depositId = Guid.NewGuid();
        var (service, sink) = ServiceOverStream(depositId, ActiveAtMaturityStream(depositId));

        await service.MatureAsync(new MatureDepositCommand(
            depositId, new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero), PayoutAccount, "test"));

        AssertEngineCaTargeted(sink, SettlementDirection.Credit);
    }

    [Fact]
    public async Task An_explicit_legacy_service_promotes_no_settlementtarget_on_the_maturity_leg()
    {
        // Legacy routing is now opt-OUT: a service constructed with an EXPLICIT SettlementTarget.LegacyDda keeps
        // the maturity leg on the legacy core — only movement headers ride, never ce_settlementtarget, so the
        // substrate router falls back to legacy (UNCHANGED).
        var depositId = Guid.NewGuid();
        var (service, sink) = ServiceOverStream(
            depositId, ActiveAtMaturityStream(depositId), SettlementTarget.LegacyDda);

        await service.MatureAsync(new MatureDepositCommand(
            depositId, new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero), PayoutAccount, "test"));

        AssertLegacyTargeted(sink, SettlementDirection.Credit);
    }

    // ---- coupon (PayCoupon credit, PERIODIC) ----------------------------------------------------

    [Fact]
    public async Task An_engine_ca_configured_service_promotes_settlementtarget_on_the_coupon_leg()
    {
        var depositId = Guid.NewGuid();
        var (service, sink) = ServiceOverStream(
            depositId, ActivePeriodicStream(depositId), SettlementTarget.EngineCa);

        await service.PayInterestAsync(new PayInterestCommand(
            depositId, new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero), PayoutAccount, "test"));

        AssertEngineCaTargeted(sink, SettlementDirection.Credit);
        // The coupon leg carries NO policy header (unlike maturity), so its header map is EXACTLY the FIVE
        // engine-CA entries — origin + directions + settlementtarget + the ADR-PC-043 §D5 promoted destination
        // (movementaccountrefs) and amount (movementamounts) — and nothing else leaks (opaque ref + cents, no PII).
        var headers = SettlementHeadersOf(sink);
        Assert.Equal(5, headers.Count);
        Assert.True(headers.ContainsKey(MovementHeaders.AccountRefsKey));
        Assert.True(headers.ContainsKey(MovementHeaders.AmountsKey));
    }

    // ---- early termination (PayEarlyTermination credit) -----------------------------------------

    [Fact]
    public async Task An_engine_ca_configured_service_promotes_settlementtarget_on_the_early_termination_leg()
    {
        var depositId = Guid.NewGuid();
        var (service, sink) = ServiceOverStream(
            depositId, ActiveAtMaturityStream(depositId), SettlementTarget.EngineCa,
            earlyTerminationPolicy: FlatQuarterPenalty);

        await service.TerminateEarlyAsync(new TerminateEarlyCommand(
            depositId, new DateTimeOffset(2026, 3, 16, 0, 0, 0, TimeSpan.Zero),
            PayoutAccount, "CUSTOMER_REQUEST", "test"));

        AssertEngineCaTargeted(sink, SettlementDirection.Credit);
    }

    // ---- renewal (rollover Debit) ---------------------------------------------------------------

    [Fact]
    public async Task An_engine_ca_configured_service_promotes_settlementtarget_on_the_renewal_rollover_leg()
    {
        // A renewal off a Matured, SAME_TERM_SAME_RATE deposit rolls the matured principal into the new
        // stream as an Originated DEBIT against the funding account — no rate-sheet re-resolve (the closing
        // rate carries forward), so the pure in-memory harness suffices. The rollover leg is the Debit the
        // header must target engine-ca.
        var newDepositId = Guid.NewGuid();
        var closingId = Guid.NewGuid();
        var (service, sink) = ServiceOverStream(
            closingId, MaturedSameRateRenewableStream(closingId), SettlementTarget.EngineCa);

        await service.ConstituteRenewalAsync(new ConstituteRenewalCommand(
            DepositId: closingId, NewDepositId: newDepositId,
            RenewedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero), Actor: "saga:renewal"));

        AssertEngineCaTargeted(sink, SettlementDirection.Debit);
    }

    // ---- assertions -----------------------------------------------------------------------------

    private static void AssertEngineCaTargeted(CapturingSink sink, SettlementDirection direction)
    {
        var headers = SettlementHeadersOf(sink);
        Assert.Equal("Originated", headers[MovementHeaders.OriginKey]);
        Assert.Equal(direction.ToString(), headers[MovementHeaders.DirectionsKey]);
        // The engine-CA counterparty rides ce_settlementtarget = engine-ca — a closed-enum token, no amount,
        // no account ref, no PII (ADR-PC-043 / ADR-PC-004). AccountRef stays on the payload's Movement.
        Assert.Equal(MovementHeaders.EngineCaValue, headers[MovementHeaders.SettlementTargetKey]);
    }

    private static void AssertLegacyTargeted(CapturingSink sink, SettlementDirection direction)
    {
        var headers = SettlementHeadersOf(sink);
        Assert.Equal("Originated", headers[MovementHeaders.OriginKey]);
        Assert.Equal(direction.ToString(), headers[MovementHeaders.DirectionsKey]);
        // No settlementtarget header — the substrate router falls back to the legacy core (UNCHANGED).
        Assert.False(headers.ContainsKey(MovementHeaders.SettlementTargetKey));
    }

    /// <summary>The single settlement-header map the append produced — the outbox row that carries the
    /// Originated movement headers (there is exactly one money-moving event per lifecycle command here).</summary>
    private static IReadOnlyDictionary<string, string> SettlementHeadersOf(CapturingSink sink)
    {
        var settlementRows = sink.OutboxRows
            .Where(r => r.IntegrationHeaders is not null
                && r.IntegrationHeaders.ContainsKey(MovementHeaders.OriginKey))
            .ToList();
        return Assert.Single(settlementRows).IntegrationHeaders!;
    }

    // ---- seed streams ---------------------------------------------------------------------------

    /// <summary>A bare constituted AT_MATURITY deposit → folds to Active (maturity / early-termination legal).</summary>
    private static DomainEvent[] ActiveAtMaturityStream(Guid depositId) =>
    [
        new DepositConstituted(
            depositId, new Money(1_000_000), 300, "rs-1", 365, Start, Maturity, "AT_MATURITY", "NONE"),
    ];

    /// <summary>A constituted PERIODIC (monthly) deposit → folds to Active with coupons payable.</summary>
    private static DomainEvent[] ActivePeriodicStream(Guid depositId) =>
    [
        new DepositConstituted(
            depositId, new Money(1_000_000), 300, "rs-1", 365, Start, Maturity, "PERIODIC", "NONE",
            PaymentPeriodMonths: 1),
    ];

    /// <summary>A constituted + matured AT_MATURITY deposit that auto-renews at the SAME rate (no re-resolve),
    /// carrying a funding account forward so the rollover debit has a persistent target. Folds to Matured.</summary>
    private static DomainEvent[] MaturedSameRateRenewableStream(Guid depositId) =>
    [
        new DepositConstituted(
            depositId, new Money(1_000_000), 300, "rs-1", 365, Start, Maturity, "AT_MATURITY",
            "SAME_TERM_SAME_RATE", ProductCode: "dpz_pt_12m_juros_venc", Role: "standard",
            FundingAccount: "PT50-CA-ENGINE-FUND"),
        new InterestAccrued(new Money(30_417), Maturity),
        new WithholdingApplied(new Money(8_517), new Money(21_900)),
        new DepositMatured(new Money(1_000_000), new Money(21_900), new Money(1_021_900), Maturity,
            AutoRenewalPolicy: "SAME_TERM_SAME_RATE"),
    ];

    // A minimal flat early-termination policy (25% of accrued interest, no floor) — enough to reach the
    // append with a positive net settlement, the leg whose header is under test.
    private static readonly EarlyTerminationPolicy FlatQuarterPenalty = EarlyTerminationPolicy.Banded(
    [
        new EarlyTerminationBand(UpToDays: null, PenaltyBasisPoints: 2_500, PenaltyBasis.AccruedInterest),
    ]);

    /// <summary>Compose the durable runtime over the in-memory store seeded with <paramref name="stream"/>,
    /// a <see cref="CapturingSink"/> that records the appended outbox headers, and a rate-sheet store that
    /// fails if touched (the covered legs — maturity / coupon / early-termination / SAME_RATE renewal — never
    /// re-resolve a sheet). The <paramref name="settlementTarget"/> is the engine-instance counterparty choice
    /// under test; omitting it exercises the EngineCa default (legacy routing is opt-OUT — pass
    /// <see cref="SettlementTarget.LegacyDda"/> explicitly for it).</summary>
    private static (TermDepositConstitutionService Service, CapturingSink Sink) ServiceOverStream(
        Guid depositId, DomainEvent[] stream,
        SettlementTarget settlementTarget = SettlementTarget.EngineCa,
        EarlyTerminationPolicy? earlyTerminationPolicy = null)
    {
        var serializer = new JsonEventSerializer();
        var registry = TermDepositFamilyModule.Registry();
        var store = new InMemoryEventStore(depositId, stream, serializer, registry);
        var sink = new CapturingSink();
        var runtime = new AggregateRuntime<DepositPosition>(
            store, sink, registry, serializer, new NullPiiProtector(), TimeProvider.System,
            () => DepositPosition.Empty);

        var service = new TermDepositConstitutionService(
            runtime, new ThrowingRateSheetStore(),
            SkeletonPack.LoadPt2026(), dayCountPrimitive: "act_360", withholdingPrimitive: "irs_juros",
            earlyTerminationPolicy: earlyTerminationPolicy,
            settlementTarget: settlementTarget);
        return (service, sink);
    }
}

/// <summary>An <see cref="IEventSink"/> that records the outbox rows an append produced instead of writing
/// them, so a pure test can inspect the <c>integration_headers</c> the runtime derived from each event's
/// <see cref="DomainEvent.IntegrationHeaders"/> getter — the map the relay promotes to <c>ce_*</c> headers.
/// The optimistic-concurrency contract is unchanged (append succeeds); nothing durable is written.</summary>
internal sealed class CapturingSink : IEventSink
{
    private readonly List<OutboxRow> _outboxRows = [];

    public IReadOnlyList<OutboxRow> OutboxRows => _outboxRows;

    public Task AppendAsync(
        Guid streamId,
        long expectedVersion,
        IReadOnlyList<EventEnvelope> events,
        IReadOnlyList<OutboxRow> outboxRows,
        Guid? commandId = null,
        CancellationToken ct = default)
    {
        _outboxRows.AddRange(outboxRows);
        return Task.CompletedTask;
    }
}
