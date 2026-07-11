using Babelstone.Engine;
using Babelstone.EventStore;
using Xunit;

namespace Babelstone.Families.CurrentAccount.Application.Tests;

/// <summary>
/// The movement-history read surface (GET /v1/accounts/{id}/movements, bd babelstone-u79p.11): proves the
/// <see cref="AccountBalanceReader.GetStatementAsync"/> passthrough returns the account's recorded movement
/// lines, in stable (stream, sequence, index) order, off the SAME spine-owned movement ledger the accounting
/// balance sums (ADR-PC-032). In plain English: the balance is the fold's rollup; the statement is the fold's
/// lines, and this exposes the lines with no new folding logic — a thin delegate to
/// <see cref="IMovementLedgerStore.GetStatementAsync"/>. The pattern mirrors the engine
/// MovementLedgerProjectorTests (an in-memory ledger store double); the hold-store dependency of the reader is
/// a throwing stub because the statement read never touches holds.
/// </summary>
public sealed class AccountMovementStatementReadTests
{
    private static MovementLedgerEntry Line(
        string accountRef,
        Guid streamId,
        long sequence,
        int index,
        string direction,
        long amountCents,
        string operation = "Disburse",
        string origin = "Originated") => new(
            AccountRef: accountRef,
            StreamId: streamId,
            SequenceNumber: sequence,
            MovementIndex: index,
            Direction: direction,
            AmountCents: amountCents,
            ValueDate: new DateOnly(2026, 3, 1),
            Operation: operation,
            Origin: origin,
            CommandId: Guid.NewGuid());

    [Fact]
    public async Task GetStatement_returns_the_recorded_movement_lines_for_the_account()
    {
        var store = new InMemoryMovementLedgerStore();
        var reader = new AccountBalanceReader(store, new UnusedHoldStore());
        var streamId = Guid.NewGuid();
        await store.AppendAsync(
        [
            Line("acct-1", streamId, sequence: 0, index: 0, "Credit", 150_000),
            Line("acct-1", streamId, sequence: 1, index: 0, "Debit", 30_000, operation: "CollectInstallment"),
        ]);

        var statement = await reader.GetStatementAsync("acct-1");

        Assert.Equal(2, statement.Count);
        Assert.Equal("Credit", statement[0].Direction);
        Assert.Equal(150_000, statement[0].AmountCents);
        Assert.Equal("Disburse", statement[0].Operation);
        Assert.Equal("Debit", statement[1].Direction);
        Assert.Equal("CollectInstallment", statement[1].Operation);
    }

    [Fact]
    public async Task GetStatement_is_account_keyed_so_another_accounts_lines_do_not_leak()
    {
        var store = new InMemoryMovementLedgerStore();
        var reader = new AccountBalanceReader(store, new UnusedHoldStore());
        await store.AppendAsync(
        [
            Line("acct-1", Guid.NewGuid(), 0, 0, "Credit", 10_000),
            Line("acct-2", Guid.NewGuid(), 0, 0, "Credit", 999),
        ]);

        var statement = await reader.GetStatementAsync("acct-1");

        var only = Assert.Single(statement);
        Assert.Equal("acct-1", only.AccountRef);
        Assert.Equal(10_000, only.AmountCents);
    }

    [Fact]
    public async Task GetStatement_is_empty_for_an_account_with_no_posted_movements()
    {
        var store = new InMemoryMovementLedgerStore();
        var reader = new AccountBalanceReader(store, new UnusedHoldStore());

        Assert.Empty(await reader.GetStatementAsync("acct-unknown"));
    }

    /// <summary>
    /// An in-memory <see cref="IMovementLedgerStore"/> test double mirroring the
    /// <see cref="PostgresMovementLedgerStore"/> contract — idempotent append on the
    /// <c>(stream_id, sequence_number, movement_index)</c> identity and an account-scoped, stably-ordered
    /// statement read. Only the surface this read lane needs; kept in the test project so the production
    /// assembly ships only the real Postgres store.
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
            => Task.FromResult(_entries
                .Where(e => e.AccountRef == accountRef)
                .Sum(e => e.Direction == "Credit" ? e.AmountCents : -e.AmountCents));

        public Task<IReadOnlyList<OverdrawnAccount>> GetOverdrawnAccountsAsync(CancellationToken ct = default)
            => throw new NotSupportedException("the statement read lane does not exercise the overdrawn read.");

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

    /// <summary>
    /// A throwing <see cref="IAccountHoldStore"/> stub: the <see cref="AccountBalanceReader"/> ctor requires
    /// a hold store, but the movement-statement read (<see cref="AccountBalanceReader.GetStatementAsync"/>)
    /// never touches holds, so every member fails loud — proving the passthrough is purely movement-ledger.
    /// </summary>
    private sealed class UnusedHoldStore : IAccountHoldStore
    {
        private static InvalidOperationException Unused([System.Runtime.CompilerServices.CallerMemberName] string member = "")
            => new($"the statement read must not touch the hold store (called {member}).");

        public Task PlaceAsync(AccountHoldRow hold, CancellationToken ct = default) => throw Unused();

        public Task PlaceLegalAsync(AccountHoldRow legalHold, CancellationToken ct = default) => throw Unused();

        public Task<HoldReleaseResult> ReleaseLegalAsync(
            string holdId, Guid releasedStreamId, long releasedSequence, CancellationToken ct = default)
            => throw Unused();

        public Task<HoldReleaseResult> CaptureAsync(
            string holdId, long capturedAmountCents, Guid releasedStreamId, long releasedSequence,
            CancellationToken ct = default) => throw Unused();

        public Task<HoldReleaseResult> ExpireAsync(
            string holdId, Guid releasedStreamId, long releasedSequence, CancellationToken ct = default)
            => throw Unused();

        public Task<long> GetActiveHoldCentsAsync(string accountRef, CancellationToken ct = default)
            => throw Unused();

        public Task<IReadOnlyList<AccountHoldRow>> GetActiveHoldsAsync(string accountRef, CancellationToken ct = default)
            => throw Unused();

        public Task<IReadOnlyList<AccountHoldRow>> GetActiveHoldsWithValueDateAtOrBeforeAsync(
            DateOnly valueDateHorizon, CancellationToken ct = default) => throw Unused();

        public Task<IReadOnlyList<AccountHoldRow>> GetActiveLegalHoldsWithExpiryAtOrBeforeAsync(
            DateOnly expiryHorizon, CancellationToken ct = default) => throw Unused();

        public Task<long> GetWindowedAuthorizationHoldCentsAsync(
            string accountRef, DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct = default)
            => throw Unused();

        public Task TruncateAsync(CancellationToken ct = default) => throw Unused();
    }
}
