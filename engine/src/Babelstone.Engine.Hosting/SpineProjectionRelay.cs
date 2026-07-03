using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Babelstone.Engine.Hosting;

/// <summary>Tuning for the in-process spine account-projection relay.</summary>
public sealed record SpineProjectionRelayOptions
{
    /// <summary>How long to wait after an empty drain before polling again.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Hosts the <see cref="SpineProjectionDrainer"/> as an in-process <see cref="BackgroundService"/> —
/// the production caller that keeps the movement ledger and the active-hold set fed from the event
/// log (ADR-PC-032 / ADR-PC-033). The same co-hosted poll-loop shape as
/// <see cref="ProjectionRelayService"/> and the outbox relay: a clean empty cycle waits a poll
/// interval, a backlog loops straight on, and a drain failure is backpressure — back off and retry,
/// leaving the checkpoints where they are (both read models are rebuildable and every apply is
/// idempotent, so nothing is lost).
/// </summary>
public sealed class SpineProjectionRelayService(
    SpineProjectionDrainer drainer,
    SpineProjectionRelayOptions options,
    ILogger<SpineProjectionRelayService>? logger = null) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = options.PollInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var folded = await drainer.DrainOnceAsync(stoppingToken);

                backoff = options.PollInterval; // a clean cycle resets the backoff
                if (folded == 0)
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
                logger?.LogWarning(
                    ex,
                    "Spine account-projection drain cycle failed; backing off {Backoff} and retrying "
                    + "(the movement ledger and hold set are rebuildable).",
                    backoff);
                await Task.Delay(backoff, stoppingToken);
                backoff = backoff < MaxBackoff ? backoff + backoff : MaxBackoff;
            }
        }
    }
}
