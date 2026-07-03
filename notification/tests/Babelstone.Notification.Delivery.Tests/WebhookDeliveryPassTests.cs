using System.Net;
using System.Text.Json;
using Babelstone.Notification.Delivery;
using Xunit;
using static Babelstone.Notification.Delivery.Tests.DeliveryTestSupport;

namespace Babelstone.Notification.Delivery.Tests;

/// <summary>
/// The bd babelstone-60n8.4 acceptance evidence, end-to-end over a fake HTTP seam (no receiver, no
/// Docker): a scheduler-produced SCHEDULED signal is delivered via an HMAC-SHA256-signed webhook
/// (ADR-IC-011 §D3/§P2) through the per-service outbox (ADR-IC-004), at-least-once — a redelivery carries
/// the SAME composite <c>notification_id</c> as its idempotency key (ADR-PC-025 slot 4), retry follows the
/// §D4 backoff (Retry-After honoured, permanent 4xx abandoned, exhaustion dead-lettered), and a delivery
/// failure never propagates out of the drain (post-flag, ADR-PC-025 slot 5).
/// </summary>
public sealed class WebhookDeliveryPassTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 7, 2);

    [Fact]
    public async Task Delivers_a_scheduled_signal_with_a_verifiable_hmac_signature_and_the_contract_envelope()
    {
        var clock = new MutableClock(T0);
        var outbox = new InMemoryDeliveryOutbox();
        var handler = new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.OK));
        var signal = Signal();
        await outbox.EnqueueAsync(signal, clock.Now);

        await Pass(outbox, handler, clock).RunOnceAsync(Today);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(Endpoint, request.Uri!.ToString());

        // ADR-IC-011 §D3: the signature verifies over the shared secret + the timestamp header + the RAW
        // body — computed here with the receiver-side verifier, exactly as a consumer would.
        var timestamp = long.Parse(request.Headers[WebhookSignature.TimestampHeader]);
        Assert.Equal(clock.Now.ToUnixTimeSeconds(), timestamp);
        Assert.True(WebhookSignature.Verify(
            Secret, timestamp, request.Body, request.Headers[WebhookSignature.SignatureHeader]));
        Assert.Equal("sub-comms-01", request.Headers[WebhookSignature.SubscriptionIdHeader]);
        Assert.NotEmpty(request.Headers[WebhookSignature.DeliveryIdHeader]);

        // The §P2 envelope, snake_case, carrying the ADR-PC-025 payload with the SCREAMING_SNAKE_CASE
        // trigger_kind symbol the governed Avro contract names.
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal(signal.NotificationId.ToString("D"), root.GetProperty("idempotency_key").GetString());
        Assert.Equal(1, root.GetProperty("delivery_attempt").GetInt32());
        var notification = root.GetProperty("notification");
        Assert.Equal("SCHEDULED", notification.GetProperty("trigger_kind").GetString());
        Assert.Equal("pt.test.notice", notification.GetProperty("template_ref").GetString());
        Assert.Equal("pt.2026.1", notification.GetProperty("template_pack_version").GetString());
        Assert.Equal(JsonValueKind.Null, notification.GetProperty("causation_id").ValueKind);
        Assert.Equal("2026-07-02", notification.GetProperty("due_at").GetString());
        Assert.Equal("1012345", notification.GetProperty("data").GetProperty("total_payout_cents").GetString());

        Assert.Equal(DeliveryStatus.Delivered, (await outbox.GetAsync(signal.NotificationId))!.Status);
    }

    [Fact]
    public async Task A_retry_carries_the_same_idempotency_key_with_an_incremented_attempt_and_a_fresh_event_id()
    {
        var clock = new MutableClock(T0);
        var outbox = new InMemoryDeliveryOutbox();
        var handler = new FakeHandler(n => FakeHandler.Status(
            n == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK));
        var signal = Signal();
        await outbox.EnqueueAsync(signal, clock.Now);
        var pass = Pass(outbox, handler, clock);

        await pass.RunOnceAsync(Today); // attempt 1 → 500
        var pending = (await outbox.GetAsync(signal.NotificationId))!;
        Assert.Equal(DeliveryStatus.Pending, pending.Status);
        Assert.Equal(1, pending.Attempts);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(30), pending.NextAttemptAt); // §D4, jitter pinned 0

        await pass.RunOnceAsync(Today); // not due yet — nothing sent
        Assert.Single(handler.Requests);

        clock.Advance(TimeSpan.FromSeconds(31));
        await pass.RunOnceAsync(Today); // attempt 2 → 200

        Assert.Equal(2, handler.Requests.Count);
        using var first = JsonDocument.Parse(handler.Requests[0].Body);
        using var second = JsonDocument.Parse(handler.Requests[1].Body);

        // At-least-once with a STABLE dedupe anchor (ADR-PC-025 slot 4 / ADR-IC-011 §D2): the consumer
        // sees the same idempotency_key on every attempt; attempt number and per-attempt event_id move.
        Assert.Equal(
            first.RootElement.GetProperty("idempotency_key").GetString(),
            second.RootElement.GetProperty("idempotency_key").GetString());
        Assert.Equal(1, first.RootElement.GetProperty("delivery_attempt").GetInt32());
        Assert.Equal(2, second.RootElement.GetProperty("delivery_attempt").GetInt32());
        Assert.NotEqual(
            first.RootElement.GetProperty("event_id").GetString(),
            second.RootElement.GetProperty("event_id").GetString());

        Assert.Equal(DeliveryStatus.Delivered, (await outbox.GetAsync(signal.NotificationId))!.Status);
    }

    [Fact]
    public async Task A_permanent_4xx_abandons_immediately_without_retry()
    {
        var clock = new MutableClock(T0);
        var outbox = new InMemoryDeliveryOutbox();
        var handler = new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.NotFound));
        var signal = Signal();
        await outbox.EnqueueAsync(signal, clock.Now);
        var pass = Pass(outbox, handler, clock);

        await pass.RunOnceAsync(Today);
        clock.Advance(TimeSpan.FromHours(13));
        await pass.RunOnceAsync(Today);

        Assert.Single(handler.Requests); // §D4: retrying a misconfigured endpoint fixes nothing
        Assert.Equal(DeliveryStatus.Abandoned, (await outbox.GetAsync(signal.NotificationId))!.Status);
    }

    [Fact]
    public async Task A_429_retry_after_is_honoured_over_the_backoff_schedule()
    {
        var clock = new MutableClock(T0);
        var outbox = new InMemoryDeliveryOutbox();
        var handler = new FakeHandler(_ =>
        {
            var response = FakeHandler.Status(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromMinutes(10));
            return response;
        });
        var signal = Signal();
        await outbox.EnqueueAsync(signal, clock.Now);

        await Pass(outbox, handler, clock).RunOnceAsync(Today);

        var record = (await outbox.GetAsync(signal.NotificationId))!;
        Assert.Equal(DeliveryStatus.Pending, record.Status);
        Assert.Equal(T0 + TimeSpan.FromMinutes(10), record.NextAttemptAt); // the receiver's ask, not 30s
    }

    [Fact]
    public async Task Exhausted_retries_dead_letter_the_delivery()
    {
        var clock = new MutableClock(T0);
        var outbox = new InMemoryDeliveryOutbox();
        var handler = new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.ServiceUnavailable));
        var signal = Signal();
        await outbox.EnqueueAsync(signal, clock.Now);
        var pass = Pass(outbox, handler, clock, Options(maxAttempts: 3));

        for (var i = 0; i < 5; i++)
        {
            await pass.RunOnceAsync(Today);
            clock.Advance(TimeSpan.FromHours(3)); // beyond any §D4 step — always due again if pending
        }

        Assert.Equal(3, handler.Requests.Count); // MaxAttempts, then §D4 exhaustion — no attempt 4
        var record = (await outbox.GetAsync(signal.NotificationId))!;
        Assert.Equal(DeliveryStatus.DeadLettered, record.Status);
        Assert.Equal(3, record.Attempts);
    }

    [Fact]
    public async Task An_unreachable_receiver_is_transient_and_never_throws_out_of_the_drain()
    {
        var clock = new MutableClock(T0);
        var outbox = new InMemoryDeliveryOutbox();
        var handler = new FakeHandler(_ => throw new HttpRequestException("connection refused"));
        var signal = Signal();
        await outbox.EnqueueAsync(signal, clock.Now);

        // Post-flag (ADR-PC-025 slot 5): the drain completes normally — a delivery failure gates nothing
        // but its own record's next attempt.
        await Pass(outbox, handler, clock).RunOnceAsync(Today);

        var record = (await outbox.GetAsync(signal.NotificationId))!;
        Assert.Equal(DeliveryStatus.Pending, record.Status);
        Assert.Equal(1, record.Attempts);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(30), record.NextAttemptAt);
    }
}
