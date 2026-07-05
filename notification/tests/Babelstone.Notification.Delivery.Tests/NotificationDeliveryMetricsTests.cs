using System.Diagnostics.Metrics;
using System.Net;
using Babelstone.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Babelstone.Notification.Delivery.Tests;

/// <summary>
/// The delivery estate's metric emission (ADR-IC-007), observed off the shared Babelstone meter
/// through a <see cref="MeterListener"/> — the same listener pattern the lifecycle driver's metric
/// tests use. Driven through the REAL passes over the existing fakes (not by calling the record
/// helpers directly), so what is asserted is the wiring: one <c>notification_deliveries_total</c>
/// increment per classified attempt with the right <c>babelstone.delivery_outcome</c> tag, and one
/// <c>notification_delivery_exhausted_published_total</c> increment per broker-acked relay publish.
/// Docker-free; the DB-backed pending-lag gauge is asserted in the Postgres Integration suite.
/// Assertions are presence-based (Contains / at-least-once): the instruments are process-global and
/// other test classes drive the same passes in parallel, so exact counts are not observable here.
/// </summary>
public sealed class NotificationDeliveryMetricsTests
{
    private static readonly DateOnly AsOf = new(2026, 7, 3);

    [Fact]
    public async Task Each_classified_attempt_increments_the_outcome_counter_with_its_tag()
    {
        var outcomes = new List<string>();
        using var listener = CounterListener(
            BabelstoneAttributes.NotificationDeliveriesMetric,
            BabelstoneAttributes.NotificationDeliveryOutcomeTag,
            outcomes);

        // The MeterListener callback appends to `outcomes` on whatever thread emits the
        // measurement, holding lock(outcomes). The instrument is process-global and other test
        // classes drive the same passes in parallel, so every read/reset of the list here must
        // take that same lock — otherwise a concurrent Add races Clear()/Contains() and throws
        // "collection was modified". These helpers keep the test body inside that lock.
        void ResetOutcomes()
        {
            lock (outcomes)
            {
                outcomes.Clear();
            }
        }

        void AssertObserved(string outcome)
        {
            lock (outcomes)
            {
                Assert.Contains(outcome, outcomes);
            }
        }

        var clock = new DeliveryTestSupport.MutableClock(new DateTimeOffset(2026, 7, 3, 8, 0, 0, TimeSpan.Zero));
        var outbox = new InMemoryDeliveryOutbox();

        // delivered: 200 on the first attempt.
        await outbox.EnqueueAsync(DeliveryTestSupport.Signal(), clock.Now);
        var okHandler = new DeliveryTestSupport.FakeHandler(_ =>
            DeliveryTestSupport.FakeHandler.Status(HttpStatusCode.OK));
        await DeliveryTestSupport.Pass(outbox, okHandler, clock).RunOnceAsync(AsOf);
        AssertObserved(NotificationDeliveryMetrics.OutcomeDelivered);

        // transient_retry: a 503 marks the attempt failed and reschedules.
        ResetOutcomes();
        await outbox.EnqueueAsync(DeliveryTestSupport.Signal(), clock.Now);
        var busyHandler = new DeliveryTestSupport.FakeHandler(_ =>
            DeliveryTestSupport.FakeHandler.Status(HttpStatusCode.ServiceUnavailable));
        await DeliveryTestSupport.Pass(outbox, busyHandler, clock).RunOnceAsync(AsOf);
        AssertObserved(NotificationDeliveryMetrics.OutcomeTransientRetry);

        // abandoned: a non-429 4xx is terminal immediately.
        ResetOutcomes();
        await outbox.EnqueueAsync(DeliveryTestSupport.Signal(), clock.Now);
        var goneHandler = new DeliveryTestSupport.FakeHandler(_ =>
            DeliveryTestSupport.FakeHandler.Status(HttpStatusCode.NotFound));
        await DeliveryTestSupport.Pass(outbox, goneHandler, clock).RunOnceAsync(AsOf);
        AssertObserved(NotificationDeliveryMetrics.OutcomeAbandoned);

        // dead_lettered: exhaust MaxAttempts=1 in one pass.
        ResetOutcomes();
        await outbox.EnqueueAsync(DeliveryTestSupport.Signal(), clock.Now);
        var deadHandler = new DeliveryTestSupport.FakeHandler(_ =>
            DeliveryTestSupport.FakeHandler.Status(HttpStatusCode.ServiceUnavailable));
        await DeliveryTestSupport.Pass(outbox, deadHandler, clock, DeliveryTestSupport.Options(maxAttempts: 1))
            .RunOnceAsync(AsOf);
        AssertObserved(NotificationDeliveryMetrics.OutcomeDeadLettered);
    }

    [Fact]
    public async Task Each_acked_relay_publish_increments_the_published_counter()
    {
        var counted = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BabelstoneTelemetry.MeterName
                && instrument.Name == BabelstoneAttributes.NotificationExhaustedPublishedMetric)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref counted, value));
        listener.Start();

        var outbox = new SinglePendingOutbox();
        var pass = new ExhaustedEventRelayPass(
            outbox,
            new AckAllPublisher(),
            DeliveryTestSupport.Options(),
            NullLogger<ExhaustedEventRelayPass>.Instance);

        await pass.RunOnceAsync(AsOf);

        Assert.True(Interlocked.Read(ref counted) >= 1, "the relay publish must tick the counter");
        Assert.True(outbox.Published, "the counter must tick only alongside the PUBLISHED flip");
    }

    private static MeterListener CounterListener(string instrumentName, string tagKey, List<string> tagValues)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BabelstoneTelemetry.MeterName && instrument.Name == instrumentName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == tagKey && tag.Value is string value)
                {
                    lock (tagValues)
                    {
                        tagValues.Add(value);
                    }
                }
            }
        });
        listener.Start();
        return listener;
    }

    private sealed class SinglePendingOutbox : IExhaustedDeliveryOutbox
    {
        private readonly ExhaustedDelivery _pending = new(
            NotificationId: Guid.NewGuid(),
            EventId: Guid.NewGuid(),
            InstanceId: Guid.NewGuid(),
            CustomerRef: null,
            TemplateRef: "pt.test.notice",
            TemplatePackVersion: "pt.2026.1",
            TriggerKind: NotificationTriggerKind.Scheduled,
            CausationId: null,
            Attempts: 10,
            LastError: "receiver answered 503",
            ExhaustedAt: DateTimeOffset.UtcNow);

        public bool Published { get; private set; }

        public Task<IReadOnlyList<ExhaustedDelivery>> ClaimPendingAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExhaustedDelivery>>(Published ? [] : [_pending]);

        public Task MarkPublishedAsync(Guid notificationId, CancellationToken ct = default)
        {
            Published = true;
            return Task.CompletedTask;
        }
    }

    private sealed class AckAllPublisher : IExhaustedEventPublisher
    {
        public Task PublishAsync(ExhaustedDelivery exhausted, CancellationToken ct = default) => Task.CompletedTask;
    }
}
