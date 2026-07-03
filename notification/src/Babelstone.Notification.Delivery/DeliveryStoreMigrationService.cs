using Babelstone.Notification.Delivery.Migrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Babelstone.Notification.Delivery;

/// <summary>
/// Applies the delivery estate's forward-only migration series at host start (ADR-IC-011; the same
/// boot shape as the lifecycle driver's ledger migration service). A plain <see cref="IHostedService"/>
/// whose <see cref="StartAsync"/> AWAITS the runner: under the host's default sequential
/// hosted-service startup, registration order is start order, so the delivery/relay workers
/// registered after this service start against a migrated store (a host opting into concurrent
/// startup forfeits that ordering and leans on the workers' failed-tick backoff instead).
/// Fail-loud: an unreachable or unmigratable database aborts host start — a delivery host that cannot
/// remember what it owes must not pretend to run (the exact amnesia this store exists to end).
/// </summary>
public sealed class DeliveryStoreMigrationService(
    string migrationConnectionString,
    ILogger<DeliveryStoreMigrationService>? logger = null) : IHostedService
{
    private readonly string _migrationConnectionString =
        string.IsNullOrWhiteSpace(migrationConnectionString)
            ? throw new ArgumentException("A migration connection string is required.", nameof(migrationConnectionString))
            : migrationConnectionString;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var applied = await new MigrationRunner(_migrationConnectionString).ApplyAsync(cancellationToken);
        if (applied.Count > 0)
        {
            logger?.LogInformation(
                "Notification delivery store migrated: applied {Count} migration(s), now at version {Version}.",
                applied.Count, applied[^1].Version);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Logs ONE composition-time warning at host start — the seam that lets
/// <c>AddNotificationWebhookDelivery</c> (which runs before any logger exists) surface a
/// configured-but-degraded mode loudly instead of silently: today, a durable delivery store with no
/// backbone to drain its exhaustion announcements to (ADR-IC-011). Deliberately a hosted service, not
/// a boot-time throw — the mode is legitimate in dev, so it must warn, not refuse.
/// </summary>
public sealed class StartupWarningService(
    ILogger<StartupWarningService>? logger,
    string message) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger?.LogWarning("{Message}", message);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
