namespace Babelstone.Notification.Delivery;

/// <summary>
/// One §D4 exhaustion fact awaiting (or having completed) its backbone announcement — the CLR face of a
/// <c>notification_delivery_exhausted</c> outbox row (ADR-IC-011 §P3 step 7; bd babelstone-60n8.10). The
/// row is written by <see cref="PostgresDeliveryOutbox.MarkDeadLetteredAsync"/> in the SAME transaction
/// as the DEAD_LETTERED flip (ADR-IC-004: a crash between "give up" and "announce it" never loses the
/// announcement); the backbone relay drains it as the governed
/// <c>operations.NotificationDeliveryExhausted</c> event.
/// </summary>
/// <param name="NotificationId">The exhausted delivery's stable composite idempotency key (ADR-PC-025
/// slot 4) — one exhausted event exists per notification_id, ever; the consumer's dedupe anchor.</param>
/// <param name="EventId">The outbox row's own event id (DB-generated, stable across relay retries) —
/// the CloudEvents <c>ce_id</c> on the published record.</param>
/// <param name="InstanceId">The instance the undelivered notification is about (the Kafka key —
/// partition_key = instance_id).</param>
/// <param name="CustomerRef">The opaque recipient REFERENCE the signal carried, when it carried one.
/// Never the name/NIF/contact itself (ADR-PC-004 §P2).</param>
/// <param name="TemplateRef">The pack-namespaced template the notification would have rendered.</param>
/// <param name="TemplatePackVersion">The pack version pinned on the signal.</param>
/// <param name="TriggerKind">EVENT_DRIVEN | SCHEDULED | PRE_CONTRACTUAL (ADR-PC-025 §6).</param>
/// <param name="Attempts">Delivery attempts made before dead-lettering (the §D4 exhaustion count).</param>
/// <param name="LastError">The final attempt's transport diagnostic — status text only, never payload
/// or PII.</param>
/// <param name="ExhaustedAt">When the store recorded the DEAD_LETTERED flip (DB clock).</param>
public sealed record ExhaustedDelivery(
    Guid NotificationId,
    Guid EventId,
    Guid InstanceId,
    Guid? CustomerRef,
    string TemplateRef,
    string TemplatePackVersion,
    NotificationTriggerKind TriggerKind,
    int Attempts,
    string? LastError,
    DateTimeOffset ExhaustedAt);

/// <summary>
/// The relay's read side of the §D4 exhaustion outbox (ADR-IC-011 §P3 step 7 / ADR-IC-004): claim the
/// PENDING announcements, publish each to the backbone, flip it PUBLISHED. A produce failure leaves the
/// row PENDING for the next pass (backpressure, never FAILED — the same posture as the engine relay).
/// Implemented by <see cref="PostgresDeliveryOutbox"/> alongside <see cref="IDeliveryOutbox"/>: the two
/// faces share one store because the exhausted row is BORN in the delivery store's own dead-letter
/// transaction.
/// </summary>
public interface IExhaustedDeliveryOutbox
{
    /// <summary>The PENDING exhausted rows in exhaustion order, at most <paramref name="limit"/> — one
    /// relay pass's worth. No lease is taken: the estate runs one relay (the same single-drainer stance
    /// as the webhook drain and the engine's outbox relay).</summary>
    Task<IReadOnlyList<ExhaustedDelivery>> ClaimPendingAsync(int limit, CancellationToken ct = default);

    /// <summary>The backbone acked the publish — flip the row PUBLISHED (DB-clock
    /// <c>published_at</c>). Fail-loud on an unknown id: the relay only marks rows it just
    /// claimed.</summary>
    Task MarkPublishedAsync(Guid notificationId, CancellationToken ct = default);
}
