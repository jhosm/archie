namespace Babelstone.Notification;

/// <summary>
/// The core's OUTBOUND port: where a pass's newly-raised reminders go to be DELIVERED (ADR-PC-025 slot 3 —
/// at-least-once via a per-service outbox, ADR-IC-004). In plain terms: the schedule pass decides a reminder
/// is due and stamps its stable composite <c>notification_id</c>; this sink is the hand-off to the delivery
/// half of the estate (the ADR-IC-011 HMAC-signed webhook transport, bd babelstone-60n8.4), which owns
/// signing, retry, backoff and dead-lettering. The core stays family-agnostic and delivery-agnostic: it knows
/// only that raised reminders are handed onward, never how they travel.
/// </summary>
/// <remarks>
/// <para>
/// <b>Optional by construction.</b> The sink is an optional dependency of
/// <see cref="NotificationSchedulePass"/>: a host that composes no delivery half (the pre-60n8.4 scheduler
/// shape) runs unchanged, raising reminders that go nowhere — exactly the prior behaviour. Registering an
/// implementation (the delivery estate's composition extension does) activates the hand-off with zero core
/// diff elsewhere.
/// </para>
/// <para>
/// <b>Idempotency contract.</b> An implementation MUST be idempotent on
/// <see cref="RaisedReminder.NotificationId"/> — re-forwarding an already-enqueued reminder is the expected
/// at-least-once case, never a second delivery obligation (ADR-PC-025 slot 4). It must also treat enqueue as
/// a local, non-throwing append for transient conditions: the pass reserves the composite id in the dedupe
/// ledger BEFORE forwarding, so a thrown enqueue after reservation would strand the reminder (raised but
/// never delivered). The v1 in-memory outbox satisfies this trivially; a durable outbox must co-locate the
/// ledger reservation and the outbox append in one transaction — a named residual of the durable-store
/// follow-up, not of this port.
/// </para>
/// </remarks>
public interface INotificationDeliverySink
{
    /// <summary>Hand the reminders a pass newly raised (already id-stamped and deduped) to the delivery
    /// half. Idempotent on <see cref="RaisedReminder.NotificationId"/>.</summary>
    /// <param name="raised">The reminders the pass admitted past the dedupe ledger this tick.</param>
    /// <param name="ct">Cancellation propagated from the worker loop.</param>
    Task EnqueueAsync(IReadOnlyList<RaisedReminder> raised, CancellationToken ct = default);
}
