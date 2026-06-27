using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Babelstone.Notification;

/// <summary>
/// The notification service's host shell — a <see cref="BackgroundService"/> poll-loop worker, the
/// same hosted-<c>BackgroundService</c> shape the engine's outbox relay and the orchestrator's
/// consume loop use (ADR-IC-011 runtime; the ADR-IC-004 per-service outbox-worker pattern). It is the
/// standing process the family-agnostic <see cref="NotificationSchedulePass"/> runs inside.
/// </summary>
/// <remarks>
/// <para>
/// <b>This worker OWNS the clock and the cadence (ADR-PC-023 §6).</b> The engine deliberately emits no
/// clock-driven signal — it exposes the read-model projections and lets this downstream consumer own the
/// question, the read cadence, the retry, and the backoff (ADR-PC-023 §3: "clock-driven timing has no
/// engine delivery guarantee because the engine emits nothing — the downstream scheduler's read cadence and
/// its own delivery contract own it"). So the loop here reads the wall clock (through
/// <see cref="TimeProvider"/>), derives today, and drives one <see cref="NotificationSchedulePass"/> per
/// tick. The CI determinism gate constrains engine folds and the engine emit path, NOT this clock-owning
/// component — this is its intended home (NOTIF-1).
/// </para>
/// <para>
/// <b>Family-agnostic by construction (ADR-IC-019 §D2/Amendment-A1).</b> The worker drives the core's
/// generic <see cref="NotificationSchedulePass"/>, which enumerates the registered family
/// <see cref="INotificationScheduleRule"/>s — it names no family and embeds no family rule (e.g. no
/// term-deposit window width or template ref). Adding a family is a new module at the host edge, zero core
/// diff.
/// </para>
/// <para>
/// <b>Cadence / retry / backoff, the same proven shape as the engine relay and the saga dispatcher.</b>
/// A clean pass waits one <see cref="NotificationSchedulerOptions.PollInterval"/> before the next.
/// A pass-cycle EXCEPTION (the engine read surface momentarily unavailable — a 5xx or a timeout, which a
/// family rule's read over the published contract surfaces rather than swallowing) is treated as
/// BACKPRESSURE: back off exponentially up to a ceiling and retry. The pass's dedupe (ADR-PC-025 slot 4)
/// makes a retried pass safe — re-reading the same world re-derives the same <c>notification_id</c>s and
/// raises nothing twice.
/// </para>
/// </remarks>
public sealed class NotificationWorker(
    NotificationSchedulePass schedulePass,
    NotificationSchedulerOptions options,
    TimeProvider clock,
    ILogger<NotificationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly NotificationSchedulePass _schedulePass =
        schedulePass ?? throw new ArgumentNullException(nameof(schedulePass));

    private readonly NotificationSchedulerOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    // The worker — not the engine, not the schedule pass — reads the clock (ADR-PC-023 §6). Injected
    // as TimeProvider so a test can drive the loop on a FakeTimeProvider with no real wall-clock wait.
    private readonly TimeProvider _clock =
        clock ?? throw new ArgumentNullException(nameof(clock));

    private readonly ILogger<NotificationWorker> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Notification worker started (ADR-IC-011). The schedule-pass poll loop owns the clock, " +
            "cadence, retry and backoff (ADR-PC-023 §6); family rules read the read-model over the " +
            "ADR-PC-027 contract.");

        var backoff = _options.PollInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // The worker owns the clock: derive TODAY here (not in the schedule pass, which stays
                // a deterministic function of the as-of date — ADR-PC-023 §6).
                var asOf = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
                await _schedulePass.RunOnceAsync(asOf, stoppingToken);

                backoff = _options.PollInterval; // a clean pass resets the backoff
                await Task.Delay(_options.PollInterval, _clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // graceful shutdown — the host signalled stop
            }
            catch (Exception ex)
            {
                // A pass-cycle failure (the engine read surface momentarily unavailable — a 5xx or a
                // timeout) is BACKPRESSURE, not a fatal error: back off exponentially up to the ceiling
                // and retry. Idempotency (ADR-PC-025 slot 4) makes the retried pass safe.
                _logger.LogWarning(
                    ex, "Notification schedule pass failed; backing off {Backoff} and retrying.", backoff);
                try
                {
                    await Task.Delay(backoff, _clock, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                backoff = backoff < MaxBackoff ? backoff + backoff : MaxBackoff;
            }
        }
    }
}

/// <summary>
/// The notification scheduler's cadence knobs — owned by the notification service, not the engine
/// (ADR-PC-023 §6: read cadence, retry and backoff are the downstream scheduler's). Notification reminders
/// are latency-tolerant, so the default poll interval is generous; an operator tunes it from configuration
/// at the host composition root.
/// </summary>
public sealed class NotificationSchedulerOptions
{
    /// <summary>How often the worker runs one schedule pass. Defaults to one hour — cheap (one bounded
    /// read per family rule per tick) and well inside a reminder's latency tolerance.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromHours(1);
}
