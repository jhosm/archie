using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Babelstone.RateSheets;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// The F.4 early-termination walking-skeleton path end-to-end against real PostgreSQL
/// (Testcontainers): constitute → break early → decider applies the banded penalty + flow-by-flow
/// withholding → settlement → append+outbox, then a durable rehydrate that folds the deposit to
/// TerminatedEarly with the net settlement. Tagged Integration, so it runs in the Testcontainers
/// lane, not the default unit lane.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EarlyTerminationHappyPathTests(ConstitutionFixture fixture)
    : IClassFixture<ConstitutionFixture>
{
    // The §2.5 worked example, pinned as the engine-instance policy: [≤30d → 100%, ≤90d → 50%,
    // null → 25%] of accrued interest, no floor.
    private static readonly EarlyTerminationPolicy WorkedExample = EarlyTerminationPolicy.Banded(
    [
        new EarlyTerminationBand(UpToDays: 30, PenaltyBasisPoints: 10_000, PenaltyBasis.AccruedInterest),
        new EarlyTerminationBand(UpToDays: 90, PenaltyBasisPoints: 5_000, PenaltyBasis.AccruedInterest),
        new EarlyTerminationBand(UpToDays: null, PenaltyBasisPoints: 2_500, PenaltyBasis.AccruedInterest),
    ]);

    [Fact]
    public async Task Constitute_then_terminate_early_appends_events_and_outbox_and_folds_terminated_position()
    {
        await fixture.EnsureRateSheetAsync(SharedSheet);

        var (runtime, service) = Compose(fixture.ConnectionString);
        var depositId = Guid.NewGuid();

        await service.ConstituteAsync(new ConstituteDepositCommand(
            DepositId: depositId, PrincipalCents: 1_000_000, ProductId: "dpz_pt_12m_juros_venc", Role: "standard",
            TermDays: 365, StartDate: new DateOnly(2026, 1, 15),
            ConstitutedAt: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
            InterestVariant: "AT_MATURITY", AutoRenewalPolicy: "NONE", FundingAccount: "PT50-DDA-001", Actor: "mcp:dev"));

        // Break it at day 60 → the second band (≤90d, 50% of accrued). gross 5,000, net 3,600,
        // penalty 2,500, settle 1,001,100.
        await service.TerminateEarlyAsync(new TerminateEarlyCommand(
            DepositId: depositId,
            TerminatedAt: new DateTimeOffset(2026, 3, 16, 0, 0, 0, TimeSpan.Zero), // 2026-01-15 + 60 days
            PayoutAccount: "PT50-DDA-001", TerminationReason: "CUSTOMER_REQUEST", Actor: "mcp:dev"));

        // The durable projection (rehydrated from the log) folds to the terminated-early numbers.
        var hydrated = await runtime.LoadAsync(depositId);
        var position = hydrated.State;
        Assert.Equal(3, hydrated.Version); // DepositConstituted, InterestAccrued, WithholdingApplied, DepositTerminatedEarly
        Assert.Equal(DepositLifecycle.TerminatedEarly, position.Lifecycle);
        Assert.Equal(new Money(5_000), position.AccruedGrossInterest); // the elapsed 60-day flow
        Assert.Equal(new Money(3_600), position.NetInterest);
        Assert.Equal(new Money(1_001_100), position.SettlementAmount);

        // Every event got its paired outbox row (the ES_ATOMIC_APPEND_OUTBOX pairing).
        Assert.Equal(4, await fixture.CountAsync("events", "stream_id", depositId));
        Assert.Equal(4, await fixture.CountAsync("outbox", "aggregate_id", depositId));

        // De-settled, gated-saga relocation (bd babelstone-t7o3.4 constitution + bd babelstone-t7o3.13
        // early termination): NO eager settlement at all — the eager settlement port is GONE
        // (bd babelstone-t7o3.17). The early-termination payout records its money leg APPEND-FIRST as an
        // Originated Credit Movement on DepositTerminatedEarly (the NET settlement, not the full principal);
        // the substrate-owned settlement saga effects the cash leg, gated (ADR-PC-032 slot 5; the HTTP
        // money-mover endpoint is bd t7o3.13.1). The recorded Movement below is the only money leg.

        var terminated = Assert.Single(await EventsOfAsync<DepositTerminatedEarly>(fixture.ConnectionString, depositId));
        var movement = Assert.Single(terminated.Movements!);
        Assert.Equal(SettlementDirection.Credit, movement.Direction);   // the net settlement ENTERS the payout account
        Assert.Equal(new Money(1_001_100), movement.Amount);
        Assert.Equal(MovementOperation.PayEarlyTermination, movement.Operation);
        Assert.Equal(MovementOrigin.Originated, movement.Origin);
        Assert.Equal("PT50-DDA-001", movement.AccountRef);
    }

    [Fact]
    public async Task Terminating_a_matured_deposit_is_rejected_by_the_f3_lifecycle_gate()
    {
        await fixture.EnsureRateSheetAsync(SharedSheet);

        var (_, service) = Compose(fixture.ConnectionString);
        var depositId = Guid.NewGuid();

        await service.ConstituteAsync(new ConstituteDepositCommand(
            DepositId: depositId, PrincipalCents: 1_000_000, ProductId: "dpz_pt_12m_juros_venc", Role: "standard",
            TermDays: 365, StartDate: new DateOnly(2026, 1, 15),
            ConstitutedAt: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
            InterestVariant: "AT_MATURITY", AutoRenewalPolicy: "NONE", FundingAccount: "PT50-DDA-001", Actor: "mcp:dev"));

        await service.MatureAsync(new MatureDepositCommand(
            DepositId: depositId, MaturedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero),
            PayoutAccount: "PT50-DDA-001", Actor: "mcp:dev"));

        // Matured is terminal — early termination is legal only from Active (the F.3 table decides).
        await Assert.ThrowsAsync<DomainRejectedException>(() => service.TerminateEarlyAsync(new TerminateEarlyCommand(
            DepositId: depositId,
            TerminatedAt: new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero),
            PayoutAccount: "PT50-DDA-001", TerminationReason: "CUSTOMER_REQUEST", Actor: "mcp:dev")));
    }

    private static RateSheet SharedSheet => TestRateSheets.MultiPriced(
        versionId: "pt-deposits-2026.1",
        effectiveFrom: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
        ("dpz_pt_12m_juros_venc", "standard", 300),
        ("dpz_pt_12m_juros_mensal", "standard", 325),
        ("dpz_pt_12m_juros_antecip", "standard", 300));

    /// <summary>Compose the durable runtime + decider with the pinned §2.5 worked-example early-termination
    /// policy (the engine-instance config stand-in). No settlement stub: the eager settlement port was
    /// deleted (bd babelstone-t7o3.17); every money leg now rides an append-first Movement (ADR-PC-032).</summary>
    private static (AggregateRuntime<DepositPosition> Runtime, TermDepositConstitutionService Service)
        Compose(string connectionString)
    {
        var store = new PostgresEventStore(connectionString);
        var runtime = new AggregateRuntime<DepositPosition>(
            store, new EventStoreSink(store), TermDepositFamilyModule.Registry(),
            new JsonEventSerializer(), new NullPiiProtector(), TimeProvider.System,
            () => DepositPosition.Empty);
        var service = new TermDepositConstitutionService(
            runtime, new PostgresRateSheetStore(connectionString), SkeletonPack.LoadPt2026(),
            dayCountPrimitive: "act_360", withholdingPrimitive: "irs_juros", earlyTerminationPolicy: WorkedExample);
        return (runtime, service);
    }

    /// <summary>Load the appended events of type <typeparamref name="TEvent"/> off the durable stream, decoding
    /// the store JSON the runtime fold uses — to assert the Movement a money-moving event records APPEND-FIRST
    /// (bd babelstone-t7o3.13), the leg the substrate-owned settlement saga effects instead of an eager settle.</summary>
    private static async Task<IReadOnlyList<TEvent>> EventsOfAsync<TEvent>(string connectionString, Guid streamId)
        where TEvent : DomainEvent
    {
        var store = new PostgresEventStore(connectionString);
        var serializer = new JsonEventSerializer();
        var events = new List<TEvent>();
        await foreach (var envelope in store.LoadAsync(streamId))
        {
            if (envelope.EventType.EndsWith(typeof(TEvent).Name, StringComparison.Ordinal))
            {
                events.Add((TEvent)serializer.Decode(envelope.Payload, typeof(TEvent)));
            }
        }

        return events;
    }
}
