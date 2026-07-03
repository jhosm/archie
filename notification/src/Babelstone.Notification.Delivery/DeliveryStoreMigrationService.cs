using Babelstone.Notification.Delivery.Migrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Babelstone.Notification.Delivery;

/// <summary>
/// Applies the delivery estate's forward-only migration series at host start (ADR-IC-011 §P3; the same
/// boot shape as the lifecycle driver's ledger migration service). A plain <see cref="IHostedService"/>
/// whose <see cref="StartAsync"/> AWAITS the runner: hosted services start in registration order, so
/// every delivery/relay worker registered AFTER this service only starts against a migrated store.
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
