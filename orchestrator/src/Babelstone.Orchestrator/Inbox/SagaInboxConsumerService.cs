using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Babelstone.Orchestrator.Inbox;

/// <summary>
/// Hosts the <see cref="SagaConsumeLoop"/> as an in-process <see cref="BackgroundService"/> — the
/// orchestrator analogue of the engine's <c>InboxConsumerService</c>, co-hosted in the saga worker
/// process (ADR-IC-003 §S2 "a Redpanda consumer like every other service", not a separate worker).
/// The loop calls <see cref="SagaConsumeLoop.ConsumeOnceAsync"/> repeatedly; the loop itself blocks up
/// to the consume timeout, so an idle topic is a tight, cheap spin rather than a busy one.
/// </summary>
/// <remarks>
/// A transient EXCEPTION (the orchestrator DB momentarily unavailable, an optimistic-concurrency loss,
/// a downstream hiccup) leaves the DB transaction rolled back and the offset UNCOMMITTED — the loop
/// seeks back, so the record is redelivered. This service treats it as backpressure: log, back off
/// exponentially up to a ceiling, then retry — never advancing past a record whose advance did not
/// commit (at-least-once delivery, effectively-once advance, ADR-IC-003 §P1). Poison records (an
/// illegal transition, a record with no <c>ce_id</c>/<c>ce_type</c>) are handled INSIDE the loop
/// (skipped past), so they never trip this backoff.
/// </remarks>
public sealed class SagaInboxConsumerService(
    SagaConsumeLoop loop,
    ILogger<SagaInboxConsumerService>? logger = null) : BackgroundService
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly SagaConsumeLoop _loop = loop ?? throw new ArgumentNullException(nameof(loop));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = InitialBackoff;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _loop.ConsumeOnceAsync(stoppingToken);
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
                    ex, "Saga consume cycle failed; backing off {Backoff} and retrying (offset NOT committed).", backoff);
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
