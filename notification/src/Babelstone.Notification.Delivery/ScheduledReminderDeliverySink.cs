using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Babelstone.Notification.Delivery;

/// <summary>
/// The SCHEDULED leg's bridge (bd babelstone-60n8.4): the notification core's
/// <see cref="INotificationDeliverySink"/> implemented over the delivery outbox. In plain terms: when the
/// scheduler's pass decides a reminder is due (already stamped with its stable composite
/// <c>notification_id</c> and deduped), this sink turns it into the one <see cref="NotificationDueSignal"/>
/// shape the shared transport delivers — <c>trigger_kind=SCHEDULED</c>, no causing domain event (a date
/// arriving has none, ADR-PC-023), structural data only — and records the delivery obligation. From there
/// the drain worker owns signing, retry, backoff and dead-lettering; the scheduler never learns or cares
/// how delivery went (post-flag, ADR-PC-025 slot 5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent by construction.</b> The outbox enqueue is keyed on the composite
/// <c>notification_id</c> (ADR-PC-025 slot 4), so a re-forwarded reminder — a scheduler restart replaying
/// a pass over a fresh in-memory ledger, say — re-opens nothing. The redelivered webhook the consumer may
/// still see carries the SAME id, which is exactly the contract: dedupe is consumer-side.
/// </para>
/// <para>
/// <b>What the v1 signal does not carry.</b> <c>customer_ref</c> is <see langword="null"/>: the
/// ADR-PC-027 read surface the scheduler works from exposes no recipient reference yet, so the consumer
/// resolves the recipient from <c>instance_id</c> — a named residual of the read surface, not of this
/// transport. <c>template_pack_version</c> is the host's pinned pack version (the same one-pinned-pack v1
/// simplification the scheduler host already makes).
/// </para>
/// </remarks>
public sealed class ScheduledReminderDeliverySink(
    IDeliveryOutbox outbox,
    WebhookDeliveryOptions options,
    TimeProvider clock,
    ILogger<ScheduledReminderDeliverySink>? logger = null) : INotificationDeliverySink
{
    private readonly IDeliveryOutbox _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    private readonly WebhookDeliveryOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <inheritdoc />
    public async Task EnqueueAsync(IReadOnlyList<RaisedReminder> raised, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(raised);

        foreach (var reminder in raised)
        {
            var enqueued = await _outbox.EnqueueAsync(ToSignal(reminder), _clock.GetUtcNow(), ct);
            if (enqueued)
            {
                logger?.LogInformation(
                    "Scheduled notification {NotificationId} (template {TemplateRef}) enqueued for webhook "
                    + "delivery (ADR-IC-004 outbox; bd babelstone-60n8.4).",
                    reminder.NotificationId, reminder.TemplateRef);
            }
        }
    }

    private NotificationDueSignal ToSignal(RaisedReminder reminder)
    {
        // Structural interpolation values only (ADR-PC-025 Decision 1): the reminder's integer-cent
        // amounts rendered invariantly, plus the schedule occurrence the composite id is keyed on — no
        // PII, ever (the renderer resolves the subject at render time by reference).
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, cents) in reminder.Amounts)
        {
            data[key] = cents.ToString(CultureInfo.InvariantCulture);
        }

        data.TryAdd("occurrence_date", reminder.OccurrenceKey.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        return new NotificationDueSignal(
            NotificationId: reminder.NotificationId,
            InstanceId: reminder.InstanceId,
            CustomerRef: null, // no recipient reference on the v1 read surface — see class remarks
            TemplateRef: reminder.TemplateRef,
            TemplatePackVersion: _options.TemplatePackVersion,
            TriggerKind: NotificationTriggerKind.Scheduled,
            CausationId: null, // a date arriving has no causing domain event (ADR-PC-023)
            Data: data,
            DueAt: reminder.DueAt);
    }
}
