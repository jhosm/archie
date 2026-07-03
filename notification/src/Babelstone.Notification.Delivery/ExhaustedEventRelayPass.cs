using Babelstone.Cadence;
using Babelstone.Telemetry;
using Microsoft.Extensions.Logging;

namespace Babelstone.Notification.Delivery;

/// <summary>
/// The per-tick exhaustion relay (ADR-IC-011): claim the PENDING
/// <c>NotificationDeliveryExhausted</c> outbox rows, publish each to the backbone, flip it PUBLISHED.
/// The notification estate's own miniature of the engine's outbox relay (ADR-IC-004), on the same
/// clock-owning cadence machinery (ADR-IC-019 mechanism reuse). A publish failure aborts the pass and
/// leaves the row — and everything behind it — PENDING for the next tick: an unreachable broker or
/// Schema Registry is BACKPRESSURE, never data loss and never a FAILED state.
/// </summary>
/// <remarks>
/// Rows are published one at a time, in exhaustion order, each flip committed only after its broker
/// ack — the dual-publish window shrinks to a crash between ack and flip, which the consumer absorbs
/// by dedupe on <c>notification_id</c> (or the stable <c>ce_id</c>: the row's DB-generated
/// <c>event_id</c> republishes unchanged). No lease is taken: the estate runs one relay, the same
/// single-drainer stance as the webhook drain it sits beside.
/// </remarks>
public sealed class ExhaustedEventRelayPass(
    IExhaustedDeliveryOutbox outbox,
    IExhaustedEventPublisher publisher,
    WebhookDeliveryOptions options,
    ILogger<ExhaustedEventRelayPass>? logger = null) : ISchedulePass
{
    private readonly IExhaustedDeliveryOutbox _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    private readonly IExhaustedEventPublisher _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    private readonly WebhookDeliveryOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Run ONE relay pass (the <paramref name="asOf"/> date is unused — outbox drains are transport
    /// timing, not domain due-ness; the same stance as <see cref="WebhookDeliveryPass"/>). Claims at
    /// most <see cref="WebhookDeliveryOptions.ClaimBatchSize"/> rows. Throws on a publish failure so
    /// the cadence worker's backoff engages (backpressure, ADR-IC-004).
    /// </summary>
    public async Task RunOnceAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var pending = await _outbox.ClaimPendingAsync(_options.ClaimBatchSize, ct);
        foreach (var exhausted in pending)
        {
            ct.ThrowIfCancellationRequested();

            // Publish, THEN flip: a crash between the two republishes the same event_id next tick —
            // at-least-once with a stable id, the ADR-IC-004 posture. The flip never precedes the ack.
            await _publisher.PublishAsync(exhausted, ct);
            await _outbox.MarkPublishedAsync(exhausted.NotificationId, ct);
            NotificationDeliveryMetrics.RecordExhaustedPublished();

            // Information, not Warning: the paging signal is the DEAD-LETTERED Error the drain pass
            // emitted at flip time; this line records that the announcement went out (the alertable
            // structured event an operator keys the out-of-band pickup on).
            logger?.LogInformation(
                BabelstoneEvents.NotificationDeliveryExhaustedPublished,
                "NotificationDeliveryExhausted published for notification {NotificationId} "
                + "({TriggerKind}, {Attempts} attempts, causation {CausationId}): {LastError}. The "
                + "obligation is dead-lettered (ADR-IC-011) — operator/consumer pickup is out of band.",
                exhausted.NotificationId, exhausted.TriggerKind, exhausted.Attempts,
                exhausted.CausationId, exhausted.LastError);
        }
    }
}

/// <summary>
/// The exhaustion relay's cadence knobs — a DISTINCT <see cref="CadenceSchedulerOptions"/> subclass
/// (the same coexistence seam as <see cref="WebhookDeliveryCadenceOptions"/>) so the relay's poll, the
/// delivery drain's poll, and the scheduler's poll all live in one host container without colliding on
/// the shared options registration.
/// </summary>
public sealed class ExhaustedRelayCadenceOptions : CadenceSchedulerOptions
{
    /// <summary>A fresh instance defaulting <see cref="CadenceSchedulerOptions.PollInterval"/> to
    /// 30 seconds — exhaustions are rare (each one is ~12h of failed retries first), so the relay
    /// needs no fast poll; 30s keeps the announcement prompt without hammering an idle table. Tuned
    /// via <c>Notification:Webhook:ExhaustedRelayPollIntervalSeconds</c>.</summary>
    public ExhaustedRelayCadenceOptions()
    {
        PollInterval = TimeSpan.FromSeconds(30);
    }
}

/// <summary>
/// The exhaustion relay's host shell — a thin named <see cref="CadenceWorker"/> subclass (the same
/// shape as <see cref="WebhookDeliveryWorker"/> and the scheduler's <c>NotificationWorker</c>): the
/// shared loop owns the clock, the cadence, and the failed-tick backoff; this type adds only its
/// DI-resolvable typed logger, a distinct log/trace category, and the distinct
/// <see cref="ExhaustedRelayCadenceOptions"/>.
/// </summary>
public sealed class ExhaustedEventRelayWorker(
    ExhaustedEventRelayPass relayPass,
    ExhaustedRelayCadenceOptions options,
    TimeProvider clock,
    ILogger<ExhaustedEventRelayWorker> logger)
    : CadenceWorker(relayPass, options, clock, logger);
