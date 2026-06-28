using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Babelstone.InboxConsumer;

/// <summary>
/// Hosts the <see cref="InboxPump"/> as an in-process <see cref="BackgroundService"/> — the consumer
/// mirror of <c>OutboxRelayService</c> (event-store-skeleton §5.1: the consumer co-hosts in the
/// process, no separate worker). The loop calls <see cref="InboxPump.PumpOnceAsync"/> repeatedly; the
/// pump itself blocks up to the consume timeout, so an idle topic is a tight, cheap spin rather than
/// a busy one.
/// </summary>
/// <remarks>
/// A handler EXCEPTION (a transient failure: the consumer DB momentarily unavailable, a downstream
/// hiccup) leaves the DB transaction rolled back and the offset UNCOMMITTED, so the record is
/// redelivered. This loop treats it as backpressure — log, back off exponentially up to a ceiling,
/// then retry — exactly as the outbox relay treats a Redpanda outage (rows stay PENDING, never
/// FAILED — ADR-IC-004). It never advances past a record whose effect did not commit; the inbox
/// is the consumer's source of truth that the message was processed, and a transient failure must
/// not be mistaken for a processed message. Poison records are handled INSIDE the pump (skipped past),
/// so they do not trip this backoff.
/// </remarks>
public sealed class InboxConsumerService(
    InboxPump pump,
    ILogger<InboxConsumerService>? logger = null) : BackgroundService
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = InitialBackoff;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await pump.PumpOnceAsync(stoppingToken);
                backoff = InitialBackoff; // a clean cycle resets the backoff
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // graceful shutdown
            }
            catch (Exception ex)
            {
                // A transient handler/DB error rolled the transaction back and left the offset
                // uncommitted: the record will be redelivered. Back off and retry — never skip it.
                logger?.LogWarning(
                    ex, "Inbox pump cycle failed; backing off {Backoff} and retrying (offset NOT committed).", backoff);
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
