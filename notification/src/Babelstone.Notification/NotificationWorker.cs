using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Babelstone.Notification;

/// <summary>
/// The notification service's host shell — a <see cref="BackgroundService"/> poll-loop worker, the
/// same hosted-<c>BackgroundService</c> shape the engine's outbox relay and the orchestrator's
/// consume loop use (ADR-IC-011 runtime; the ADR-IC-004 per-service outbox-worker pattern). It is
/// the standing process the maturity scheduler (bd babelstone-60n8.2) and the
/// <c>NotificationDue</c> emission (bd babelstone-60n8.3) will later run inside.
/// </summary>
/// <remarks>
/// <para>
/// <b>Skeleton only.</b> This worker stands up the host and proves the
/// <see cref="DepositReadClient"/> read access is wired — it does NOT yet schedule
/// anything on a clock or emit any event. There is deliberately no timer cadence and no outbox
/// write: adding either is the explicit scope of the downstream children, and keeping them out
/// here is what the babelstone-60n8.1 acceptance criteria require ("no timing/scheduling logic and
/// no event emission"). The loop simply idles until the host stops, so the process is a real,
/// long-running service the deployment can run, scale, and observe.
/// </para>
/// <para>
/// The read client is injected (not constructed here) so the timing child can drive it without
/// touching the host shell, and so a test can substitute a fake HTTP transport. No clock reads, no
/// emission, no I/O beyond the idle wait.
/// </para>
/// </remarks>
public sealed class NotificationWorker(
    DepositReadClient depositReadClient,
    ILogger<NotificationWorker> logger) : BackgroundService
{
    // Held so the timing/emission children resolve the read window through the host, and so the DI
    // composition fails loud at startup if the client was not registered. Not exercised on a clock
    // here — this skeleton introduces no scheduling (bd babelstone-60n8.1 acceptance criteria).
    private readonly DepositReadClient _depositReadClient =
        depositReadClient ?? throw new ArgumentNullException(nameof(depositReadClient));

    private readonly ILogger<NotificationWorker> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Notification worker started (ADR-IC-011). Deposit read access wired over the " +
            "ADR-PC-027 contract; no scheduler timing or emission in this skeleton.");

        // No scheduling cadence yet (bd babelstone-60n8.2 adds the idempotent timing loop). Idle
        // until the host stops; Task.Delay(Infinite) parks the loop without a busy-wait and without
        // reading the clock. The cancellation on shutdown is expected, not an error.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown — the host signalled stop.
        }
    }
}
