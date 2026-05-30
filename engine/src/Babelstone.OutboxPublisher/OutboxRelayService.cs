using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Babelstone.OutboxPublisher;

/// <summary>
/// Hosts the <see cref="OutboxDrainer"/> as an in-process <see cref="BackgroundService"/>
/// (event-store-skeleton §5.1: the relay co-hosts in the engine process, no separate worker).
/// Poll loop: drain a batch every <see cref="OutboxRelayOptions.PollInterval"/>; on a produce
/// failure (Redpanda unavailable) treat it as BACKPRESSURE — back off and retry, leaving rows
/// PENDING, NEVER FAILED (ADR-IC-004 §P7).
/// </summary>
public sealed class OutboxRelayService(
    OutboxDrainer drainer,
    OutboxRelayOptions options,
    ILogger<OutboxRelayService>? logger = null) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = options.PollInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await drainer.DrainOnceAsync(stoppingToken);
                backoff = options.PollInterval; // a clean cycle resets the backoff
                // When the drain emptied the tail, wait a poll interval; otherwise loop straight
                // on to drain the next batch while there is backlog.
                if (published == 0)
                {
                    await Task.Delay(options.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // graceful shutdown
            }
            catch (Exception ex)
            {
                // Redpanda unavailability (or a transient DB error) is backpressure: rows stay
                // PENDING, we back off exponentially up to the ceiling, then keep retrying. The
                // publish-lag SLI + alerting is Epic G.1 (this loop just keeps the rows safe).
                logger?.LogWarning(ex, "Outbox drain cycle failed; backing off {Backoff} and retrying (rows stay PENDING).", backoff);
                await Task.Delay(backoff, stoppingToken);
                backoff = backoff < MaxBackoff ? backoff + backoff : MaxBackoff;
            }
        }
    }
}
