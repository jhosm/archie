using Babelstone.Notification.Delivery;
using Xunit;

namespace Babelstone.Notification.Delivery.Tests;

/// <summary>
/// The ADR-IC-004 outbox invariants at the store seam: enqueue is idempotent on the composite
/// <c>notification_id</c> (ADR-PC-025 slot 4 — an at-least-once upstream re-presenting a signal re-opens
/// nothing, even after delivery), and a record is claimable exactly while it is pending AND due.
/// </summary>
public sealed class InMemoryDeliveryOutboxTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Enqueue_is_idempotent_on_notification_id()
    {
        var outbox = new InMemoryDeliveryOutbox();
        var signal = Signal();

        Assert.True(await outbox.EnqueueAsync(signal, T0));
        Assert.False(await outbox.EnqueueAsync(signal, T0.AddMinutes(1)));

        var due = await outbox.ClaimDueAsync(T0.AddMinutes(2), limit: 10);
        Assert.Single(due);
        Assert.Equal(0, due[0].Attempts);
        Assert.Equal(T0, due[0].EnqueuedAt); // the re-present did not reset the record
    }

    [Fact]
    public async Task A_delivered_record_absorbs_a_late_redelivery_and_is_never_claimable_again()
    {
        var outbox = new InMemoryDeliveryOutbox();
        var signal = Signal();
        await outbox.EnqueueAsync(signal, T0);
        await outbox.MarkDeliveredAsync(signal.NotificationId, attempts: 1);

        Assert.False(await outbox.EnqueueAsync(signal, T0.AddHours(1)));
        Assert.Empty(await outbox.ClaimDueAsync(T0.AddHours(2), limit: 10));
        Assert.Equal(DeliveryStatus.Delivered, (await outbox.GetAsync(signal.NotificationId))!.Status);
    }

    [Fact]
    public async Task Claim_honours_the_next_attempt_time_and_the_limit()
    {
        var outbox = new InMemoryDeliveryOutbox();
        var early = Signal();
        var late = Signal();
        await outbox.EnqueueAsync(early, T0);
        await outbox.EnqueueAsync(late, T0);
        await outbox.MarkAttemptFailedAsync(late.NotificationId, attempts: 1, nextAttemptAt: T0.AddMinutes(30), reason: "503");

        // Before the retry time only the immediately-due record is claimable.
        var due = await outbox.ClaimDueAsync(T0.AddMinutes(1), limit: 10);
        Assert.Single(due);
        Assert.Equal(early.NotificationId, due[0].NotificationId);

        // At the retry time both are due, soonest-due first; the limit bounds the batch.
        var all = await outbox.ClaimDueAsync(T0.AddMinutes(31), limit: 10);
        Assert.Equal(2, all.Count);
        Assert.Equal(early.NotificationId, all[0].NotificationId);
        Assert.Single(await outbox.ClaimDueAsync(T0.AddMinutes(31), limit: 1));
    }

    [Fact]
    public async Task Terminal_records_are_not_claimable()
    {
        var outbox = new InMemoryDeliveryOutbox();
        var abandoned = Signal();
        var deadLettered = Signal();
        await outbox.EnqueueAsync(abandoned, T0);
        await outbox.EnqueueAsync(deadLettered, T0);
        await outbox.MarkAbandonedAsync(abandoned.NotificationId, attempts: 1, reason: "404");
        await outbox.MarkDeadLetteredAsync(deadLettered.NotificationId, attempts: 10, reason: "503");

        Assert.Empty(await outbox.ClaimDueAsync(T0.AddDays(1), limit: 10));
        Assert.Equal(DeliveryStatus.Abandoned, (await outbox.GetAsync(abandoned.NotificationId))!.Status);
        Assert.Equal(DeliveryStatus.DeadLettered, (await outbox.GetAsync(deadLettered.NotificationId))!.Status);
    }

    [Fact]
    public async Task Marking_an_unknown_record_fails_loud()
    {
        var outbox = new InMemoryDeliveryOutbox();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => outbox.MarkDeliveredAsync(Guid.NewGuid(), attempts: 1));
    }

    private static NotificationDueSignal Signal() => new(
        NotificationId: Guid.NewGuid(),
        InstanceId: Guid.NewGuid(),
        CustomerRef: null,
        TemplateRef: "pt.test.notice",
        TemplatePackVersion: "pt.2026.1",
        TriggerKind: NotificationTriggerKind.Scheduled,
        CausationId: null,
        Data: new Dictionary<string, string> { ["total_payout_cents"] = "1012345" },
        DueAt: new DateOnly(2026, 7, 2));
}
