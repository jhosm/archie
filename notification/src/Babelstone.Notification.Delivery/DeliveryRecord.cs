namespace Babelstone.Notification.Delivery;

/// <summary>
/// Where one delivery obligation stands — the ADR-IC-011 §P3 delivery-record lifecycle. A record is
/// created <see cref="Pending"/>, stays <see cref="Pending"/> across transient-failure retries (with a
/// moving next-attempt time), and terminates exactly one of three ways.
/// </summary>
public enum DeliveryStatus
{
    /// <summary>Owed and not yet confirmed — due for (re)attempt once its next-attempt time arrives.</summary>
    Pending,

    /// <summary>The receiver confirmed with a 2xx (ADR-IC-011 §D4). Terminal. The record is retained so a
    /// redelivered upstream signal (the expected at-least-once case) re-enqueues nothing.</summary>
    Delivered,

    /// <summary>Retries exhausted (§D4 — <see cref="WebhookDeliveryOptions.MaxAttempts"/> transient
    /// failures). Terminal; an operator/consumer must intervene. The durable store
    /// (<see cref="PostgresDeliveryOutbox"/>) records the <c>NotificationDeliveryExhausted</c>
    /// backbone announcement in the same transaction as this flip (ADR-IC-011).</summary>
    DeadLettered,

    /// <summary>The receiver answered a non-429 4xx — the endpoint is misconfigured, retrying cannot fix
    /// it (§D4). Terminal immediately; human review required.</summary>
    Abandoned,
}

/// <summary>
/// One row of the per-service delivery outbox (ADR-IC-004 / ADR-IC-011 §P3): the signal owed, how many
/// attempts have failed, when the next attempt is due, and the terminal state if any. Immutable snapshot —
/// the store owns transitions. Deliberately carries the STRUCTURAL signal only: rendered content and
/// render-time-resolved PII never land here (they materialise per attempt and are discarded —
/// ADR-PC-025 §PII).
/// </summary>
/// <param name="NotificationId">The stable composite idempotency key (ADR-PC-025 slot 4) — the outbox's
/// enqueue-dedupe key and the consumer's dedupe anchor.</param>
/// <param name="Signal">The structural notification signal to deliver.</param>
/// <param name="Status">Where the obligation stands.</param>
/// <param name="Attempts">Delivery attempts made so far (0 before the first).</param>
/// <param name="EnqueuedAt">When the obligation was recorded — the envelope's <c>occurred_at</c>.</param>
/// <param name="NextAttemptAt">When the next attempt is due (meaningful while <see cref="DeliveryStatus.Pending"/>).</param>
/// <param name="LastError">The most recent failure detail, for diagnostics; never carries payload or PII.</param>
public sealed record DeliveryRecord(
    Guid NotificationId,
    NotificationDueSignal Signal,
    DeliveryStatus Status,
    int Attempts,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset NextAttemptAt,
    string? LastError);
