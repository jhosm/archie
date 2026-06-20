using Babelstone.EventStore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Babelstone.OutboxPublisher;

/// <summary>
/// Hosts the <see cref="DedupRetentionSweeper"/> as an in-process <see cref="BackgroundService"/> —
/// the engine's retention job for its two dedup ledgers (<c>command_dedup</c>, migration 0015, and
/// <c>inbox</c>, migration 0012). It co-hosts in the engine process exactly as the outbox relay does
/// (event-store-skeleton §5.1: no separate worker). Each cycle deletes the aged tail of both ledgers
/// up to a batch cap; while a cycle hits the cap there is still a backlog, so it loops straight on;
/// once a cycle clears under the cap the tail is caught up and it idles for the sweep interval.
/// </summary>
/// <remarks>
/// <para>
/// Lives in this assembly purely because it is the engine-process home that already carries
/// <c>Microsoft.Extensions.Hosting</c> for §5.1 in-process loops; the DELETE logic itself is in
/// <see cref="DedupRetentionSweeper"/> over in <c>Babelstone.EventStore</c>, the single owner of the
/// engine storage tables. The sweep is operational housekeeping, NOT on any command/event hot path —
/// it never touches the append transaction, the dedup pre-check, or a handler.
/// </para>
/// <para>
/// A sweep failure (the DB momentarily unavailable, a transient error) is benign: the rows simply are
/// not pruned this cycle and the NEXT cycle catches them — a deferred delete never loses a receipt or
/// opens a duplicate, so unlike the outbox/inbox loops there is no "rows stay PENDING" hazard to guard.
/// We log, back off exponentially up to a ceiling, and retry. A clean cycle resets the backoff.
/// </para>
/// </remarks>
public sealed class DedupRetentionSweepService(
    DedupRetentionSweeper sweeper,
    DedupRetentionOptions options,
    ILogger<DedupRetentionSweepService>? logger = null) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = options.SweepInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var swept = await sweeper.SweepOnceAsync(stoppingToken);
                backoff = options.SweepInterval; // a clean cycle resets the backoff

                if (swept.Total > 0)
                {
                    logger?.LogInformation(
                        "Dedup retention sweep deleted {CommandDedup} command_dedup + {Inbox} inbox rows.",
                        swept.CommandDedupDeleted, swept.InboxDeleted);
                }

                // A full batch on EITHER ledger means a backlog remains; loop straight on to drain the
                // next batch. Only when both cleared under the cap is the tail caught up, so we idle.
                var backlogRemains =
                    swept.CommandDedupDeleted >= options.BatchSize || swept.InboxDeleted >= options.BatchSize;
                if (!backlogRemains)
                {
                    await Task.Delay(options.SweepInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // graceful shutdown
            }
            catch (Exception ex)
            {
                // A transient sweep failure is benign — the aged rows are simply pruned next cycle, never
                // lost — so back off and retry. No receipt is at risk: a deferred delete cannot open a
                // duplicate or drop an idempotency receipt (the ledgers are only ADDED to off this path).
                logger?.LogWarning(ex, "Dedup retention sweep cycle failed; backing off {Backoff} and retrying.", backoff);
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
