namespace Babelstone.Cadence;

/// <summary>
/// The in-memory <see cref="IDedupeLedger"/> a v1 cadence consumer uses to prove the idempotency invariant
/// (ADR-PC-036 §Decision 2 + ADR-IC-019 / ADR-PC-025 slot 4). Thread-safe (a single <see cref="HashSet{T}"/>
/// behind a lock — a worker runs one pass at a time, but reserving is cheap and the lock keeps a future
/// concurrent pass honest). A durable, crash-surviving ledger is a later concern; within one process lifetime
/// this gives the "re-runs don't re-act" guarantee. Left UNSEALED so a domain consumer can subclass it for a
/// named ledger interface (e.g. the notification estate's <c>InMemoryNotificationDedupeLedger</c>) without
/// reimplementing the reservation logic.
/// </summary>
public class InMemoryDedupeLedger : IDedupeLedger
{
    private readonly HashSet<Guid> _seen = [];
    private readonly Lock _gate = new();

    public Task<bool> TryReserveAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_seen.Add(id));
        }
    }
}
