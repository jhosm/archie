using Babelstone.Notification.Delivery;
using Xunit;
using static Babelstone.Notification.Delivery.Tests.DeliveryTestSupport;

namespace Babelstone.Notification.Delivery.Tests;

/// <summary>
/// The SCHEDULED-leg bridge (bd babelstone-60n8.4): a scheduler-raised reminder maps into the one shared
/// <c>NotificationDueSignal</c> shape — <c>trigger_kind=SCHEDULED</c>, no causation (ADR-PC-023), the SAME
/// composite <c>notification_id</c> the scheduler stamped (ADR-PC-025 slot 4), structural data only — and a
/// re-forwarded reminder re-opens nothing (the idempotent enqueue).
/// </summary>
public sealed class ScheduledReminderDeliverySinkTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Maps_a_raised_reminder_into_the_scheduled_signal_shape()
    {
        var outbox = new InMemoryDeliveryOutbox();
        var sink = new ScheduledReminderDeliverySink(outbox, Options(), new MutableClock(T0));
        var reminder = Reminder();

        await sink.EnqueueAsync([reminder]);

        var record = await outbox.GetAsync(reminder.NotificationId);
        Assert.NotNull(record);
        var signal = record.Signal;
        Assert.Equal(reminder.NotificationId, signal.NotificationId); // the scheduler's composite id IS the key
        Assert.Equal(reminder.InstanceId, signal.InstanceId);
        Assert.Equal(NotificationTriggerKind.Scheduled, signal.TriggerKind);
        Assert.Null(signal.CausationId); // a date arriving has no causing domain event (ADR-PC-023)
        Assert.Null(signal.CustomerRef); // no recipient reference on the v1 read surface
        Assert.Equal("pt.test.notice", signal.TemplateRef);
        Assert.Equal("pt.2026.1", signal.TemplatePackVersion);
        Assert.Equal(reminder.DueAt, signal.DueAt);
        Assert.Equal("1012345", signal.Data["total_payout_cents"]); // integer cents, invariant string
        Assert.Equal("2026-07-16", signal.Data["occurrence_date"]);
    }

    [Fact]
    public async Task Reforwarding_the_same_reminder_is_the_idempotent_no_op()
    {
        var outbox = new InMemoryDeliveryOutbox();
        var clock = new MutableClock(T0);
        var sink = new ScheduledReminderDeliverySink(outbox, Options(), clock);
        var reminder = Reminder();

        await sink.EnqueueAsync([reminder]);
        await outbox.MarkDeliveredAsync(reminder.NotificationId, attempts: 1);
        clock.Advance(TimeSpan.FromHours(1));
        await sink.EnqueueAsync([reminder]); // a scheduler restart replaying its pass

        var record = (await outbox.GetAsync(reminder.NotificationId))!;
        Assert.Equal(DeliveryStatus.Delivered, record.Status); // not re-opened — the consumer is not re-notified
        Assert.Equal(T0, record.EnqueuedAt);
    }

    private static RaisedReminder Reminder() => new(
        NotificationId: Guid.NewGuid(),
        InstanceId: Guid.NewGuid(),
        TemplateRef: "pt.test.notice",
        OccurrenceKey: new DateOnly(2026, 7, 16),
        DueAt: new DateOnly(2026, 7, 2),
        Amounts: new Dictionary<string, long> { ["total_payout_cents"] = 1012345 });
}
