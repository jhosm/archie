using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// Tests for the spine-owned <see cref="MovementLedgerProjector"/> and the <see cref="IMovementBearing"/>
/// seam (ADR-PC-032 §A1 / §95 read side). In plain English: these prove the engine can build an
/// account-statement and balance off whatever money-moving events get appended, WITHOUT knowing which
/// product family produced them — it discovers the movements through the one-member
/// <see cref="IMovementBearing"/> interface and folds them into an <c>account_ref</c>-keyed ledger. The
/// tests cover the four things the read model must get right: only Movement-bearing events contribute, the
/// ledger is account-keyed (and aggregates across streams/families), the balance signs Credit/Debit
/// correctly, and re-applying an event (the at-least-once drainer) never double-counts.
/// </summary>
public sealed class MovementLedgerProjectorTests
{
    // A family-agnostic, test-only Movement-bearing event: the projector reads ONLY the IMovementBearing
    // seam (a spine type) + the Movement atom, never a family-typed shape, so any event that implements the
    // interface is foldable. Defined here (not a real family event) to keep Engine.Tests family-agnostic.
    private sealed record TestMoneyMover(IReadOnlyList<Movement> Movements) : DomainEvent, IMovementBearing;

    // An event that moves no money: it does NOT implement IMovementBearing, so the projector skips it.
    private sealed record TestNonMover(string Note) : DomainEvent;

    private static Movement Move(
        string accountRef,
        SettlementDirection direction,
        long amountCents,
        MovementOperation operation = MovementOperation.Disburse,
        MovementOrigin origin = MovementOrigin.Originated) => new(
            AccountRef: accountRef,
            Direction: direction,
            Amount: new Money(amountCents),
            ValueDate: new DateOnly(2026, 6, 25),
            Operation: operation,
            Origin: origin,
            CommandId: Guid.NewGuid());

    [Fact]
    public async Task A_single_credit_movement_folds_to_a_positive_balance_and_one_statement_line()
    {
        var store = new InMemoryMovementLedgerStore();
        var projector = new MovementLedgerProjector(store);
        var streamId = Guid.NewGuid();

        await projector.ApplyAsync(
            streamId,
            sequenceNumber: 0,
            new TestMoneyMover([Move("acct-1", SettlementDirection.Credit, 10_000)]));

        Assert.Equal(10_000, await store.GetBalanceCentsAsync("acct-1"));
        var line = Assert.Single(await store.GetStatementAsync("acct-1"));
        Assert.Equal(streamId, line.StreamId);
        Assert.Equal(0, line.MovementIndex);
        Assert.Equal("Credit", line.Direction);
        Assert.Equal(10_000, line.AmountCents);
        Assert.Equal("Disburse", line.Operation);
        Assert.Equal("Originated", line.Origin);
    }

    [Fact]
    public async Task A_debit_movement_subtracts_from_the_account_balance()
    {
        var store = new InMemoryMovementLedgerStore();
        var projector = new MovementLedgerProjector(store);

        await projector.ApplyAsync(
            Guid.NewGuid(), 0, new TestMoneyMover([Move("acct-1", SettlementDirection.Credit, 10_000)]));
        await projector.ApplyAsync(
            Guid.NewGuid(), 0,
            new TestMoneyMover([Move("acct-1", SettlementDirection.Debit, 3_000, MovementOperation.CollectInstallment)]));

        // Credit 10_000 then Debit 3_000 → net 7_000 (the balance signs by direction relative to the account).
        Assert.Equal(7_000, await store.GetBalanceCentsAsync("acct-1"));
        Assert.Equal(2, (await store.GetStatementAsync("acct-1")).Count);
    }

    [Fact]
    public async Task A_multi_movement_event_folds_every_leg_as_a_distinct_line_keyed_by_carrier_index()
    {
        var store = new InMemoryMovementLedgerStore();
        var projector = new MovementLedgerProjector(store);
        var streamId = Guid.NewGuid();

        // A renewal-shaped event: one append carrying a rollover-debit AND an interest-credit against the
        // same account (ADR-PC-032 §A3) — both must become distinct ledger lines under one (stream, sequence).
        await projector.ApplyAsync(
            streamId,
            sequenceNumber: 4,
            new TestMoneyMover(
            [
                Move("acct-1", SettlementDirection.Debit, 20_000, MovementOperation.RolloverDebit),
                Move("acct-1", SettlementDirection.Credit, 500, MovementOperation.PayCoupon),
            ]));

        Assert.Equal(-19_500, await store.GetBalanceCentsAsync("acct-1"));
        var lines = await store.GetStatementAsync("acct-1");
        Assert.Equal(2, lines.Count);
        // The carrier index disambiguates the two legs of the one event, so the idempotency key is unique.
        Assert.Equal([0, 1], lines.Select(l => l.MovementIndex).OrderBy(i => i).ToArray());
    }

    [Fact]
    public async Task The_ledger_is_account_keyed_so_distinct_accounts_stay_separate()
    {
        var store = new InMemoryMovementLedgerStore();
        var projector = new MovementLedgerProjector(store);

        await projector.ApplyAsync(
            Guid.NewGuid(), 0,
            new TestMoneyMover(
            [
                Move("acct-1", SettlementDirection.Credit, 10_000),
                Move("acct-2", SettlementDirection.Credit, 250),
            ]));

        Assert.Equal(10_000, await store.GetBalanceCentsAsync("acct-1"));
        Assert.Equal(250, await store.GetBalanceCentsAsync("acct-2"));
        Assert.Empty(await store.GetStatementAsync("acct-unknown"));
        Assert.Equal(0, await store.GetBalanceCentsAsync("acct-unknown"));
    }

    [Fact]
    public async Task Movements_against_one_account_from_different_streams_aggregate()
    {
        var store = new InMemoryMovementLedgerStore();
        var projector = new MovementLedgerProjector(store);

        // A single account_ref can receive movements from many streams (across families): a disbursement
        // credit on one stream, an installment debit on another. The account-keyed ledger sums them.
        await projector.ApplyAsync(
            Guid.NewGuid(), 0, new TestMoneyMover([Move("shared-acct", SettlementDirection.Credit, 100_000)]));
        await projector.ApplyAsync(
            Guid.NewGuid(), 7,
            new TestMoneyMover([Move("shared-acct", SettlementDirection.Debit, 4_000, MovementOperation.CollectInstallment)]));

        Assert.Equal(96_000, await store.GetBalanceCentsAsync("shared-acct"));
    }

    [Fact]
    public async Task A_non_movement_bearing_event_contributes_nothing()
    {
        var store = new InMemoryMovementLedgerStore();
        var projector = new MovementLedgerProjector(store);

        await projector.ApplyAsync(Guid.NewGuid(), 0, new TestNonMover("no money moved here"));

        Assert.Empty(await store.GetStatementAsync("acct-1"));
    }

    [Fact]
    public async Task A_movement_bearing_event_with_an_empty_carrier_contributes_nothing()
    {
        var store = new InMemoryMovementLedgerStore();
        var projector = new MovementLedgerProjector(store);

        await projector.ApplyAsync(Guid.NewGuid(), 0, new TestMoneyMover([]));

        Assert.Empty(await store.GetStatementAsync("acct-1"));
    }

    [Fact]
    public async Task Re_applying_the_same_event_is_idempotent_under_at_least_once_delivery()
    {
        var store = new InMemoryMovementLedgerStore();
        var projector = new MovementLedgerProjector(store);
        var streamId = Guid.NewGuid();
        var @event = new TestMoneyMover([Move("acct-1", SettlementDirection.Credit, 10_000)]);

        // The at-least-once drainer may re-deliver an event after a crash between the ledger write and the
        // checkpoint advance. The append is idempotent on (stream_id, sequence_number, movement_index), so
        // re-applying the SAME event re-inserts the same line as a no-op — no double count.
        await projector.ApplyAsync(streamId, 0, @event);
        await projector.ApplyAsync(streamId, 0, @event);

        Assert.Equal(10_000, await store.GetBalanceCentsAsync("acct-1"));
        Assert.Single(await store.GetStatementAsync("acct-1"));
    }

    [Fact]
    public async Task Truncate_clears_the_ledger_for_a_rebuild()
    {
        var store = new InMemoryMovementLedgerStore();
        var projector = new MovementLedgerProjector(store);

        await projector.ApplyAsync(
            Guid.NewGuid(), 0, new TestMoneyMover([Move("acct-1", SettlementDirection.Credit, 10_000)]));
        await store.TruncateAsync();

        Assert.Empty(await store.GetStatementAsync("acct-1"));
        Assert.Equal(0, await store.GetBalanceCentsAsync("acct-1"));
    }

    /// <summary>
    /// An in-memory <see cref="IMovementLedgerStore"/> test double, mirroring the
    /// <see cref="PostgresMovementLedgerStore"/> contract: idempotent append on the
    /// <c>(stream_id, sequence_number, movement_index)</c> identity, a direction-signed balance sum, and
    /// account-scoped statement reads. Kept in the test project (the same convention as the other in-memory
    /// storage doubles), so the production assembly ships only the real Postgres store.
    /// </summary>
    private sealed class InMemoryMovementLedgerStore : IMovementLedgerStore
    {
        private readonly List<MovementLedgerEntry> _entries = [];

        public Task AppendAsync(IReadOnlyList<MovementLedgerEntry> entries, CancellationToken ct = default)
        {
            foreach (var entry in entries)
            {
                var alreadyApplied = _entries.Any(e =>
                    e.StreamId == entry.StreamId
                    && e.SequenceNumber == entry.SequenceNumber
                    && e.MovementIndex == entry.MovementIndex);
                if (!alreadyApplied)
                {
                    _entries.Add(entry);
                }
            }

            return Task.CompletedTask;
        }

        public Task<long> GetBalanceCentsAsync(string accountRef, CancellationToken ct = default)
        {
            var balance = _entries
                .Where(e => e.AccountRef == accountRef)
                .Sum(e => e.Direction == "Credit" ? e.AmountCents : -e.AmountCents);
            return Task.FromResult(balance);
        }

        public Task<IReadOnlyList<MovementLedgerEntry>> GetStatementAsync(
            string accountRef, CancellationToken ct = default)
        {
            IReadOnlyList<MovementLedgerEntry> statement = _entries
                .Where(e => e.AccountRef == accountRef)
                .OrderBy(e => e.StreamId)
                .ThenBy(e => e.SequenceNumber)
                .ThenBy(e => e.MovementIndex)
                .ToList();
            return Task.FromResult(statement);
        }

        public Task TruncateAsync(CancellationToken ct = default)
        {
            _entries.Clear();
            return Task.CompletedTask;
        }
    }
}
