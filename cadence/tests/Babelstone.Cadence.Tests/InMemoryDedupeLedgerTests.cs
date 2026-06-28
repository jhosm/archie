using Babelstone.Cadence;
using Xunit;

namespace Babelstone.Cadence.Tests;

/// <summary>
/// Tests for <see cref="InMemoryDedupeLedger"/> — the generic in-memory <see cref="IDedupeLedger"/>
/// (ADR-PC-036 §Decision 2 + ADR-IC-019 / ADR-PC-025 slot 4). The first reservation of an id admits the work;
/// a second reservation of the same id is the idempotent replay and is refused; distinct ids are independent.
/// </summary>
public sealed class InMemoryDedupeLedgerTests
{
    [Fact]
    public async Task First_reservation_admits_and_the_repeat_is_refused()
    {
        IDedupeLedger ledger = new InMemoryDedupeLedger();
        var id = Guid.NewGuid();

        Assert.True(await ledger.TryReserveAsync(id));
        Assert.False(await ledger.TryReserveAsync(id));
        Assert.False(await ledger.TryReserveAsync(id));
    }

    [Fact]
    public async Task Distinct_ids_are_reserved_independently()
    {
        IDedupeLedger ledger = new InMemoryDedupeLedger();

        Assert.True(await ledger.TryReserveAsync(Guid.NewGuid()));
        Assert.True(await ledger.TryReserveAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Concurrent_reservations_of_the_same_id_admit_exactly_once()
    {
        IDedupeLedger ledger = new InMemoryDedupeLedger();
        var id = Guid.NewGuid();

        // The ledger reserves under a lock, so even a racing pair admits the work exactly once — the invariant
        // a future concurrent pass relies on.
        var results = await Task.WhenAll(
            Enumerable.Range(0, 64).Select(_ => Task.Run(() => ledger.TryReserveAsync(id))));

        Assert.Equal(1, results.Count(reserved => reserved));
    }
}
