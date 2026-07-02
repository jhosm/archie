using Babelstone.Cadence;
using Microsoft.Extensions.Logging;

namespace Babelstone.Notification.Delivery;

/// <summary>
/// The per-tick delivery drain — the delivery estate's <see cref="ISchedulePass"/> (ADR-IC-019 mechanism
/// reuse: the same clock-owning <see cref="CadenceWorker"/> loop the scheduler and the lifecycle driver
/// run on). In plain terms: each tick it claims the outbox records whose retry time has arrived, makes one
/// signed webhook attempt for each, and records the §D4 outcome — delivered, retry-with-backoff, abandoned
/// (permanent 4xx), or dead-lettered (retries exhausted). At-least-once falls out of the outbox: a record
/// stays claimable until a 2xx confirms it, and every attempt carries the same composite
/// <c>notification_id</c> as its idempotency key (ADR-PC-025 slot 4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Strictly post-flag (ADR-PC-025 slot 5 / bd babelstone-60n8.4).</b> A delivery failure marks the
/// record and nothing else — it never throws out of the per-record loop, never touches the scheduler's
/// pass, and never signals anything back at the producing flow. A matured deposit is matured whether or
/// not its notice got through; the only thing a failure gates is its own record's next attempt.
/// </para>
/// <para>
/// <b>Why this pass reads a fine-grained clock (unlike the domain passes).</b> The ADR-PC-023 §6 rule —
/// the pass is a deterministic function of the as-of DATE — targets domain schedule passes deciding
/// <em>whether work is due</em>. Retry backoff is sub-day transport timing (§D4 starts at 30 seconds), so
/// this pass takes the injected <see cref="TimeProvider"/> for due-record claims and next-attempt stamps;
/// determinism is preserved the same way the worker's is — a test drives a fake clock, and the jitter
/// draw is an injectable function (pure <see cref="WebhookRetrySchedule"/> underneath).
/// </para>
/// </remarks>
public sealed class WebhookDeliveryPass(
    IDeliveryOutbox outbox,
    WebhookDeliveryClient client,
    WebhookDeliveryOptions options,
    TimeProvider clock,
    ILogger<WebhookDeliveryPass>? logger = null,
    Func<double>? jitter = null) : ISchedulePass
{
    private readonly IDeliveryOutbox _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    private readonly WebhookDeliveryClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly WebhookDeliveryOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    // The ±25% jitter draw (§D4), injectable so tests pin it; production draws uniformly in [-1, 1].
    private readonly Func<double> _jitter = jitter ?? (() => (Random.Shared.NextDouble() * 2.0) - 1.0);

    /// <summary>
    /// Run ONE drain pass: claim the due records (bounded by
    /// <see cref="WebhookDeliveryOptions.ClaimBatchSize"/>) and attempt each, recording the §D4 outcome.
    /// The <paramref name="asOf"/> date the worker supplies is unused — delivery due-ness is sub-day
    /// transport timing read off the injected clock (see the class remarks).
    /// </summary>
    public async Task RunOnceAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var due = await _outbox.ClaimDueAsync(_clock.GetUtcNow(), _options.ClaimBatchSize, ct);
        foreach (var record in due)
        {
            ct.ThrowIfCancellationRequested();
            await AttemptAsync(record, ct);
        }
    }

    private async Task AttemptAsync(DeliveryRecord record, CancellationToken ct)
    {
        var attempt = record.Attempts + 1;
        var result = await _client.DeliverAsync(record, attempt, ct);

        switch (result.Outcome)
        {
            case WebhookDeliveryOutcome.Delivered:
                await _outbox.MarkDeliveredAsync(record.NotificationId, attempt, ct);
                logger?.LogInformation(
                    "Notification {NotificationId} ({TriggerKind}) delivered on attempt {Attempt}.",
                    record.NotificationId, record.Signal.TriggerKind, attempt);
                break;

            case WebhookDeliveryOutcome.PermanentFailure:
                // §D4: a non-429 4xx means the endpoint is misconfigured — abandon immediately and flag
                // for human review (the subscription-suspension event is the durable store's follow-up).
                await _outbox.MarkAbandonedAsync(record.NotificationId, attempt, result.Detail, ct);
                logger?.LogError(
                    "Notification {NotificationId} ABANDONED on attempt {Attempt}: {Detail}. The webhook "
                    + "endpoint is misconfigured (ADR-IC-011 §D4) — human review required.",
                    record.NotificationId, attempt, result.Detail);
                break;

            case WebhookDeliveryOutcome.TransientFailure when attempt >= _options.MaxAttempts:
                // §D4 exhaustion (~12h of backoff): dead-letter. The producing flow is long committed and
                // unaffected (post-flag); the consumer/operator picks this up out of band.
                await _outbox.MarkDeadLetteredAsync(record.NotificationId, attempt, result.Detail, ct);
                logger?.LogError(
                    "Notification {NotificationId} DEAD-LETTERED after {Attempt} attempts: {Detail} "
                    + "(ADR-IC-011 §D4 exhaustion).",
                    record.NotificationId, attempt, result.Detail);
                break;

            case WebhookDeliveryOutcome.TransientFailure:
                // §D4 backoff: the receiver's Retry-After wins when it sent one (429); otherwise the
                // exponential schedule with the injected jitter draw.
                var delay = result.RetryAfter ?? WebhookRetrySchedule.NextDelay(attempt, _jitter());
                var nextAttemptAt = _clock.GetUtcNow() + delay;
                await _outbox.MarkAttemptFailedAsync(
                    record.NotificationId, attempt, nextAttemptAt, result.Detail, ct);
                logger?.LogWarning(
                    "Notification {NotificationId} delivery attempt {Attempt} failed transiently ({Detail}); "
                    + "next attempt at {NextAttemptAt:O}.",
                    record.NotificationId, attempt, result.Detail, nextAttemptAt);
                break;

            default:
                throw new InvalidOperationException($"Unknown delivery outcome '{result.Outcome}'.");
        }
    }
}
