using System.Diagnostics.Metrics;
using Babelstone.Telemetry;
using Npgsql;

namespace Babelstone.Notification.Delivery;

/// <summary>
/// The delivery estate's operational metrics (ADR-IC-007 Layer 1) — the aggregable face of what the
/// drain pass and the exhaustion relay already log line-by-line. Two counters on the shared
/// <see cref="BabelstoneTelemetry.Meter"/> under the <see cref="BabelstoneAttributes"/> name contract:
/// attempt outcomes (tagged by <see cref="BabelstoneAttributes.NotificationDeliveryOutcomeTag"/>) and
/// broker-acked exhaustion announcements. A host turns them on with one <c>AddMeter</c>; with no
/// listener attached every record is a near-zero-cost no-op. Every dimension is a closed structural
/// vocabulary — never PII (ADR-PC-004 / OBS_NO_PII_ATTRS).
/// </summary>
public static class NotificationDeliveryMetrics
{
    /// <summary>The closed outcome vocabulary (the delivery pass's ADR-IC-011 classification).</summary>
    public const string OutcomeDelivered = "delivered";
    public const string OutcomeTransientRetry = "transient_retry";
    public const string OutcomeAbandoned = "abandoned";
    public const string OutcomeDeadLettered = "dead_lettered";

    private static readonly Counter<long> Deliveries =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.NotificationDeliveriesMetric,
            description: "Webhook delivery attempts by classified outcome (ADR-IC-011): delivered | transient_retry | abandoned | dead_lettered.");

    private static readonly Counter<long> ExhaustedPublished =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.NotificationExhaustedPublishedMetric,
            description: "NotificationDeliveryExhausted events published to the backbone (broker-acked relay publishes, ADR-IC-011).");

    /// <summary>One delivery attempt classified — count it under its outcome tag.</summary>
    public static void RecordOutcome(string outcome) =>
        Deliveries.Add(1, new KeyValuePair<string, object?>(
            BabelstoneAttributes.NotificationDeliveryOutcomeTag, outcome));

    /// <summary>One exhaustion announcement acked by the broker.</summary>
    public static void RecordExhaustedPublished() => ExhaustedPublished.Add(1);
}

/// <summary>
/// The exhausted-announcement backlog-age gauge
/// (<see cref="BabelstoneAttributes.NotificationExhaustedPendingLagMetric"/>) — the notification
/// estate's mirror of the engine's <c>OutboxLagObserver</c> (ADR-IC-004 posture): the age in seconds
/// of the oldest <c>PENDING</c> <c>notification_delivery_exhausted</c> row, read fresh from the DB at
/// each metrics-collection cycle. A per-publish counter goes silent exactly when the relay is wedged
/// or no backbone is configured — this gauge keeps climbing then, which is what makes silently
/// accumulating unannounced dead-letters alertable. Register ONE observer per process, alongside the
/// durable store.
/// </summary>
public sealed class ExhaustedPendingLagObserver : IDisposable
{
    // clock_timestamp() on both ends — single-clock in the DB, no host/DB skew; COALESCE maps an
    // empty backlog to 0. Bounded by the partial index on (exhausted_at) WHERE status = 'PENDING'.
    private const string OldestPendingLagSql = """
        SELECT COALESCE(EXTRACT(EPOCH FROM clock_timestamp() - MIN(exhausted_at)), 0)
        FROM notification_delivery_exhausted
        WHERE status = 'PENDING';
        """;

    private readonly string _connectionString;

    // The observer owns its OWN Meter under the shared name (a host's AddMeter still picks it up), so
    // disposing the observer removes the gauge without tearing down the process-wide static meter —
    // the same ownership shape as the engine's OutboxLagObserver.
    private readonly Meter _meter;

    public ExhaustedPendingLagObserver(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _meter = new Meter(BabelstoneTelemetry.MeterName);
        _meter.CreateObservableGauge(
            BabelstoneAttributes.NotificationExhaustedPendingLagMetric,
            observeValue: MeasureOldestPendingLagSeconds,
            unit: "s",
            description: "Age in seconds of the oldest PENDING NotificationDeliveryExhausted outbox row.");
    }

    /// <summary>Synchronous because OTel observable-instrument callbacks are; a transient DB error
    /// propagates to the collection cycle, which logs and skips the measurement (a stale series is
    /// its own alert signal) without tearing the pipeline down.</summary>
    private Measurement<double> MeasureOldestPendingLagSeconds()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(OldestPendingLagSql, connection);
        return new(Convert.ToDouble(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Dispose() => _meter.Dispose();
}
