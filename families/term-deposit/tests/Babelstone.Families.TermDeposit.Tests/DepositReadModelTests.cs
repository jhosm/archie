using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Families.TermDeposit.Tests;

/// <summary>
/// D.4 (babelstone-yfr2) CQRS read-model tests: the term-deposit family materialises the
/// denormalized <c>read_model.deposits</c> row (ADR-IC-005) by folding the SAME deposit-position
/// state the live read path computes. These cover the runner's runtime properties — handler-skip,
/// at-least-once idempotency (the ADR-IC-005 §P2 monotonicity guard), and the byte-identical
/// rebuild that the truncate-and-refold path (§P5) relies on — plus the pure state→row mapper.
/// </summary>
public sealed class DepositReadModelTests
{
    [Fact]
    public async Task Constituting_materialises_the_read_model_row()
    {
        var store = new InMemoryReadModelStore();
        var runner = ReadModelRunner(store);
        var streamId = Guid.NewGuid();

        await runner.ApplyAsync(Envelope(streamId, 0, "term_deposit.DepositConstituted",
            new DepositConstituted(streamId, new Money(1_000_000), 300, "pt-deposits-2026.1", 365,
                new DateOnly(2026, 1, 15), new DateOnly(2027, 1, 15), "AT_MATURITY", "NONE")));

        var row = await store.GetAsync(streamId);
        Assert.NotNull(row);
        Assert.Equal("engine", row.Sor);                       // ADR-PC-018 §6.2 routing truth
        Assert.Equal(1_000_000, row.PrincipalCents);
        Assert.Equal(300, row.TanBasisPoints);
        Assert.Equal(new DateOnly(2027, 1, 15), row.MaturityDate);
        Assert.Equal("Active", row.Lifecycle);
        Assert.Equal(0, row.LastSequence);
        // ADR-IC-005 §P3 / ADR-PC-010 §P5: last_updated is the event's transaction_time, not a clock.
        Assert.Equal(Origin, row.LastUpdated);
    }

    [Fact]
    public async Task Constituting_denormalizes_the_catalogue_product_code()
    {
        // bd babelstone-v794: DepositConstituted carries the catalogue product_code; the position
        // fold copies it; the read model denormalizes it under its honest name. A populated code
        // round-trips onto the row, and a pre-v794 constitution (the Avro "" default) surfaces "".
        var store = new InMemoryReadModelStore();
        var runner = ReadModelRunner(store);

        var withCode = Guid.NewGuid();
        await runner.ApplyAsync(Envelope(withCode, 0, "term_deposit.DepositConstituted",
            new DepositConstituted(withCode, new Money(1_000_000), 300, "pt-deposits-2026.1", 365,
                new DateOnly(2026, 1, 15), new DateOnly(2027, 1, 15), "AT_MATURITY", "NONE",
                PaymentPeriodMonths: 0, ProductCode: "dpz_pt_12m_juros_venc")));

        var preV794 = Guid.NewGuid();
        await runner.ApplyAsync(Envelope(preV794, 0, "term_deposit.DepositConstituted",
            new DepositConstituted(preV794, new Money(1_000_000), 300, "pt-deposits-2026.1", 365,
                new DateOnly(2026, 1, 15), new DateOnly(2027, 1, 15), "AT_MATURITY", "NONE")));

        Assert.Equal("dpz_pt_12m_juros_venc", (await store.GetAsync(withCode))!.ProductCode);
        Assert.Equal("", (await store.GetAsync(preV794))!.ProductCode);
    }

    [Fact]
    public async Task Runner_skips_events_the_position_fold_does_not_handle()
    {
        var store = new InMemoryReadModelStore();
        var runner = ReadModelRunner(store);
        var streamId = Guid.NewGuid();

        // No DepositConstituted yet: a bare WithholdingApplied is not constituting, but the position
        // fold DOES handle it. To assert the skip, use an event type the position registry has no
        // handler for is not possible here (the position folds all family events); instead assert a
        // truly unhandled type name leaves no row.
        await runner.ApplyAsync(Envelope(streamId, 0, "other_family.Unknown", new WithholdingApplied(new Money(1), new Money(1))));

        Assert.Null(await store.GetAsync(streamId));
    }

    [Fact]
    public async Task Runner_is_idempotent_under_at_least_once_redelivery()
    {
        var store = new InMemoryReadModelStore();
        var runner = ReadModelRunner(store);
        var streamId = Guid.NewGuid();

        var seq0 = Envelope(streamId, 0, "term_deposit.DepositConstituted",
            new DepositConstituted(streamId, new Money(1_000_000), 300, "pt-deposits-2026.1", 365,
                new DateOnly(2026, 1, 15), new DateOnly(2027, 1, 15), "AT_MATURITY", "NONE"));
        var seq1 = Envelope(streamId, 1, "term_deposit.InterestAccrued",
            new InterestAccrued(new Money(30_417), new DateOnly(2027, 1, 15)));

        await runner.ApplyAsync(seq0);
        await runner.ApplyAsync(seq1);
        await runner.ApplyAsync(seq1); // crash-replay of seq1 — the §P2 guard must drop it

        var row = await store.GetAsync(streamId);
        Assert.Equal(1, row!.LastSequence);
        // The accrual folded exactly once: re-applying seq1 did not double-count.
        Assert.Equal(new Money(30_417).Cents, FoldedPosition(store, streamId).AccruedGrossInterest.Cents);
    }

    [Fact]
    public async Task Rebuild_reproduces_a_byte_identical_read_model_row()
    {
        // ADR-IC-005 §P5 + ADR-PC-010 §P5: folds are deterministic and every stamp is event-derived,
        // so re-folding the same events (a truncate-and-refold rebuild) yields a byte-for-byte
        // identical denormalized row — the rebuild-determinism gate for the read model.
        var streamId = Guid.NewGuid();
        var events = new[]
        {
            Envelope(streamId, 0, "term_deposit.DepositConstituted",
                new DepositConstituted(streamId, new Money(1_000_000), 300, "pt-deposits-2026.1", 365,
                    new DateOnly(2026, 1, 15), new DateOnly(2027, 1, 15), "AT_MATURITY", "NONE")),
            Envelope(streamId, 1, "term_deposit.InterestAccrued", new InterestAccrued(new Money(30_417), new DateOnly(2027, 1, 15))),
            Envelope(streamId, 2, "term_deposit.WithholdingApplied", new WithholdingApplied(new Money(8_517), new Money(21_900))),
            Envelope(streamId, 3, "term_deposit.DepositMatured",
                new DepositMatured(new Money(1_000_000), new Money(21_900), new Money(1_021_900), new DateOnly(2027, 1, 15))),
        };

        var first = new InMemoryReadModelStore();
        var firstRunner = ReadModelRunner(first);
        foreach (var e in events)
        {
            await firstRunner.ApplyAsync(e);
        }

        var second = new InMemoryReadModelStore();
        var secondRunner = ReadModelRunner(second);
        foreach (var e in events)
        {
            await secondRunner.ApplyAsync(e);
        }

        var a = await first.GetAsync(streamId);
        var b = await second.GetAsync(streamId);
        Assert.NotNull(a);
        Assert.NotNull(b);
        // Every field is event-derived: the two rebuilds agree on the body bytes, the freshness pair,
        // and every denormalized query column. The whole row is compared field-by-field rather than
        // by record equality — ReadOnlyMemory<byte> equality is by reference, so the Detail bytes are
        // asserted with ToArray() (the same reason the bitemporal store tests compare payloads thus).
        Assert.Equal(a.Detail.ToArray(), b.Detail.ToArray());
        Assert.Equal(a with { Detail = default }, b with { Detail = default }); // every non-byte field value-equal
        // Folded the full lifecycle: Matured with the canonical payout.
        Assert.Equal("Matured", a.Lifecycle);
        Assert.Equal(1_021_900, a.TotalPayoutCents);
        Assert.Equal(3, a.LastSequence);
    }

    [Fact]
    public void Map_to_read_model_is_pure_and_event_derived()
    {
        // The mapper is a pure function of (state, event-derived context): same input → same output,
        // no clock, no I/O. Two calls with identical input produce equal rows.
        var streamId = Guid.NewGuid();
        var position = DepositPosition.Empty with
        {
            DepositId = streamId,
            Principal = new Money(500_000),
            TanBasisPoints = 250,
            RateSheetVersionId = "rs-x",
            ProductCode = "dpz_pt_12m_juros_venc",
            MaturityDate = new DateOnly(2027, 5, 1),
            InterestVariant = "PERIODIC",
            AutoRenewalPolicy = "SAME_TERM_SAME_RATE",
            PaymentPeriodMonths = 3,
            AccruedGrossInterest = new Money(1_234),
            WithholdingToDate = new Money(345),
            NetInterest = new Money(889),
            CouponsPaid = 2,
            TotalPayout = new Money(512_345),
            Lifecycle = DepositLifecycle.Active,
        };
        var fold = new ReadModelFold<DepositPosition>(position, streamId, 7, Origin);

        var a = TermDepositProjectionModule.MapToReadModel(fold);
        var b = TermDepositProjectionModule.MapToReadModel(fold);

        // ReadOnlyMemory<byte> compares by reference, so equate the Detail bytes explicitly and the
        // rest of the record by value.
        Assert.Equal(a.Detail.ToArray(), b.Detail.ToArray());
        Assert.Equal(a with { Detail = default }, b with { Detail = default });
        Assert.Equal("engine", a.Sor);
        Assert.Equal(7, a.LastSequence);
        Assert.Equal(Origin, a.LastUpdated);
        Assert.Equal(512_345, a.TotalPayoutCents);
        Assert.Equal("dpz_pt_12m_juros_venc", a.ProductCode); // bd babelstone-v794
        // D.4 single-resource enrichment: the full displayable position projects onto the row (the
        // same fold the live read path computes), so GET /v1/deposits/{id} need not fold for these.
        Assert.Equal("SAME_TERM_SAME_RATE", a.AutoRenewalPolicy);
        Assert.Equal(3, a.PaymentPeriodMonths);
        Assert.Equal(1_234, a.AccruedGrossInterestCents);
        Assert.Equal(345, a.WithholdingToDateCents);
        Assert.Equal(889, a.NetInterestCents);
        Assert.Equal(2, a.CouponsPaid);
    }

    // --- helpers ---

    private static IProjectionRunner ReadModelRunner(IReadModelStore<DepositReadModelRow> store) =>
        new TermDepositProjectionModule().CreateReadModelRunner(new ReadModelInfra<DepositReadModelRow>(store, new JsonEventSerializer()));

    private static DepositPosition FoldedPosition(InMemoryReadModelStore store, Guid streamId)
    {
        var row = store.GetAsync(streamId).GetAwaiter().GetResult()!;
        return new JsonStateSerializer<DepositPosition>().Deserialize(row.Detail);
    }

    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static EventEnvelope Envelope(Guid streamId, long sequence, string eventType, DomainEvent @event) => new(
        EventId: Guid.NewGuid(),
        StreamId: streamId,
        SequenceNumber: sequence,
        EventType: eventType,
        EventSchemaVersion: 1,
        Family: "term_deposit",
        PartitionKey: streamId,
        PackVersion: "pt.2026.1",
        SchemaVersion: "term_deposit@2026.1",
        ValidTime: Origin.AddDays(sequence),
        TransactionTime: Origin,
        CausationId: null,
        CorrelationId: null,
        Actor: "test",
        Payload: new JsonEventSerializer().Encode(@event).Bytes,
        PayloadSchemaId: 0);

    /// <summary>
    /// A minimal in-memory <see cref="IReadModelStore{TRow}"/> over the family's
    /// <see cref="DepositReadModelRow"/> for the family-side runner tests — enough to exercise the
    /// UPSERT monotonicity guard, point lookup, and truncate. The real Postgres store
    /// (<c>PostgresDepositReadModelStore</c>) is integration-tested in the family's Application tests.
    /// </summary>
    private sealed class InMemoryReadModelStore : IReadModelStore<DepositReadModelRow>
    {
        private readonly Dictionary<Guid, DepositReadModelRow> _rows = [];

        public Task UpsertAsync(DepositReadModelRow row, CancellationToken ct = default)
        {
            // ADR-IC-005 §P2: overwrite only on a strictly higher sequence.
            if (!_rows.TryGetValue(row.StreamId, out var existing) || existing.LastSequence < row.LastSequence)
            {
                _rows[row.StreamId] = row;
            }

            return Task.CompletedTask;
        }

        public Task<DepositReadModelRow?> GetAsync(Guid streamId, CancellationToken ct = default) =>
            Task.FromResult(_rows.TryGetValue(streamId, out var row) ? row : null);

        public Task TruncateAsync(CancellationToken ct = default)
        {
            _rows.Clear();
            return Task.CompletedTask;
        }
    }
}
