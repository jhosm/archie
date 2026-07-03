using Babelstone.Cadence;
using Microsoft.Extensions.Logging;

namespace Babelstone.Notification.Delivery;

/// <summary>
/// The per-tick delivery drain — the delivery estate's <see cref="ISchedulePass"/> (ADR-IC-019 mechanism
/// reuse: the same clock-owning <see cref="CadenceWorker"/> loop the scheduler and the lifecycle driver
/// run on). In plain terms: each tick it (1) drains whatever EVENT_DRIVEN <c>NotificationDue</c> signals
/// the bus source consumed into the outbox (bd babelstone-60n8.7 — the SAME outbox the SCHEDULED leg
/// fills, one transport parameterised by <c>trigger_kind</c>), then (2) claims the records whose retry
/// time has arrived, makes one signed webhook attempt for each — rendering the instance-pinned template
/// with render-time PII resolution first when the trigger is EVENT_DRIVEN — and records the §D4 outcome:
/// delivered, retry-with-backoff, abandoned (permanent 4xx), or dead-lettered (retries exhausted).
/// At-least-once falls out of the outbox: a record stays claimable until a 2xx confirms it, and every
/// attempt carries the same composite <c>notification_id</c> as its idempotency key (ADR-PC-025 slot 4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Strictly post-flag (ADR-PC-025 slot 5 / bd babelstone-60n8.4/.7).</b> A render OR delivery failure
/// marks the record and nothing else — it never throws out of the per-record loop, never touches the
/// scheduler's pass, and never signals anything back at the producing flow. A matured deposit is matured
/// whether or not its notice got through; the only thing a failure gates is its own record's next attempt.
/// A failed PII resolve (the surface down) is the ADR-PC-025 "retry the render later" case: the attempt
/// classifies transient and retries on the same §D4 backoff as a failed POST.
/// </para>
/// <para>
/// <b>Rendering happens per ATTEMPT, never at enqueue (ADR-PC-025 §PII).</b> The outbox persists the
/// STRUCTURAL signal only; PII is resolved by reference at render time, rides one POST transiently, and
/// is discarded — so no durable medium in this estate ever holds a name or NIF, and a subject shredded
/// between retries simply stops resolving.
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
    INotificationDueSource source,
    INoticeRenderer renderer,
    WebhookDeliveryOptions options,
    TimeProvider clock,
    ILogger<WebhookDeliveryPass>? logger = null,
    Func<double>? jitter = null) : ISchedulePass
{
    private readonly IDeliveryOutbox _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    private readonly WebhookDeliveryClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly INotificationDueSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly INoticeRenderer _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    private readonly WebhookDeliveryOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    // The ±25% jitter draw (§D4), injectable so tests pin it; production draws uniformly in [-1, 1].
    private readonly Func<double> _jitter = jitter ?? (() => (Random.Shared.NextDouble() * 2.0) - 1.0);

    /// <summary>
    /// Run ONE drain pass: ingest the consumed EVENT_DRIVEN signals into the outbox, then claim the due
    /// records (bounded by <see cref="WebhookDeliveryOptions.ClaimBatchSize"/>) and attempt each,
    /// recording the §D4 outcome. The <paramref name="asOf"/> date the worker supplies is unused —
    /// delivery due-ness is sub-day transport timing read off the injected clock (see the class remarks).
    /// </summary>
    public async Task RunOnceAsync(DateOnly asOf, CancellationToken ct = default)
    {
        await IngestConsumedSignalsAsync(ct);

        var due = await _outbox.ClaimDueAsync(_clock.GetUtcNow(), _options.ClaimBatchSize, ct);
        foreach (var record in due)
        {
            ct.ThrowIfCancellationRequested();
            await AttemptAsync(record, ct);
        }
    }

    /// <summary>
    /// The EVENT_DRIVEN ingress (bd babelstone-60n8.7): move whatever the bus source consumed into the
    /// shared outbox — idempotent on the composite <c>notification_id</c>, so bus redelivery (the
    /// expected at-least-once case, ADR-PC-025 slot 3) re-opens nothing. A source failure is INGRESS
    /// backpressure only: it is logged and retried next tick, and deliberately does NOT abort the pass —
    /// an unavailable bus must never stall the outbound retry queue.
    /// </summary>
    private async Task IngestConsumedSignalsAsync(CancellationToken ct)
    {
        IReadOnlyList<NotificationDueSignal> consumed;
        try
        {
            consumed = await _source.PollAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex, "NotificationDue source poll failed; ingress retries next tick (outbound drain continues).");
            return;
        }

        foreach (var signal in consumed)
        {
            if (await _outbox.EnqueueAsync(signal, _clock.GetUtcNow(), ct))
            {
                logger?.LogInformation(
                    "Consumed notification {NotificationId} ({TriggerKind}) enqueued for webhook delivery "
                    + "(ADR-IC-004 outbox; bd babelstone-60n8.7).",
                    signal.NotificationId, signal.TriggerKind);
            }
        }
    }

    private async Task AttemptAsync(DeliveryRecord record, CancellationToken ct)
    {
        var attempt = record.Attempts + 1;
        var result = await TryDeliverAsync(record, attempt, ct);

        switch (result.Outcome)
        {
            case WebhookDeliveryOutcome.Delivered:
                await _outbox.MarkDeliveredAsync(record.NotificationId, attempt, ct);
                // The aggregable outcome series (ADR-IC-007): recorded once per classified attempt,
                // beside the per-attempt log line the counter makes rate-queryable.
                NotificationDeliveryMetrics.RecordOutcome(NotificationDeliveryMetrics.OutcomeDelivered);
                logger?.LogInformation(
                    "Notification {NotificationId} ({TriggerKind}) delivered on attempt {Attempt}.",
                    record.NotificationId, record.Signal.TriggerKind, attempt);
                break;

            case WebhookDeliveryOutcome.PermanentFailure:
                // §D4: a non-429 4xx means the endpoint is misconfigured — abandon immediately and flag
                // for human review (the subscription-suspension event is the durable store's follow-up).
                await _outbox.MarkAbandonedAsync(record.NotificationId, attempt, result.Detail, ct);
                NotificationDeliveryMetrics.RecordOutcome(NotificationDeliveryMetrics.OutcomeAbandoned);
                logger?.LogError(
                    "Notification {NotificationId} ABANDONED on attempt {Attempt}: {Detail}. The webhook "
                    + "endpoint is misconfigured (ADR-IC-011 §D4) — human review required.",
                    record.NotificationId, attempt, result.Detail);
                break;

            case WebhookDeliveryOutcome.TransientFailure when attempt >= _options.MaxAttempts:
                // §D4 exhaustion (~12h of backoff): dead-letter. The producing flow is long committed and
                // unaffected (post-flag); the consumer/operator picks this up out of band.
                await _outbox.MarkDeadLetteredAsync(record.NotificationId, attempt, result.Detail, ct);
                NotificationDeliveryMetrics.RecordOutcome(NotificationDeliveryMetrics.OutcomeDeadLettered);
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
                NotificationDeliveryMetrics.RecordOutcome(NotificationDeliveryMetrics.OutcomeTransientRetry);
                logger?.LogWarning(
                    "Notification {NotificationId} delivery attempt {Attempt} failed transiently ({Detail}); "
                    + "next attempt at {NextAttemptAt:O}.",
                    record.NotificationId, attempt, result.Detail, nextAttemptAt);
                break;

            default:
                throw new InvalidOperationException($"Unknown delivery outcome '{result.Outcome}'.");
        }
    }

    /// <summary>
    /// Render (EVENT_DRIVEN only — the trigger_kind parameterisation, bd babelstone-60n8.7) and make one
    /// delivery attempt. A render failure — typically the PII-resolve surface momentarily down —
    /// classifies TRANSIENT: the attempt retries on the §D4 backoff (ADR-PC-025 "retry the render
    /// later"), and per-attempt rendering means the retry resolves fresh PII rather than replaying a
    /// stale or stranded copy.
    /// </summary>
    private async Task<WebhookDeliveryResult> TryDeliverAsync(
        DeliveryRecord record, int attempt, CancellationToken ct)
    {
        RenderedNotice? rendered = null;
        if (record.Signal.TriggerKind == NotificationTriggerKind.EventDriven)
        {
            try
            {
                rendered = await _renderer.RenderAsync(record.Signal, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new WebhookDeliveryResult(
                    WebhookDeliveryOutcome.TransientFailure, StatusCode: null, RetryAfter: null,
                    Detail: $"render failed: {ex.Message}");
            }
        }

        return await _client.DeliverAsync(record, attempt, rendered, ct);
    }
}
