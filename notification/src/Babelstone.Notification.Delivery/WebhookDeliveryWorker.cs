using Babelstone.Cadence;
using Microsoft.Extensions.Logging;

namespace Babelstone.Notification.Delivery;

/// <summary>
/// The delivery estate's host shell — the outbound-webhook face of the shared clock-owning
/// <see cref="CadenceWorker"/> (ADR-PC-036 §Decision 2 + ADR-IC-019 mechanism reuse), exactly the shape
/// <c>NotificationWorker</c> gives the scheduler: a thin named subclass that binds the generic poll loop to
/// the <see cref="WebhookDeliveryPass"/> drain. All loop behaviour — the cadence, the backpressure
/// exponential backoff on a failed tick — is the shared worker's; this type adds only its own DI-resolvable
/// typed logger and a distinct log/trace category, plus the distinct
/// <see cref="WebhookDeliveryCadenceOptions"/> so its fast drain cadence coexists with the scheduler's
/// hourly cadence in one host container.
/// </summary>
/// <remarks>
/// Runs as a second hosted <c>BackgroundService</c> BESIDE the scheduler's <c>NotificationWorker</c> — the
/// per-service outbox worker of ADR-IC-004: the scheduler decides and enqueues; this worker drains and
/// delivers. Their only coupling is the outbox between them, so a slow receiver never stalls a scheduling
/// pass (post-flag, ADR-PC-025 slot 5).
/// </remarks>
public sealed class WebhookDeliveryWorker(
    WebhookDeliveryPass deliveryPass,
    WebhookDeliveryCadenceOptions options,
    TimeProvider clock,
    ILogger<WebhookDeliveryWorker> logger)
    : CadenceWorker(deliveryPass, options, clock, logger);
