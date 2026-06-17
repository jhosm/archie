using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// GDPR Article 17 right-to-be-forgotten — the service half (bd babelstone-nzw6). These are PURE unit
/// tests (no Docker): a deposit's prior events are seeded into an in-memory <see cref="IEventStore"/> so
/// the rehydrated position folds to a given lifecycle, and the service is wired with a
/// <see cref="NullSink"/> (the append is discarded) and rate-sheet/settlement stubs that throw if touched
/// (erasure must never resolve a sheet or move money). They pin: erasure is ACCEPTED from a live or a
/// business-closed deposit (it still holds the subject's PII), and REJECTED from a non-existent (Pending)
/// or already-erased deposit — the latter being the idempotency guard. The actual key crypto-shred is the
/// HOST's job (the OpenBao boundary); this layer only writes the structural audit fact, never any PII.
/// </summary>
public sealed class PersonalDataErasureTests
{
    private static readonly DateOnly Start = new(2026, 1, 15);
    private static readonly DateOnly Maturity = new(2027, 1, 15);
    private static readonly DateTimeOffset ErasedAt = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    // A salted one-way pseudonym (ADR-IC-016 §8) the HOST would derive — the only subject reference that
    // ever reaches this layer; never the raw subject id (ADR-PC-004 §P2).
    private const string Pseudonym = "a1b2c3d4e5f60718";

    [Fact]
    public async Task Erasure_is_accepted_on_an_active_deposit()
    {
        var depositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, ActiveStream(depositId));

        // Does not throw: a live deposit still holds the subject's PII, so erasure is legal. The append
        // is discarded by the NullSink; the legality gate is what is under test.
        await service.ErasePersonalDataAsync(Command(depositId));
    }

    [Fact]
    public async Task Erasure_is_accepted_on_a_business_closed_deposit()
    {
        // A matured (business-terminal) deposit still carries the subject's PII until erased — GDPR
        // erasure must reach it even though every BUSINESS transition is closed.
        var depositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, MaturedAtMaturityStream(depositId));

        await service.ErasePersonalDataAsync(Command(depositId));
    }

    [Fact]
    public async Task Erasure_is_rejected_on_a_non_existent_deposit()
    {
        // Pending (no events) → no deposit exists to erase.
        var depositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, []);

        var ex = await Assert.ThrowsAsync<DomainRejectedException>(
            () => service.ErasePersonalDataAsync(Command(depositId)));

        Assert.Contains("Pending", ex.Message);
        Assert.Contains("Erase", ex.Message); // names the illegal transition
    }

    [Fact]
    public async Task Erasing_an_already_erased_deposit_is_rejected_the_idempotency_guard()
    {
        var depositId = Guid.NewGuid();
        var service = ServiceOverStream(depositId, ErasedStream(depositId));

        var ex = await Assert.ThrowsAsync<DomainRejectedException>(
            () => service.ErasePersonalDataAsync(Command(depositId)));

        // Re-erasure is an illegal transition from the terminal Erased state — the idempotency guard.
        Assert.Contains("Erased", ex.Message);
        Assert.Contains("Erase", ex.Message);
    }

    // ---- seed streams ---------------------------------------------------------------------------

    private static DomainEvent[] ActiveStream(Guid depositId) =>
    [
        new DepositConstituted(
            depositId, new Money(1_000_000), 300, "rs-1", 365, Start, Maturity, "AT_MATURITY", "NONE"),
    ];

    private static DomainEvent[] MaturedAtMaturityStream(Guid depositId) =>
    [
        new DepositConstituted(
            depositId, new Money(1_000_000), 300, "rs-1", 365, Start, Maturity, "AT_MATURITY", "NONE"),
        new InterestAccrued(new Money(30_417), Maturity),
        new WithholdingApplied(new Money(8_517), new Money(21_900)),
        new DepositMatured(new Money(1_000_000), new Money(21_900), new Money(1_021_900), Maturity),
    ];

    private static DomainEvent[] ErasedStream(Guid depositId) =>
    [
        new DepositConstituted(
            depositId, new Money(1_000_000), 300, "rs-1", 365, Start, Maturity, "AT_MATURITY", "NONE"),
        new PersonalDataErasureRequested(depositId, Pseudonym, new DateOnly(2026, 5, 1), "GDPR_ARTICLE_17"),
    ];

    private static ErasePersonalDataCommand Command(Guid depositId) =>
        new(depositId, Pseudonym, ErasedAt, "GDPR_ARTICLE_17", "test", CommandId: Guid.NewGuid());

    /// <summary>Compose the durable runtime over an in-memory store seeded with <paramref name="stream"/>,
    /// a discard sink, and rate-sheet/settlement stubs that throw if touched (erasure never resolves a
    /// sheet or settles). Only LoadAsync is exercised — the append is discarded by the NullSink.</summary>
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
