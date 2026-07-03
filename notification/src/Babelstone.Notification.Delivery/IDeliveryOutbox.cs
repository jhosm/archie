namespace Babelstone.Notification.Delivery;

/// <summary>
/// The per-service delivery OUTBOX (ADR-IC-004; ADR-IC-011 §P3) the webhook drain works off. In plain
/// terms: "consume/raise a notification obligation" and "deliver it over HTTP" must not be one atomic
/// action — the obligation is recorded here first, and a delivery worker drains the records with retries,
/// so a crash between the two never loses a notification and a flaky receiver never blocks the producer.
/// At-least-once falls out: a record stays claimable until a 2xx confirms it.
/// </summary>
/// <remarks>
/// <b>Idempotent enqueue on the composite <c>notification_id</c> (ADR-PC-025 slot 4).</b> Both legs feed
/// this store — the scheduler's SCHEDULED sink and the EVENT_DRIVEN bus drain — and both upstreams are
/// themselves at-least-once, so a re-presented signal is the EXPECTED case: enqueue admits a given
/// <c>notification_id</c> exactly once, ever (terminal records are retained so a late redelivery of an
/// already-delivered signal re-opens nothing). The v1 store is in-memory (the same posture as the
/// scheduler's dedupe ledger); a durable, crash-surviving store is the named follow-up and slots in
/// behind this port — the interface is the contract, the storage is replaceable (ADR-IC-004).
/// </remarks>
public interface IDeliveryOutbox
{
    /// <summary>Record a delivery obligation. Returns <see langword="true"/> if this
    /// <c>notification_id</c> is new (a record was created, due immediately), <see langword="false"/> on
    /// the idempotent re-present (an existing record — pending or terminal — absorbs it).</summary>
    /// <param name="signal">The structural signal owed to the consumer.</param>
    /// <param name="now">The enqueue instant (the caller owns the clock — ADR-PC-023 §6 posture).</param>
    /// <param name="ct">Cancellation.</param>
    Task<bool> EnqueueAsync(NotificationDueSignal signal, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>The pending records whose next-attempt time has arrived, ordered soonest-due first, at
    /// most <paramref name="limit"/> — one drain pass's worth of work. No lease is taken: the estate runs
    /// one drain worker (the same single-drainer stance as the engine's outbox relay).</summary>
    Task<IReadOnlyList<DeliveryRecord>> ClaimDueAsync(DateTimeOffset now, int limit, CancellationToken ct = default);

    /// <summary>The receiver confirmed (2xx) — terminal <see cref="DeliveryStatus.Delivered"/>.</summary>
    Task MarkDeliveredAsync(Guid notificationId, int attempts, CancellationToken ct = default);

    /// <summary>A transient failure (5xx / timeout / 429) — still <see cref="DeliveryStatus.Pending"/>,
    /// next due at <paramref name="nextAttemptAt"/> (the §D4 backoff the pass computed).</summary>
    Task MarkAttemptFailedAsync(
        Guid notificationId, int attempts, DateTimeOffset nextAttemptAt, string? reason, CancellationToken ct = default);

    /// <summary>Retries exhausted (§D4) — terminal <see cref="DeliveryStatus.DeadLettered"/>.</summary>
    Task MarkDeadLetteredAsync(Guid notificationId, int attempts, string? reason, CancellationToken ct = default);

    /// <summary>Permanent receiver rejection (non-429 4xx, §D4) — terminal
    /// <see cref="DeliveryStatus.Abandoned"/>; retrying cannot fix a misconfigured endpoint.</summary>
    Task MarkAbandonedAsync(Guid notificationId, int attempts, string? reason, CancellationToken ct = default);

    /// <summary>The current record for <paramref name="notificationId"/>, or <see langword="null"/> —
    /// diagnostics and tests; never on the delivery hot path.</summary>
    Task<DeliveryRecord?> GetAsync(Guid notificationId, CancellationToken ct = default);
}
