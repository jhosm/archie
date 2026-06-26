using System.Runtime.CompilerServices;
using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Babelstone.RateSheets;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// F.3 (babelstone-29v8): the decider consults the <see cref="LifecycleTransitions"/> table and
/// rejects an illegal command with the established <see cref="DomainRejectedException"/> pattern.
/// These are PURE unit tests (no Docker): the deposit's prior events are seeded into an in-memory
/// <see cref="IEventStore"/> so the rehydrated position folds to a terminal lifecycle, and the
/// service is wired with a <see cref="NullSink"/> (the illegal command must be rejected BEFORE any
/// append) and a settlement port that fails if touched (rejection must precede the money leg).
/// The table's own legality matrix is unit-tested separately in the family-tier
/// <c>LifecycleTransitionsTests</c>; here we pin the decider→table integration.
/// </summary>
public sealed class LifecycleRejectionTests
{
    private static readonly DateOnly Start = new(2026, 1, 15);
    private static readonly DateOnly Maturity = new(2027, 1, 15);

    [Fact]
    public async Task MatureAsync_rejects_maturing_a_matured_deposit()
    {
        var depositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, MaturedAtMaturityStream(depositId));

        var ex = await Assert.ThrowsAsync<DomainRejectedException>(() =>
            service.MatureAsync(new MatureDepositCommand(
                depositId, new DateTimeOffset(2028, 1, 15, 0, 0, 0, TimeSpan.Zero), "PT50-DDA-001", "test")));

        Assert.Contains("Matured", ex.Message);
        Assert.Contains("Mature", ex.Message); // names the illegal transition
    }

    [Fact]
    public async Task PayInterestAsync_rejects_paying_a_coupon_on_a_matured_deposit()
    {
        var depositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, MaturedAtMaturityStream(depositId));

        var ex = await Assert.ThrowsAsync<DomainRejectedException>(() =>
            service.PayInterestAsync(new PayInterestCommand(
                depositId, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), "PT50-DDA-001", "test")));

        // Rejected by the lifecycle gate (closed deposit), not by the PERIODIC-variant check that
        // follows it — the transition gate fires first.
        Assert.Contains("Matured", ex.Message);
        Assert.Contains("PayInterest", ex.Message);
    }

    [Fact]
    public async Task MatureAsync_allows_maturing_an_active_deposit()
    {
        // Sanity: the SAME gate lets the legal Active→Matured transition through (it reaches the
        // settlement leg). The Active stream is a bare constitution; NullSink discards the append.
        var depositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, ActiveStream(depositId), failOnSettle: false);

        // Does not throw: the legal transition proceeds past the gate.
        await service.MatureAsync(new MatureDepositCommand(
            depositId, new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero), "PT50-DDA-001", "test"));
    }

    // ---- seed streams ---------------------------------------------------------------------------

    /// <summary>A bare constituted AT_MATURITY deposit → folds to Active.</summary>
    private static DomainEvent[] ActiveStream(Guid depositId) =>
    [
        new DepositConstituted(
            depositId, new Money(1_000_000), 300, "rs-1", 365, Start, Maturity, "AT_MATURITY", "NONE"),
    ];

    /// <summary>A constituted + matured AT_MATURITY deposit → folds to the terminal Matured state.</summary>
    private static DomainEvent[] MaturedAtMaturityStream(Guid depositId) =>
    [
        new DepositConstituted(
            depositId, new Money(1_000_000), 300, "rs-1", 365, Start, Maturity, "AT_MATURITY", "NONE"),
        new InterestAccrued(new Money(30_417), Maturity),
        new WithholdingApplied(new Money(8_517), new Money(21_900)),
        new DepositMatured(new Money(1_000_000), new Money(21_900), new Money(1_021_900), Maturity),
    ];

    /// <summary>Compose the durable runtime over an in-memory store seeded with <paramref name="stream"/>,
    /// a discard sink, and a rate-sheet store + settlement port that fail if touched (a rejection must
    /// not resolve a sheet or settle). Only LoadAsync is exercised on the legal-transition path.</summary>
    private static TermDepositConstitutionService ServiceOverStream(
        Guid depositId, DomainEvent[] stream, bool failOnSettle = true)
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

/// <summary>A read-only in-memory event store seeded with one stream's events, encoded the same way
/// the runtime encodes on append. AppendAsync is never called (the runtime uses a NullSink); only
/// LoadAsync is meaningful, so the write path throws to make any accidental use loud.</summary>
internal sealed class InMemoryEventStore : IEventStore
{
    private readonly Guid _streamId;
    private readonly IReadOnlyList<EventEnvelope> _envelopes;

    public InMemoryEventStore(
        Guid streamId, IReadOnlyList<DomainEvent> events, IEventSerializer serializer, HandlerRegistry registry)
    {
        _streamId = streamId;
        var envelopes = new List<EventEnvelope>(events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            if (!registry.TryResolveByPayloadType(events[i].GetType(), out var registration))
            {
                throw new InvalidOperationException($"No handler for {events[i].GetType()}");
            }

            var encoded = serializer.Encode(events[i]);
            envelopes.Add(new EventEnvelope(
                EventId: Guid.NewGuid(), StreamId: streamId, SequenceNumber: i,
                EventType: registration.EventType, EventSchemaVersion: registration.EventSchemaVersion,
                Family: "term_deposit", PartitionKey: streamId, PackVersion: "pt.2026.1",
                SchemaVersion: "term_deposit@2026.1", ValidTime: default, TransactionTime: default,
                CausationId: null, CorrelationId: null, Actor: "test",
                Payload: encoded.Bytes, PayloadSchemaId: encoded.SchemaId));
        }

        _envelopes = envelopes;
    }

    public async IAsyncEnumerable<EventEnvelope> LoadAsync(
        Guid streamId, long fromSequence = 0, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (streamId != _streamId)
        {
            yield break;
        }

        foreach (var envelope in _envelopes)
        {
            if (envelope.SequenceNumber >= fromSequence)
            {
                yield return envelope;
            }
        }

        await Task.CompletedTask;
    }

    public Task AppendAsync(
        Guid streamId, long expectedVersion, IReadOnlyList<EventEnvelope> events,
        IReadOnlyList<OutboxRow> outboxRows, Guid? commandId = null, CancellationToken ct = default) =>
        throw new InvalidOperationException("InMemoryEventStore is read-only; the runtime uses a NullSink.");

    public Task<IReadOnlyList<Guid>> ReadStreamIdsAsync(string family, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([_streamId]);
}

/// <summary>A rate-sheet store that fails if resolved — a lifecycle rejection must never reach it.</summary>
internal sealed class ThrowingRateSheetStore : IRateSheetStore
{
    public Task<RateSheetResolution?> ResolveAsync(string family, DateTimeOffset asOf, CancellationToken ct = default) =>
        throw new InvalidOperationException("rate sheet resolved on a rejected command");

    public Task InsertAsync(RateSheet sheet, CancellationToken ct = default) =>
        throw new InvalidOperationException("rate sheet inserted on a rejected command");

    public Task<RateSheet?> TryGetAsync(string rateSheetVersionId, CancellationToken ct = default) =>
        throw new InvalidOperationException("rate sheet fetched on a rejected command");
}

/// <summary>A settlement port that fails if invoked when <c>failOnSettle</c> — a rejected command
/// must never move money. The legal-transition sanity test passes <c>failOnSettle: false</c>.</summary>
internal sealed class ThrowingSettlementPort(bool failOnSettle) : ISettlementPort
{
    public Task SettleAsync(SettlementInstruction instruction, CancellationToken ct = default) =>
        failOnSettle
            ? throw new InvalidOperationException("settlement attempted on a rejected command")
            : Task.CompletedTask;
}
