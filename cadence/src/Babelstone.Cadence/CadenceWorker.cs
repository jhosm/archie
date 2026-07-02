using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Babelstone.Cadence;

/// <summary>
/// The generic clock-owning poll-loop worker (ADR-PC-036 §Decision 2 + ADR-IC-019 mechanism reuse) — a
/// <see cref="BackgroundService"/>, the same hosted-<c>BackgroundService</c> shape the engine's outbox relay and
/// the orchestrator's consume loop use (ADR-IC-011 runtime). It is the standing process a domain
/// <see cref="ISchedulePass"/> runs inside: the notification scheduler's per-tick pass today (ADR-IC-019), the
/// lifecycle-command driver's pass next (ADR-PC-036). This type is the machinery the two share; subclass it for
/// a named worker (e.g. the notification estate's <c>NotificationWorker</c>) or register it directly.
/// </summary>
/// <remarks>
/// <para>
/// <b>This worker OWNS the clock and the cadence (ADR-PC-023 §6).</b> The engine deliberately emits no
/// clock-driven signal — it exposes the read-model projections and lets a downstream consumer own the question,
/// the read cadence, the retry, and the backoff (ADR-PC-023 §3). So the loop here reads the wall clock (through
/// <see cref="TimeProvider"/>), derives today, and drives one <see cref="ISchedulePass"/> per tick. The CI
/// determinism gate constrains engine folds and the engine emit path, NOT this clock-owning component — this is
/// its intended home.
/// </para>
/// <para>
/// <b>Domain-agnostic by construction.</b> The worker drives an injected <see cref="ISchedulePass"/> — it names
/// no family, no product, and no notification template; the per-tick semantics live entirely in the pass. A new
/// clock-driven consumer is a new pass implementation plus its own host wiring, with zero diff to this machinery.
/// </para>
/// <para>
/// <b>Cadence / retry / backoff, the same proven shape as the engine relay and the saga dispatcher.</b>
/// A clean pass waits one <see cref="CadenceSchedulerOptions.PollInterval"/> before the next. A pass-cycle
/// EXCEPTION (a downstream read surface momentarily unavailable — a 5xx or a timeout the pass surfaces rather
/// than swallowing) is treated as BACKPRESSURE: back off exponentially up to a ceiling and retry. The pass's own
/// dedupe makes a retried pass safe — re-reading the same world re-derives the same ids and acts on nothing
/// twice.
/// </para>
/// </remarks>
public class CadenceWorker : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly ISchedulePass _schedulePass;
    private readonly CadenceSchedulerOptions _options;

    // The worker — not the domain pass — reads the clock (ADR-PC-023 §6). Injected as TimeProvider so a test
    // can drive the loop on a fake clock with no real wall-clock wait.
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    // The SHARED Babelstone.Engine ActivitySource, injected by the host (which owns the OTel wiring and the
    // Babelstone.Telemetry contract). It arrives as a plain BCL ActivitySource so this generic library stays
    // framework-only and extraction-ready (ADR-PC-019 §P2 — no engine/src or family reference): the host,
    // not cadence, names Babelstone.Telemetry. Optional/nullable: a host that wires no tracer (or a test)
    // leaves it null and the per-tick span becomes a no-op, exactly like an unlistened ActivitySource.
    private readonly ActivitySource? _activitySource;

    public CadenceWorker(
        ISchedulePass schedulePass,
        CadenceSchedulerOptions options,
        TimeProvider clock,
        ILogger logger,
        ActivitySource? activitySource = null)
    {
        _schedulePass = schedulePass ?? throw new ArgumentNullException(nameof(schedulePass));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _activitySource = activitySource;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Cadence worker started (ADR-IC-011). The poll loop owns the clock, cadence, retry and backoff " +
            "(ADR-PC-023 §6); the registered schedule pass runs one tick per poll interval.");

        var backoff = _options.PollInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // The worker owns the clock: derive TODAY here (not in the schedule pass, which stays a
                // deterministic function of the as-of date — ADR-PC-023 §6).
                var asOf = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
                await RunPassAsync(asOf, stoppingToken);

                backoff = _options.PollInterval; // a clean pass resets the backoff
                await Task.Delay(_options.PollInterval, _clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // graceful shutdown — the host signalled stop
            }
            catch (Exception ex)
            {
                // A pass-cycle failure (a downstream read surface momentarily unavailable — a 5xx or a
                // timeout) is BACKPRESSURE, not a fatal error: back off exponentially up to the ceiling and
                // retry. The pass's own idempotency makes the retried pass safe.
                _logger.LogWarning(
                    ex, "Cadence schedule pass failed; backing off {Backoff} and retrying.", backoff);
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

    // One per-tick pass, wrapped in the manual `cadence.pass` span (ADR-IC-007 <entity>.<operation>). The span
    // is opened in this impure loop shell — never inside the pass, which stays a deterministic function of the
    // as-of date (ADR-PC-023 §6). The span name and the `babelstone.as_of` tag key are string literals rather
    // than Babelstone.Telemetry constants ON PURPOSE: this library is extraction-ready and must not reference
    // engine/src (ADR-PC-019 §P2), so it cannot name BabelstoneAttributes; the literals mirror that contract's
    // values (`BabelstoneAttributes.AsOf`). With no injected source or no listener, StartActivity returns null
    // and this is a near-zero-cost no-op. The as-of date is a plain calendar date — structural, never PII.
    private async Task RunPassAsync(DateOnly asOf, CancellationToken stoppingToken)
    {
        using var activity = _activitySource?.StartActivity("cadence.pass");
        activity?.SetTag("babelstone.as_of", asOf.ToString("O"));
        try
        {
            await _schedulePass.RunOnceAsync(asOf, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Mark the tick's span as failed before it disposes, so a backpressure retry is visible in the
            // trace as an errored pass; the exception still propagates to the loop's back-off handler. A
            // cancellation (graceful shutdown) is filtered out above — it is not a pass failure.
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}

/// <summary>
/// A cadence worker's cadence knobs — owned by the consuming service, not the engine (ADR-PC-023 §6: read
/// cadence, retry and backoff are the downstream consumer's). Latency-tolerant work (a notification reminder,
/// a lifecycle command on a generous due date) defaults to a generous poll interval; an operator tunes it from
/// configuration at the host composition root. A single-consumer host binds this type directly (as the
/// notification scheduler does); it is left UNSEALED only so a consumer that genuinely needs a distinct named
/// options type or DI key can subclass it.
/// </summary>
public class CadenceSchedulerOptions
{
    /// <summary>How often the worker runs one schedule pass. Defaults to one hour — cheap (one bounded read
    /// per registered rule per tick) and well inside a latency-tolerant signal's tolerance.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromHours(1);
}
