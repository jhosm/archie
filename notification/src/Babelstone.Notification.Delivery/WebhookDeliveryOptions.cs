using Babelstone.Cadence;

namespace Babelstone.Notification.Delivery;

/// <summary>
/// The webhook delivery leg's knobs, bound from the <c>Notification:Webhook</c> configuration section by
/// <see cref="NotificationDeliveryServiceCollectionExtensions.AddNotificationWebhookDelivery"/>. In plain
/// terms: where the communications-system consumer's endpoint is, the shared secret that signs every
/// delivery (ADR-IC-011 §D3), and how hard to try before dead-lettering (§D4).
/// </summary>
/// <remarks>
/// The endpoint/secret pair stands in for an ADR-IC-011 §P1 pre-registered subscription: the notification
/// consumer here is the bank's own communications system, a statically-provisioned counterparty
/// (ADR-PC-025), not a self-service registrant — so the subscription-registration API surface is out of
/// scope and the pair arrives via configuration (the secret via environment/secret-store, never committed;
/// the same posture as every other credential — ADR-PC-004 Amendment A1). The §P1 SSRF posture still
/// applies at composition: HTTPS-only, with a documented loopback exception for local dev stacks.
/// </remarks>
public sealed class WebhookDeliveryOptions
{
    /// <summary>The communications-system consumer's HTTPS endpoint every delivery POSTs to
    /// (<c>Notification:Webhook:EndpointUrl</c>). HTTPS-only (ADR-IC-011 §P1); plain HTTP is accepted for
    /// loopback hosts only (the local dev stack).</summary>
    public required string EndpointUrl { get; init; }

    /// <summary>The shared HMAC secret (ADR-IC-011 §D3), from <c>Notification:Webhook:Secret</c> — an
    /// environment/secret-store value, never committed, never logged (ADR-IC-011 residual risks).</summary>
    public required string Secret { get; init; }

    /// <summary>The subscription id stamped on the <c>X-Webhook-Subscription-Id</c> header
    /// (<c>Notification:Webhook:SubscriptionId</c>); the header is omitted when unset (no
    /// registration surface exists for this statically-provisioned consumer).</summary>
    public string? SubscriptionId { get; init; }

    /// <summary>Delivery attempts before a signal is dead-lettered (ADR-IC-011 §D4 — 10 attempts,
    /// roughly 12 hours of backoff). <c>Notification:Webhook:MaxAttempts</c>.</summary>
    public int MaxAttempts { get; init; } = 10;

    /// <summary>The most due deliveries one drain pass attempts (<c>Notification:Webhook:ClaimBatchSize</c>)
    /// — bounds a pass the same way the engine relay bounds its outbox drain.</summary>
    public int ClaimBatchSize { get; init; } = 50;

    /// <summary>The pack version stamped as <c>template_pack_version</c> on SCHEDULED signals this estate
    /// itself produces (<c>Notification:Webhook:TemplatePackVersion</c>, defaulting to the host's pinned
    /// <c>Engine:PackVersion</c>). v1 simplification: the scheduler host resolves ONE pinned pack, so its
    /// outbound signals pin that version; true per-instance pinning (ADR-PC-009) rides in when the read
    /// surface exposes the instance's pinned version.</summary>
    public required string TemplatePackVersion { get; init; }
}

/// <summary>
/// The delivery worker's cadence knobs — a DISTINCT options type (the <see cref="CadenceSchedulerOptions"/>
/// subclass seam that type is left unsealed for) so the delivery drain's fast poll and the scheduler's
/// hourly poll coexist in one host container without colliding on the shared options registration.
/// Deliveries are retried on sub-minute backoff steps (ADR-IC-011 §D4 starts at 30 seconds), so the drain
/// polls far faster than the reminder scheduler.
/// </summary>
public sealed class WebhookDeliveryCadenceOptions : CadenceSchedulerOptions
{
    /// <summary>A fresh instance defaulting <see cref="CadenceSchedulerOptions.PollInterval"/> to
    /// 15 seconds — half the first ADR-IC-011 §D4 backoff step, so a due retry waits at most one tick
    /// beyond its scheduled time. Tuned via <c>Notification:Webhook:PollIntervalSeconds</c>.</summary>
    public WebhookDeliveryCadenceOptions()
    {
        PollInterval = TimeSpan.FromSeconds(15);
    }
}
