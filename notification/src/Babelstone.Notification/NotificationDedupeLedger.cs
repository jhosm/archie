namespace Babelstone.Notification;

/// <summary>
/// The "already raised this one" memory the core dedupes against (ADR-PC-025 slot 4) — family-agnostic. A
/// composite <c>notification_id</c> is reserved once; a second attempt to reserve the same id is the
/// idempotent replay the contract mandates and returns <see langword="false"/>. Abstracted so the emission
/// child (bd babelstone-60n8.3) can back it with a durable store while v1 proves the invariant against the
/// in-memory default.
/// </summary>
public interface INotificationDedupeLedger
{
    /// <summary>Reserve <paramref name="notificationId"/> if it is new to this ledger. Returns
    /// <see langword="true"/> the FIRST time an id is seen (the decision is new and should be raised),
    /// and <see langword="false"/> on every subsequent attempt (the idempotent replay — already raised).</summary>
    Task<bool> TryReserveAsync(Guid notificationId, CancellationToken ct = default);
}

/// <summary>
/// The in-memory <see cref="INotificationDedupeLedger"/> v1 uses to prove the slot-4 idempotency
/// invariant. Thread-safe (a single <see cref="HashSet{T}"/> behind a lock — the worker runs one pass
/// at a time, but reserving is cheap and the lock keeps a future concurrent pass honest). A durable,
/// crash-surviving ledger is the emission child's concern (bd babelstone-60n8.3); within one process
/// lifetime this gives the "re-runs don't re-notify" guarantee the acceptance criteria require.
/// </summary>
public sealed class InMemoryNotificationDedupeLedger : INotificationDedupeLedger
{
    private readonly HashSet<Guid> _seen = [];
    private readonly Lock _gate = new();

    public Task<bool> TryReserveAsync(Guid notificationId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_seen.Add(notificationId));
        }
    }
}
