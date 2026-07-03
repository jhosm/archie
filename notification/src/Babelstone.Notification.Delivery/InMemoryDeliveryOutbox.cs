namespace Babelstone.Notification.Delivery;

/// <summary>
/// The v1 in-memory <see cref="IDeliveryOutbox"/> — the same in-process posture as the scheduler's
/// <c>InMemoryDedupeLedger</c>: it proves the at-least-once + idempotent-enqueue invariants within one
/// process lifetime, and a durable, crash-surviving store (the ADR-IC-011 §P3 PostgreSQL delivery table)
/// slots in behind the port as the named follow-up. Thread-safe (one dictionary behind a lock — a single
/// drain worker runs at a time, but enqueue arrives from the scheduler pass and the bus drain).
/// </summary>
public sealed class InMemoryDeliveryOutbox : IDeliveryOutbox
{
    private readonly Dictionary<Guid, DeliveryRecord> _records = [];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public Task<bool> EnqueueAsync(NotificationDueSignal signal, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        lock (_gate)
        {
            if (_records.ContainsKey(signal.NotificationId))
            {
                // The idempotent re-present (ADR-PC-025 slot 4): an at-least-once upstream re-offered a
                // signal this outbox already owns — pending, delivered, or dead-lettered. Absorb it.
                return Task.FromResult(false);
            }

            _records[signal.NotificationId] = new DeliveryRecord(
                signal.NotificationId,
                signal,
                DeliveryStatus.Pending,
                Attempts: 0,
                EnqueuedAt: now,
                NextAttemptAt: now, // due immediately — the first attempt needs no backoff
                LastError: null);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeliveryRecord>> ClaimDueAsync(
        DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        lock (_gate)
        {
            IReadOnlyList<DeliveryRecord> due =
            [
                .. _records.Values
                    .Where(r => r.Status == DeliveryStatus.Pending && r.NextAttemptAt <= now)
                    .OrderBy(r => r.NextAttemptAt)
                    .ThenBy(r => r.NotificationId) // stable tiebreak so a pass is deterministic
                    .Take(limit),
            ];
            return Task.FromResult(due);
        }
    }

    /// <inheritdoc />
    public Task MarkDeliveredAsync(Guid notificationId, int attempts, CancellationToken ct = default) =>
        Transition(notificationId, r => r with { Status = DeliveryStatus.Delivered, Attempts = attempts, LastError = null });

    /// <inheritdoc />
    public Task MarkAttemptFailedAsync(
        Guid notificationId, int attempts, DateTimeOffset nextAttemptAt, string? reason, CancellationToken ct = default) =>
        Transition(notificationId, r => r with { Attempts = attempts, NextAttemptAt = nextAttemptAt, LastError = reason });

    /// <inheritdoc />
    public Task MarkDeadLetteredAsync(Guid notificationId, int attempts, string? reason, CancellationToken ct = default) =>
        Transition(notificationId, r => r with { Status = DeliveryStatus.DeadLettered, Attempts = attempts, LastError = reason });

    /// <inheritdoc />
    public Task MarkAbandonedAsync(Guid notificationId, int attempts, string? reason, CancellationToken ct = default) =>
        Transition(notificationId, r => r with { Status = DeliveryStatus.Abandoned, Attempts = attempts, LastError = reason });

    /// <inheritdoc />
    public Task<DeliveryRecord?> GetAsync(Guid notificationId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_records.TryGetValue(notificationId, out var record) ? record : null);
        }
    }

    private Task Transition(Guid notificationId, Func<DeliveryRecord, DeliveryRecord> apply)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(notificationId, out var record))
            {
                // Fail loud at the seam: a mark for an unknown id is a wiring bug (the pass only marks
                // records it just claimed), not a runtime condition to swallow.
                throw new InvalidOperationException(
                    $"Delivery outbox holds no record for notification '{notificationId}'.");
            }

            _records[notificationId] = apply(record);
            return Task.CompletedTask;
        }
    }
}
