namespace Babelstone.Notification.Delivery;

/// <summary>
/// The EVENT_DRIVEN ingress seam (bd babelstone-60n8.7): where consumed <c>NotificationDue</c> signals
/// enter the shared delivery transport. In plain terms: the engine emits
/// <c>NotificationDue(trigger_kind=EVENT_DRIVEN)</c> onto the post-commit bus through its outbox relay
/// (ADR-PC-025 / ADR-IC-004); an implementation of this port consumes them and the delivery pass drains
/// them into the SAME outbox the SCHEDULED leg fills — one transport, parameterised by
/// <c>trigger_kind</c>, never two (the 60n8.7 sharing requirement).
/// </summary>
/// <remarks>
/// <para>
/// <b>At-least-once composes through.</b> The bus leg is itself at-least-once (per-instance order,
/// redelivery expected — ADR-PC-025 slot 3), so a poll may re-present a signal already seen; the outbox's
/// idempotent enqueue on the composite <c>notification_id</c> absorbs it (slot 4). An implementation
/// should therefore commit its consumer offset only AFTER its signals are enqueued — the ADR-IC-011 §P3
/// step-3 rule: no notification obligation lost between consume and record.
/// </para>
/// <para>
/// <b>Why a seam and not a broker consumer here.</b> The engine-side EVENT_DRIVEN <c>NotificationDue</c>
/// emission is a named 60n8.7 residual (60n8.3 shipped the SCHEDULED emission contract only), so there is
/// no live topic to consume yet. The transport is complete and proven behind this port — tests drive it
/// with an in-memory source — and the Redpanda/Avro consumer (the same Confluent stack the engine's inbox
/// consumer uses) slots in as the follow-up implementation once the producer exists, with zero change to
/// the outbox, signer, retry, or renderer.
/// </para>
/// </remarks>
public interface INotificationDueSource
{
    /// <summary>Fetch whatever consumed <c>NotificationDue</c> signals are waiting — a bounded,
    /// non-blocking batch (empty when nothing arrived). A thrown failure is INGRESS backpressure the pass
    /// logs and retries next tick; it never stalls the outbound retry drain.</summary>
    Task<IReadOnlyList<NotificationDueSignal>> PollAsync(CancellationToken ct = default);
}

/// <summary>The default no-op source a host runs with until the engine-side EVENT_DRIVEN emission and its
/// bus consumer land (see <see cref="INotificationDueSource"/> remarks) — the SCHEDULED leg alone drives
/// the transport. Registered with <c>TryAdd</c>, so composing a real source replaces it with no other
/// change.</summary>
public sealed class NullNotificationDueSource : INotificationDueSource
{
    /// <inheritdoc />
    public Task<IReadOnlyList<NotificationDueSignal>> PollAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<NotificationDueSignal>>([]);
}
