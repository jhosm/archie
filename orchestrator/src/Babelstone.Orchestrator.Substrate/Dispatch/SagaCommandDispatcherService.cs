using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Babelstone.Orchestrator.Dispatch;

/// <summary>
/// Hosts the <see cref="SagaCommandDispatchDrainer"/> as an in-process <see cref="BackgroundService"/>
/// — the saga analogue of the engine's <c>OutboxRelayService</c>, but delivering to HTTP rather than
/// Redpanda (bd babelstone-t7o3.3, ADR-PC-029). The saga now actually DRIVES the engine: each command
/// the saga decided into <c>saga_outbox</c> is drained here and POSTed to its target.
/// </summary>
/// <remarks>
/// Poll loop, the same proven shape as the engine relay: drain a batch every
/// <see cref="SagaCommandDispatcherOptions.PollInterval"/>; when the drain emptied the deliverable
/// tail (zero terminal flips this cycle) wait a poll interval, otherwise loop straight on to the next
/// batch while there is backlog. A drain-cycle EXCEPTION (the orchestrator DB momentarily
/// unavailable) is treated as BACKPRESSURE — back off exponentially up to a ceiling and retry,
/// leaving rows PENDING. A per-row TRANSIENT delivery failure (5xx/timeout) is NOT an exception: the
/// drainer leaves that row PENDING internally and the loop re-attempts it next cycle — idempotency on
/// the engine's command_dedup makes the retry safe. A per-row TERMINAL refusal (4xx) is recorded
/// FAILED by the drainer and surfaced for the saga's compensation path, never retried.
/// </remarks>
public sealed class SagaCommandDispatcherService(
    SagaCommandDispatchDrainer drainer,
    SagaCommandDispatcherOptions options,
    ILogger<SagaCommandDispatcherService>? logger = null) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly SagaCommandDispatchDrainer _drainer = drainer ?? throw new ArgumentNullException(nameof(drainer));
    private readonly SagaCommandDispatcherOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = _options.PollInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settled = await _drainer.DrainOnceAsync(stoppingToken);
                backoff = _options.PollInterval; // a clean cycle resets the backoff
                if (settled == 0)
                {
                    // Nothing settled (an empty tail, or only transient failures left PENDING): wait a
                    // poll interval before re-attempting. A transient 5xx therefore retries on the next
                    // tick — idempotency makes it safe.
                    await Task.Delay(_options.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // graceful shutdown
            }
            catch (Exception ex)
            {
                // A drain-cycle failure (the orchestrator DB momentarily unavailable) is backpressure:
                // rows stay PENDING, back off exponentially up to the ceiling, then keep retrying.
                logger?.LogWarning(
                    ex, "Saga command dispatch cycle failed; backing off {Backoff} and retrying (rows stay PENDING).", backoff);
                try
                {
                    await Task.Delay(backoff, stoppingToken);
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
