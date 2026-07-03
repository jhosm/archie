using System.Diagnostics.Metrics;
using Babelstone.Telemetry;

namespace Babelstone.Lifecycle;

/// <summary>
/// The lifecycle driver's operational metrics (bd babelstone-1nkm.4) — the observability surface for the
/// always-on money-mover whose worst failures are silent by construction. In plain terms: this host moves
/// money on a schedule with no human watching a screen, so an operator needs three answers from a
/// dashboard at 2am — <i>is it ticking</i> (the <c>lifecycle_pass_last_success_timestamp_seconds</c>
/// heartbeat gauge, the host's liveness/health signal), <i>is it landing its POSTs</i> (the
/// dispatch/failure counters), and <i>how late is it firing</i> (the dispatch-lag histogram) — plus the
/// page that turns the settlement-health stall (a parked cash leg silently holding a whole schedule,
/// LCD-2) into an alert instead of an invisible miss. All instruments live on the shared
/// <c>Babelstone.Engine</c> meter (<see cref="BabelstoneTelemetry.Meter"/>, ADR-IC-007 Layer 1) under the
/// <see cref="BabelstoneAttributes"/> name contract, so the host turns them on with one <c>AddMeter</c>
/// and the <c>lifecycle-driver</c> alert group reads them by their exact strings.
/// </summary>
/// <remarks>
/// <para>
/// <b>Emitted from the impure shell only (OBS-2).</b> The schedule pass and its rules are I/O-bearing
/// driver-shell components — never an engine fold or pure decider — so recording here cannot touch replay
/// determinism; the engine stays clockless regardless (the wall clock read for the heartbeat/lag stamps
/// lives HERE, in the clock-owning driver host, ADR-PC-023 §6). Every dimension is the structural
/// <c>command_kind</c> code — never PII (ADR-IC-007 / <c>OBS_NO_PII_ATTRS</c>; the runtime guard's metric
/// View admits exactly that key). With no meter listener attached every record is a near-zero-cost no-op,
/// so tests and un-observed hosts are unaffected.
/// </para>
/// <para>
/// <b>The heartbeat gauge emits nothing until the first completed pass</b> — the alert rules read
/// <c>absent()</c> as its own signal (a driver that never completed a pass), the same convention as the
/// reconciliation drill-freshness gauge and the <c>EngineMetricsAbsent</c> staging-liveness rule.
/// <see cref="RecordScheduleHeld"/> is the LCD-2 settlement-health gate's emit hook: shipped with the
/// monitoring surface (this issue), CALLED by the gate when it lands (bd babelstone-6cpq.10) — until
/// then the series is absent and its page is dormant by construction.
/// </para>
/// </remarks>
public static class LifecycleDriverMetrics
{
    private static readonly Counter<long> Dispatched =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.LifecycleDispatchedMetric,
            description: "Due lifecycle occurrences POSTed to the engine and durably recorded (ADR-PC-038 claim->POST->record).");

    private static readonly Counter<long> DispatchFailed =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.LifecycleDispatchFailureMetric,
            description: "Claimed occurrences whose engine POST failed — released un-recorded for the next pass to retry (command_dedup keeps the retry effectively-once).");

    private static readonly Histogram<double> DispatchLag =
        BabelstoneTelemetry.Meter.CreateHistogram<double>(
            BabelstoneAttributes.LifecycleDispatchLagMetric,
            unit: "s",
            description: "Seconds between an occurrence's business due date (UTC midnight) and its successful dispatch.");

    private static readonly Counter<long> ScheduleHeld =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.LifecycleScheduleHeldMetric,
            description: "Recurring occurrences HELD by the settlement-health gate because the prior occurrence's cash leg is parked (LCD-2 — alerted, never invisible).");

    // Unix-epoch MILLISECONDS of the most recent COMPLETED schedule pass (−1 = none yet). A long under
    // Interlocked so the write is atomic on every platform; the observable heartbeat gauge reads it each
    // collection cycle and emits NOTHING until the first completed pass (absent() is the alert's own
    // signal).
    private static long _lastPassSuccessUnixMs = -1;

    // Register the heartbeat gauge once, in the static initializer, on the shared meter — the same
    // pattern as the engine's ReconciliationMetrics drill-freshness gauge.
    static LifecycleDriverMetrics() =>
        BabelstoneTelemetry.Meter.CreateObservableGauge(
            BabelstoneAttributes.LifecyclePassFreshnessMetric,
            observeValues: ObservePassFreshness,
            unit: "s",
            description: "Unix-epoch seconds of the most recent COMPLETED lifecycle schedule pass — the always-on driver's tick-liveness heartbeat.");

    /// <summary>One occurrence successfully POSTed AND durably recorded: count it and record its
    /// dispatch lag (now − the due date's UTC midnight, clamped at zero for an early-window fire).</summary>
    public static void RecordDispatched(string commandKind, DateOnly dueAt, TimeProvider? clock = null)
    {
        var tag = KindTag(commandKind);
        Dispatched.Add(1, tag);

        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var due = new DateTimeOffset(dueAt.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        DispatchLag.Record(Math.Max(0d, (now - due).TotalSeconds), tag);
    }

    /// <summary>One claimed occurrence's POST failed (backpressure — released for retry).</summary>
    public static void RecordDispatchFailed(string commandKind) =>
        DispatchFailed.Add(1, KindTag(commandKind));

    /// <summary>One schedule pass ran to completion — refresh the tick-liveness heartbeat.</summary>
    public static void RecordPassCompleted(TimeProvider? clock = null) =>
        Interlocked.Exchange(
            ref _lastPassSuccessUnixMs,
            (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds());

    /// <summary>
    /// The LCD-2 settlement-health gate's emit hook (ADR-PC-036 §Decision 4): a recurring rule that HOLDS
    /// occurrence N+1 because occurrence N's cash leg is parked calls this once per held occurrence per
    /// pass, so the silent schedule stall becomes the <c>LifecycleScheduleHeld</c> page. Shipped here with
    /// the monitoring surface; wired by the gate build (bd babelstone-6cpq.10).
    /// </summary>
    public static void RecordScheduleHeld(string commandKind) =>
        ScheduleHeld.Add(1, KindTag(commandKind));

    private static IEnumerable<Measurement<double>> ObservePassFreshness()
    {
        var unixMs = Interlocked.Read(ref _lastPassSuccessUnixMs);
        if (unixMs >= 0)
        {
            yield return new Measurement<double>(unixMs / 1000.0);
        }
    }

    private static KeyValuePair<string, object?> KindTag(string commandKind) =>
        new(BabelstoneAttributes.LifecycleCommandKindTag, commandKind);
}
