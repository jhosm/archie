using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// Tests for the spine-owned <see cref="AccountHoldProjector"/> — the HOLD_LIFECYCLE_PURE gate
/// (ADR-PC-033 slots 2/4). In plain English: these prove the hold lifecycle is exactly the three
/// pure transitions <c>HoldPlaced → HoldCaptured | HoldExpired</c>, that <c>hold_id</c> makes the
/// lifecycle idempotent (a re-delivered or duplicate release folds at most once — never a
/// double-release), that a partial capture releases the remainder, and that replaying the same
/// event sequence reproduces the same hold set (replay determinism, no clock anywhere in the fold).
/// </summary>
public sealed class AccountHoldProjectorTests
{
    private static readonly DateOnly ValueDate = new(2026, 6, 25);

    private static HoldPlaced Placed(string holdId, string accountRef = "acct-1", long cents = 5_000) =>
        new(Guid.NewGuid(), holdId, accountRef, new Money(cents), ValueDate);

    private static HoldCaptured Captured(string holdId, long cents, string accountRef = "acct-1") =>
        new(Guid.NewGuid(), holdId, accountRef, new Money(cents), ValueDate.AddDays(2));

    private static HoldExpired Expired(string holdId, string accountRef = "acct-1") =>
        new(Guid.NewGuid(), holdId, accountRef, ValueDate.AddDays(7));

    [Fact]
    public async Task A_placed_hold_is_active_and_reduces_the_available_balance_fold()
    {
        var store = new InMemoryAccountHoldStore();
        var projector = new AccountHoldProjector(store);

        await projector.ApplyAsync(Guid.NewGuid(), 0, Placed("hold-1", cents: 5_000));

        Assert.Equal(5_000, await store.GetActiveHoldCentsAsync("acct-1"));
        var hold = Assert.Single(await store.GetActiveHoldsAsync("acct-1"));
        Assert.Equal("hold-1", hold.HoldId);
        Assert.Equal("ACTIVE", hold.State);
    }

    [Fact]
    public async Task A_captured_hold_leaves_the_active_set()
    {
        var store = new InMemoryAccountHoldStore();
        var projector = new AccountHoldProjector(store);

        await projector.ApplyAsync(Guid.NewGuid(), 0, Placed("hold-1", cents: 5_000));
        await projector.ApplyAsync(Guid.NewGuid(), 1, Captured("hold-1", cents: 5_000));

        Assert.Equal(0, await store.GetActiveHoldCentsAsync("acct-1"));
        Assert.Empty(await store.GetActiveHoldsAsync("acct-1"));
    }

    [Fact]
    public async Task A_partial_capture_releases_the_whole_earmark_and_records_the_captured_amount()
    {
        var store = new InMemoryAccountHoldStore();
        var projector = new AccountHoldProjector(store);

        // ADR-PC-033 slot 2: a HoldCaptured for LESS than the held amount releases the remainder —
        // the whole hold leaves the active set; only the captured cents were posted (by the capture's
        // own Movement, not by this fold).
        await projector.ApplyAsync(Guid.NewGuid(), 0, Placed("hold-1", cents: 5_000));
        await projector.ApplyAsync(Guid.NewGuid(), 1, Captured("hold-1", cents: 3_000));

        Assert.Equal(0, await store.GetActiveHoldCentsAsync("acct-1"));
        var row = store.Row("hold-1");
        Assert.Equal("CAPTURED", row.State);
        Assert.Equal(3_000, row.CapturedAmountCents);
        Assert.Equal(5_000, row.AmountCents); // the placement fact is immutable
    }

    [Fact]
    public async Task An_expired_hold_leaves_the_active_set_with_no_capture_amount()
    {
        var store = new InMemoryAccountHoldStore();
        var projector = new AccountHoldProjector(store);

        await projector.ApplyAsync(Guid.NewGuid(), 0, Placed("hold-1", cents: 5_000));
        await projector.ApplyAsync(Guid.NewGuid(), 1, Expired("hold-1"));

        Assert.Equal(0, await store.GetActiveHoldCentsAsync("acct-1"));
        var row = store.Row("hold-1");
        Assert.Equal("EXPIRED", row.State);
        Assert.Null(row.CapturedAmountCents); // nothing posted on expiry (ADR-PC-033 slot 2)
    }

    [Fact]
    public async Task A_redelivered_HoldPlaced_never_earmarks_twice()
    {
        var store = new InMemoryAccountHoldStore();
        var projector = new AccountHoldProjector(store);
        var placed = Placed("hold-1", cents: 5_000);

        // The at-least-once drive may re-deliver after a crash between apply and checkpoint; the
        // hold_id key (ADR-PC-033 slot 4) makes the re-apply a no-op.
        await projector.ApplyAsync(Guid.NewGuid(), 0, placed);
        await projector.ApplyAsync(Guid.NewGuid(), 0, placed);

        Assert.Equal(5_000, await store.GetActiveHoldCentsAsync("acct-1"));
        Assert.Single(await store.GetActiveHoldsAsync("acct-1"));
    }

    [Fact]
    public async Task A_second_capture_of_the_same_hold_is_a_no_op_never_a_double_release()
    {
        var store = new InMemoryAccountHoldStore();
        var projector = new AccountHoldProjector(store);

        await projector.ApplyAsync(Guid.NewGuid(), 0, Placed("hold-1", cents: 5_000));
        await projector.ApplyAsync(Guid.NewGuid(), 1, Captured("hold-1", cents: 5_000));
        await projector.ApplyAsync(Guid.NewGuid(), 2, Captured("hold-1", cents: 5_000));

        // The first capture's fold stands; the duplicate transitioned zero rows (slot 4).
        var row = store.Row("hold-1");
        Assert.Equal("CAPTURED", row.State);
        Assert.Equal(5_000, row.CapturedAmountCents);
    }

    [Fact]
    public async Task An_expiry_after_capture_is_a_no_op_the_terminal_state_stands()
    {
        var store = new InMemoryAccountHoldStore();
        var projector = new AccountHoldProjector(store);

        await projector.ApplyAsync(Guid.NewGuid(), 0, Placed("hold-1", cents: 5_000));
        await projector.ApplyAsync(Guid.NewGuid(), 1, Captured("hold-1", cents: 5_000));
        await projector.ApplyAsync(Guid.NewGuid(), 2, Expired("hold-1"));

        Assert.Equal("CAPTURED", store.Row("hold-1").State);
    }

    [Fact]
    public async Task A_release_for_an_unplaced_hold_folds_to_nothing()
    {
        var store = new InMemoryAccountHoldStore();
        var projector = new AccountHoldProjector(store);

        // The fold trusts its input stream (ADR-PC-033 slot 5): an unmatched release transitions
        // nothing here — the mismatch is the family's reconciliation surface, not a fold failure.
        await projector.ApplyAsync(Guid.NewGuid(), 0, Captured("hold-ghost", cents: 1_000));
        await projector.ApplyAsync(Guid.NewGuid(), 1, Expired("hold-ghost2"));

        Assert.Empty(await store.GetActiveHoldsAsync("acct-1"));
        Assert.False(store.Has("hold-ghost"));
        Assert.False(store.Has("hold-ghost2"));
    }

    [Fact]
    public async Task A_non_hold_event_contributes_nothing()
    {
        var store = new InMemoryAccountHoldStore();
        var projector = new AccountHoldProjector(store);

        await projector.ApplyAsync(Guid.NewGuid(), 0, new TestUnrelated("no hold here"));

        Assert.Empty(await store.GetActiveHoldsAsync("acct-1"));
    }

    [Fact]
    public async Task Replaying_the_same_lifecycle_sequence_reproduces_the_same_hold_set()
    {
        // HOLD_LIFECYCLE_PURE: the fold is a deterministic function of the event sequence — no
        // clock, no randomness — so folding the SAME sequence into a fresh (rebuilt) store
        // reproduces the same rows. This is the unit half of the replay gate; the Postgres
        // truncate-then-refold half lives in the integration suite (ACCOUNT_BALANCE_IS_A_FOLD).
        var stream = Guid.NewGuid();
        var events = new (long Seq, DomainEvent Event)[]
        {
            (0, Placed("hold-1", cents: 5_000)),
            (1, Placed("hold-2", "acct-2", 700)),
            (2, Captured("hold-1", cents: 3_000)),
            (3, Expired("hold-2", "acct-2")),
        };

        var first = new InMemoryAccountHoldStore();
        var second = new InMemoryAccountHoldStore();
        foreach (var store in new[] { first, second })
        {
            var projector = new AccountHoldProjector(store);
            foreach (var (seq, @event) in events)
            {
                await projector.ApplyAsync(stream, seq, @event);
            }
        }

        Assert.Equal(first.AllRows(), second.AllRows());
    }

    [Fact]
    public async Task Reset_for_rebuild_clears_the_hold_set()
    {
        var store = new InMemoryAccountHoldStore();
        var projector = new AccountHoldProjector(store);

        await projector.ApplyAsync(Guid.NewGuid(), 0, Placed("hold-1"));
        await projector.ResetForRebuildAsync();

        Assert.Empty(await store.GetActiveHoldsAsync("acct-1"));
        Assert.False(store.Has("hold-1"));
    }

    // A family-agnostic, test-only event the projector must ignore (kept local so Engine.Tests
    // stays family-agnostic).
    private sealed record TestUnrelated(string Note) : DomainEvent;

    /// <summary>
    /// An in-memory <see cref="IAccountHoldStore"/> test double mirroring the
    /// <see cref="PostgresAccountHoldStore"/> contract: placement idempotent on <c>hold_id</c>,
    /// releases transitioning ONLY an ACTIVE row, and truncate for rebuild. Kept in the test
    /// project (the same convention as the other in-memory storage doubles).
    /// </summary>
    private sealed class InMemoryAccountHoldStore : IAccountHoldStore
    {
        private readonly Dictionary<string, AccountHoldRow> _rows = new(StringComparer.Ordinal);

        public AccountHoldRow Row(string holdId) => _rows[holdId];

        public bool Has(string holdId) => _rows.ContainsKey(holdId);

        public IReadOnlyList<AccountHoldRow> AllRows() =>
            _rows.Values.OrderBy(r => r.HoldId, StringComparer.Ordinal).ToList();

        public Task PlaceAsync(AccountHoldRow hold, CancellationToken ct = default)
        {
            _rows.TryAdd(hold.HoldId, hold); // ON CONFLICT (hold_id) DO NOTHING
            return Task.CompletedTask;
        }

        public Task<bool> CaptureAsync(
            string holdId, long capturedAmountCents, Guid releasedStreamId, long releasedSequence,
            CancellationToken ct = default)
        {
            if (!_rows.TryGetValue(holdId, out var row) || row.State != "ACTIVE")
            {
                return Task.FromResult(false);
            }

            _rows[holdId] = row with
            {
                State = "CAPTURED",
                CapturedAmountCents = capturedAmountCents,
                ReleasedStreamId = releasedStreamId,
                ReleasedSequence = releasedSequence,
            };
            return Task.FromResult(true);
        }

        public Task<bool> ExpireAsync(
            string holdId, Guid releasedStreamId, long releasedSequence, CancellationToken ct = default)
        {
            if (!_rows.TryGetValue(holdId, out var row) || row.State != "ACTIVE")
            {
                return Task.FromResult(false);
            }

            _rows[holdId] = row with
            {
                State = "EXPIRED",
                ReleasedStreamId = releasedStreamId,
                ReleasedSequence = releasedSequence,
            };
            return Task.FromResult(true);
        }

        public Task<long> GetActiveHoldCentsAsync(string accountRef, CancellationToken ct = default) =>
            Task.FromResult(_rows.Values
                .Where(r => r.AccountRef == accountRef && r.State == "ACTIVE")
                .Sum(r => r.AmountCents));

        public Task<IReadOnlyList<AccountHoldRow>> GetActiveHoldsAsync(
            string accountRef, CancellationToken ct = default)
        {
            IReadOnlyList<AccountHoldRow> holds = _rows.Values
                .Where(r => r.AccountRef == accountRef && r.State == "ACTIVE")
                .OrderBy(r => r.HoldId, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(holds);
        }

        public Task<IReadOnlyList<AccountHoldRow>> GetActiveHoldsWithValueDateAtOrBeforeAsync(
            DateOnly valueDateHorizon, CancellationToken ct = default)
        {
            IReadOnlyList<AccountHoldRow> holds = _rows.Values
                .Where(r => r.State == "ACTIVE" && r.ValueDate <= valueDateHorizon)
                .OrderBy(r => r.AccountRef, StringComparer.Ordinal)
                .ThenBy(r => r.HoldId, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(holds);
        }

        public Task TruncateAsync(CancellationToken ct = default)
        {
            _rows.Clear();
            return Task.CompletedTask;
        }
    }
}
