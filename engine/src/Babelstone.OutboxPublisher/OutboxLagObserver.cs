using System.Diagnostics.Metrics;
using Babelstone.Telemetry;
using Npgsql;

namespace Babelstone.OutboxPublisher;

/// <summary>
/// The publish-lag SLI (ADR-IC-004 / ADR-IC-007 Layer 1): an <see cref="ObservableGauge{T}"/>
/// named <c>outbox_publish_lag_seconds</c> on the shared Babelstone meter, carrying the age in
/// seconds of the OLDEST <c>PENDING</c> outbox row at each metrics-collection cycle.
/// </summary>
/// <remarks>
/// <para>
/// ADR-IC-004 mandates a GAUGE of "the age in seconds of the oldest PENDING row at the time of the poll",
/// alarmed Warning &gt;30s and Critical &gt;5min — the Critical case being "the publisher is not
/// running or Redpanda is unavailable". A per-published-row latency histogram cannot serve those
/// alerts: in exactly those failure modes NO row publishes, so a per-row instrument goes silent
/// precisely when the alert must fire. This observable gauge instead reads the backlog directly each
/// collection cycle, so it keeps climbing as the oldest row ages regardless of publish activity.
/// </para>
/// <para>
/// The value is <c>EXTRACT(EPOCH FROM clock_timestamp() - MIN(created_at))</c> over PENDING rows,
/// computed entirely in the DB — single-clock by construction, so no host/DB clock skew can bias it
/// (0 when the backlog is empty). The query is bounded by the ADR-IC-004 partial index on
/// <c>(created_at, sequence_number) WHERE status = 'PENDING'</c>.
/// </para>
/// <para>
/// Register ONE observer per process (the host that runs the relay). The OTel metrics collection
/// cycle drives the callback; the callback runs a short synchronous query on its own connection so
/// it does not contend with the drain transaction's row locks. With no listener attached the gauge
/// is never collected and the query never runs.
/// </para>
/// </remarks>
public sealed class OutboxLagObserver : IDisposable
{
    // clock_timestamp() (not now()/transaction_timestamp()) is the wall clock at statement time, the
    // right "time of the poll" reference; both ends of the subtraction are the DB clock. COALESCE
    // maps an empty backlog (MIN over no rows is NULL) to 0 — no PENDING rows means zero lag.
    private const string OldestPendingLagSql = """
        SELECT COALESCE(EXTRACT(EPOCH FROM clock_timestamp() - MIN(created_at)), 0)
        FROM outbox
        WHERE status = 'PENDING';
        """;

    private readonly string _connectionString;

    // The observer owns its OWN Meter (named the shared BabelstoneTelemetry.MeterName scope so a
    // host's AddMeter(MeterName) still picks the gauge up). Observable instruments are not themselves
    // IDisposable — their lifetime is the meter's — so owning a meter is what lets the observer be
    // disposed (removing the gauge) without tearing down the process-wide static meter. Register at
    // most ONE observer per process.
    private readonly Meter _meter;

    public OutboxLagObserver(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _meter = new Meter(BabelstoneTelemetry.MeterName);
        _meter.CreateObservableGauge(
            BabelstoneAttributes.OutboxPublishLagMetric,
            observeValue: MeasureOldestPendingLagSeconds,
            unit: "s",
            description: "Age in seconds of the oldest PENDING outbox row (the §P4 publish-lag SLI).");
    }

    /// <summary>
    /// The gauge callback: the oldest-PENDING-row age in seconds, read fresh from the DB. Synchronous
    /// because OTel observable-instrument callbacks are. A transient DB error propagates to the OTel
    /// collection cycle, which catches and logs it and skips this measurement (a stale series the
    /// alerting rule reads as its own signal) — it does not tear down the pipeline. The DB being
    /// reachable is a precondition for the relay anyway (it polls the same table).
    /// </summary>
    private Measurement<double> MeasureOldestPendingLagSeconds()
        => new(QueryOldestPendingLagSeconds());

    private double QueryOldestPendingLagSeconds()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(OldestPendingLagSql, connection);
        return Convert.ToDouble(command.ExecuteScalar());
    }

    public void Dispose() => _meter.Dispose();
}
