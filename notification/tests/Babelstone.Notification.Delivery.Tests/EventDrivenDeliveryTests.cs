using System.Net;
using System.Text.Json;
using Babelstone.Notification.Delivery;
using Xunit;
using static Babelstone.Notification.Delivery.Tests.DeliveryTestSupport;

namespace Babelstone.Notification.Delivery.Tests;

/// <summary>
/// The bd babelstone-60n8.7 acceptance evidence: an EVENT_DRIVEN <c>NotificationDue</c> consumed off the
/// bus seam travels the SAME transport as the SCHEDULED leg — same outbox, same HMAC signer, same §D4
/// retry, parameterised only by <c>trigger_kind</c> — with the instance-pinned template rendered per
/// attempt, PII resolved at render time by reference (never persisted, never on the bus — ADR-PC-025
/// §PII), idempotency on the composite <c>notification_id</c>, and render/delivery failures strictly
/// post-flag (ADR-PC-025 slot 5).
/// </summary>
public sealed class EventDrivenDeliveryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 7, 2);

    [Fact]
    public async Task A_consumed_event_driven_signal_is_rendered_with_render_time_pii_and_delivered_signed()
    {
        var clock = new MutableClock(T0);
        var outbox = new InMemoryDeliveryOutbox();
        var handler = new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.OK));
        var customerRef = Guid.NewGuid();
        var causationId = Guid.NewGuid();
        var signal = Signal(NotificationTriggerKind.EventDriven, customerRef, causationId);
        var source = new FakeNotificationDueSource();
        source.QueueBatch(signal);
        var pii = new FakePiiResolveClient { Pii = { ["name"] = "Maria Silva", ["nif"] = "123456789" } };

        await Pass(outbox, handler, clock, source: source, piiResolveClient: pii).RunOnceAsync(Today);

        // The shared transport delivered it: signed exactly like the SCHEDULED leg.
        var request = Assert.Single(handler.Requests);
        var timestamp = long.Parse(request.Headers[WebhookSignature.TimestampHeader]);
        Assert.True(WebhookSignature.Verify(
            Secret, timestamp, request.Body, request.Headers[WebhookSignature.SignatureHeader]));

        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal(signal.NotificationId.ToString("D"), root.GetProperty("idempotency_key").GetString());

        var notification = root.GetProperty("notification");
        Assert.Equal("EVENT_DRIVEN", notification.GetProperty("trigger_kind").GetString());
        Assert.Equal(causationId.ToString("D"), notification.GetProperty("causation_id").GetString());
        // customer_id — the governed Avro field name (bd babelstone-60n8.12: the webhook wire matches
        // contracts/avro/operations/NotificationDue.avsc, not the CLR signal's CustomerRef spelling).
        Assert.Equal(customerRef.ToString("D"), notification.GetProperty("customer_id").GetString());

        // The rendered slot: structural data + the PII resolved AT RENDER TIME by reference.
        var rendered = root.GetProperty("rendered");
        Assert.Equal("pt.test.notice", rendered.GetProperty("template_ref").GetString());
        Assert.Equal("pt.2026.1", rendered.GetProperty("template_pack_version").GetString());
        Assert.True(rendered.GetProperty("pii_resolved").GetBoolean());
        var fields = rendered.GetProperty("fields");
        Assert.Equal("Maria Silva", fields.GetProperty("name").GetString());
        Assert.Equal("123456789", fields.GetProperty("nif").GetString());
        Assert.Equal("1012345", fields.GetProperty("total_payout_cents").GetString());

        // The resolve was by reference, for exactly the configured fields.
        var resolve = Assert.Single(pii.Resolves);
        Assert.Equal(customerRef, resolve.SubjectRef);
        Assert.Equal(new[] { "name", "nif" }, resolve.Fields);

        Assert.Equal(DeliveryStatus.Delivered, (await outbox.GetAsync(signal.NotificationId))!.Status);
    }

    [Fact]
    public async Task Pii_never_lands_in_the_outbox_and_the_structural_notification_payload_stays_pii_free()
    {
        var clock = new MutableClock(T0);
        var outbox = new InMemoryDeliveryOutbox();
        var handler = new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.OK));
        var signal = Signal(NotificationTriggerKind.EventDriven, Guid.NewGuid(), Guid.NewGuid());
        var source = new FakeNotificationDueSource();
        source.QueueBatch(signal);
        var pii = new FakePiiResolveClient { Pii = { ["name"] = "Maria Silva" } };

        await Pass(outbox, handler, clock, source: source, piiResolveClient: pii).RunOnceAsync(Today);

        // ADR-PC-025 §PII: the durable record holds the STRUCTURAL signal only — the resolved name exists
        // nowhere but the in-flight request (rendered per attempt, discarded with it).
        var record = (await outbox.GetAsync(signal.NotificationId))!;
        Assert.DoesNotContain("name", record.Signal.Data.Keys);
        Assert.DoesNotContain(record.Signal.Data.Values, value => value.Contains("Maria", StringComparison.Ordinal));

        // And the wire's structural notification payload carries the reference, never the resolved value.
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var dataJson = body.RootElement.GetProperty("notification").GetProperty("data").GetRawText();
        Assert.DoesNotContain("Maria", dataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_pii_resolve_retries_on_the_backoff_and_resolves_fresh_on_the_retry()
    {
        var clock = new MutableClock(T0);
        var outbox = new InMemoryDeliveryOutbox();
        var handler = new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.OK));
        var signal = Signal(NotificationTriggerKind.EventDriven, Guid.NewGuid(), Guid.NewGuid());
        var source = new FakeNotificationDueSource();
        source.QueueBatch(signal);
        var pii = new FakePiiResolveClient { ThrowOnResolve = new HttpRequestException("resolve surface down") };
        var pass = Pass(outbox, handler, clock, source: source, piiResolveClient: pii);

        // Attempt 1: the render fails (resolve surface down) — post-flag, the pass completes; the record
        // retries on the §D4 backoff ("retry the render later", ADR-PC-025) and NOTHING was posted.
        await pass.RunOnceAsync(Today);
        Assert.Empty(handler.Requests);
        var record = (await outbox.GetAsync(signal.NotificationId))!;
        Assert.Equal(DeliveryStatus.Pending, record.Status);
        Assert.Equal(1, record.Attempts);
        Assert.Equal(T0 + TimeSpan.FromSeconds(30), record.NextAttemptAt);

        // Attempt 2: the surface is back — the retry renders FRESH (per-attempt rendering) and delivers.
        pii.ThrowOnResolve = null;
        pii.Pii["name"] = "Maria Silva";
        clock.Advance(TimeSpan.FromSeconds(31));
        await pass.RunOnceAsync(Today);

        var request = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(
            "Maria Silva",
            body.RootElement.GetProperty("rendered").GetProperty("fields").GetProperty("name").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("delivery_attempt").GetInt32());
        Assert.Equal(DeliveryStatus.Delivered, (await outbox.GetAsync(signal.NotificationId))!.Status);
    }

    [Fact]
    public async Task A_shredded_subject_renders_structurally_and_still_delivers()
    {
        var clock = new MutableClock(T0);
        var outbox = new InMemoryDeliveryOutbox();
        var handler = new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.OK));
        var signal = Signal(NotificationTriggerKind.EventDriven, Guid.NewGuid(), Guid.NewGuid());
        var source = new FakeNotificationDueSource();
        source.QueueBatch(signal);

        // The default fake resolves nothing — the crypto-shredded outcome (ADR-PC-004 §P3).
        await Pass(outbox, handler, clock, source: source).RunOnceAsync(Today);

        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var rendered = body.RootElement.GetProperty("rendered");
        Assert.False(rendered.GetProperty("pii_resolved").GetBoolean());
        Assert.Equal("1012345", rendered.GetProperty("fields").GetProperty("total_payout_cents").GetString());
        Assert.Equal(DeliveryStatus.Delivered, (await outbox.GetAsync(signal.NotificationId))!.Status);
    }

    [Fact]
    public async Task Bus_redelivery_of_the_same_notification_id_is_absorbed_by_the_shared_outbox()
    {
        var clock = new MutableClock(T0);
        var outbox = new InMemoryDeliveryOutbox();
        var handler = new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.OK));
        var signal = Signal(NotificationTriggerKind.EventDriven, Guid.NewGuid(), Guid.NewGuid());
        var source = new FakeNotificationDueSource();
        source.QueueBatch(signal);
        source.QueueBatch(signal); // the at-least-once bus re-presents it (ADR-PC-025 slot 3)
        var pass = Pass(outbox, handler, clock, source: source);

        await pass.RunOnceAsync(Today); // ingests + delivers
        await pass.RunOnceAsync(Today); // re-ingests the redelivery — absorbed, nothing re-sent

        Assert.Single(handler.Requests);
        Assert.Equal(DeliveryStatus.Delivered, (await outbox.GetAsync(signal.NotificationId))!.Status);
    }

    [Fact]
    public async Task A_failing_bus_source_never_stalls_the_outbound_drain()
    {
        var clock = new MutableClock(T0);
        var outbox = new InMemoryDeliveryOutbox();
        var handler = new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.OK));
        var scheduled = Signal(); // a SCHEDULED delivery already owed
        await outbox.EnqueueAsync(scheduled, clock.Now);
        var source = new FakeNotificationDueSource
        {
            OnPoll = () => throw new InvalidOperationException("bus unavailable"),
        };

        // Ingress backpressure is logged and retried next tick; the owed delivery still goes out.
        await Pass(outbox, handler, clock, source: source).RunOnceAsync(Today);

        Assert.Single(handler.Requests);
        Assert.Equal(DeliveryStatus.Delivered, (await outbox.GetAsync(scheduled.NotificationId))!.Status);
    }

    [Fact]
    public async Task The_scheduled_leg_rides_the_same_transport_without_a_rendered_slot()
    {
        var clock = new MutableClock(T0);
        var outbox = new InMemoryDeliveryOutbox();
        var handler = new FakeHandler(_ => FakeHandler.Status(HttpStatusCode.OK));
        var pii = new FakePiiResolveClient { Pii = { ["name"] = "Maria Silva" } };
        var signal = Signal(); // SCHEDULED
        await outbox.EnqueueAsync(signal, clock.Now);

        await Pass(outbox, handler, clock, piiResolveClient: pii).RunOnceAsync(Today);

        // Parameterised by trigger_kind (bd babelstone-60n8.7): the SCHEDULED leg is delivered by the
        // SAME pass and signer, but rendering (and any PII resolve) is the downstream consumer's
        // (ADR-PC-025 Decision 2) — no render call, an explicit null rendered slot.
        Assert.Empty(pii.Resolves);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("rendered").ValueKind);
        Assert.Equal(DeliveryStatus.Delivered, (await outbox.GetAsync(signal.NotificationId))!.Status);
    }
}
