using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelstone.Notification.Delivery.Tests;

/// <summary>
/// The §D4 exhaustion relay's drain semantics (bd babelstone-60n8.10; ADR-IC-011 §P3 step 7 /
/// ADR-IC-004), over fakes at the outbox and publisher seams: publish-then-flip ordering, batch
/// bounding, and the backpressure posture — a publish failure aborts the pass and flips NOTHING behind
/// it, so an unreachable broker leaves every unannounced row PENDING for the next tick (never lost,
/// never FAILED).
/// </summary>
public sealed class ExhaustedEventRelayPassTests
{
    private static readonly DateOnly AsOf = new(2026, 7, 3);

    private static ExhaustedDelivery Exhausted() => new(
        NotificationId: Guid.NewGuid(),
        EventId: Guid.NewGuid(),
        InstanceId: Guid.NewGuid(),
        CustomerRef: null,
        TemplateRef: "pt.test.notice",
        TemplatePackVersion: "pt.2026.1",
        TriggerKind: NotificationTriggerKind.Scheduled,
        Attempts: 10,
        LastError: "receiver answered 503",
        ExhaustedAt: DateTimeOffset.UtcNow);

    private static ExhaustedEventRelayPass Pass(FakeExhaustedOutbox outbox, FakePublisher publisher, int batch = 50) =>
        new(
            outbox,
            publisher,
            new WebhookDeliveryOptions
            {
                EndpointUrl = DeliveryTestSupport.Endpoint,
                Secret = DeliveryTestSupport.Secret,
                TemplatePackVersion = "pt.2026.1",
                ClaimBatchSize = batch,
            },
            NullLogger<ExhaustedEventRelayPass>.Instance);

    [Fact]
    public async Task Publishes_every_pending_row_and_flips_each_after_its_ack()
    {
        var journal = new List<string>();
        var first = Exhausted();
        var second = Exhausted();
        var outbox = new FakeExhaustedOutbox(journal, first, second);
        var publisher = new FakePublisher(journal);

        await Pass(outbox, publisher).RunOnceAsync(AsOf);

        Assert.Equal(
            [first.NotificationId, second.NotificationId],
            publisher.Published.Select(e => e.NotificationId));
        // Publish-then-flip, interleaved per row: each flip happens only after ITS broker ack — the
        // flip never precedes the publish (the ADR-IC-004 no-loss ordering).
        Assert.Equal(
            [$"publish {first.NotificationId}", $"mark {first.NotificationId}",
             $"publish {second.NotificationId}", $"mark {second.NotificationId}"],
            journal);
    }

    [Fact]
    public async Task A_publish_failure_aborts_the_pass_and_flips_nothing_behind_it()
    {
        var journal = new List<string>();
        var first = Exhausted();
        var second = Exhausted();
        var third = Exhausted();
        var outbox = new FakeExhaustedOutbox(journal, first, second, third);
        var publisher = new FakePublisher(journal) { FailOn = second.NotificationId };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Pass(outbox, publisher).RunOnceAsync(AsOf));

        // The first row completed (published + flipped); the failed one and everything behind it stay
        // PENDING — backpressure, never a half-recorded announcement (ADR-IC-004).
        Assert.Equal([first.NotificationId], outbox.Marked);
        Assert.Equal([first.NotificationId], publisher.Published.Select(e => e.NotificationId));
    }

    [Fact]
    public async Task The_claim_is_bounded_by_the_batch_size()
    {
        var journal = new List<string>();
        var outbox = new FakeExhaustedOutbox(journal, Exhausted(), Exhausted(), Exhausted());
        var publisher = new FakePublisher(journal);

        await Pass(outbox, publisher, batch: 2).RunOnceAsync(AsOf);

        Assert.Equal(2, publisher.Published.Count);
    }

    private sealed class FakeExhaustedOutbox(List<string> journal, params ExhaustedDelivery[] pending)
        : IExhaustedDeliveryOutbox
    {
        private readonly List<ExhaustedDelivery> _pending = [.. pending];

        public List<Guid> Marked { get; } = [];

        public Task<IReadOnlyList<ExhaustedDelivery>> ClaimPendingAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExhaustedDelivery>>(
                [.. _pending.Where(e => !Marked.Contains(e.NotificationId)).Take(limit)]);

        public Task MarkPublishedAsync(Guid notificationId, CancellationToken ct = default)
        {
            Marked.Add(notificationId);
            journal.Add($"mark {notificationId}");
            return Task.CompletedTask;
        }
    }

    private sealed class FakePublisher(List<string> journal) : IExhaustedEventPublisher
    {
        public List<ExhaustedDelivery> Published { get; } = [];
        public Guid? FailOn { get; init; }

        public Task PublishAsync(ExhaustedDelivery exhausted, CancellationToken ct = default)
        {
            if (exhausted.NotificationId == FailOn)
            {
                throw new InvalidOperationException("broker unreachable (test)");
            }

            Published.Add(exhausted);
            journal.Add($"publish {exhausted.NotificationId}");
            return Task.CompletedTask;
        }
    }
}
