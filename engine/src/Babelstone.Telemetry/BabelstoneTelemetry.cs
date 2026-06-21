using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Babelstone.Telemetry;

/// <summary>
/// The single <see cref="ActivitySource"/> every Babelstone .NET host and the runtime shell
/// open manual spans on, plus the single <see cref="Meter"/> they record metrics on
/// (ADR-IC-007 Layer 1). A process turns these on by registering the names with its OTel
/// provider (<c>AddSource(BabelstoneTelemetry.ActivitySourceName)</c> /
/// <c>AddMeter(BabelstoneTelemetry.MeterName)</c>); when no listener is attached (the common
/// test/library path) <see cref="ActivitySource.StartActivity(string,ActivityKind)"/> returns
/// <c>null</c> and an instrument's <c>Record</c> is a near-zero-cost no-op. The source/meter
/// names double as the OTel <c>instrumentation.scope</c>, so they are stable, versioned identifiers.
/// </summary>
public static class BabelstoneTelemetry
{
    /// <summary>The instrumentation scope / activity-source name. Stable — hosts register it by this exact string.</summary>
    public const string ActivitySourceName = "Babelstone.Engine";

    /// <summary>The instrumentation scope / meter name. Stable — hosts register it via <c>AddMeter</c> by this exact string.</summary>
    public const string MeterName = "Babelstone.Engine";

    /// <summary>The process-wide source manual spans (e.g. <c>accrual.computed</c>) are started on.</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    /// <summary>The process-wide meter instruments (e.g. the outbox publish-lag SLI) are created on.</summary>
    public static readonly Meter Meter = new(MeterName);
}

/// <summary>
/// The two snapshotter operational signals ADR-PC-003 §P6 calls for, emitted by the runtime / snapshot
/// store so the <c>snapshot-operations</c> alert group can go live (bd babelstone-sk7e):
/// <list type="bullet">
///   <item><b>snapshot lag</b> — an observable gauge of the largest un-snapshotted event count observed
///   across streams (§P6 (1)). Gauge-shaped because the <c>SnapshotLagHigh</c> alert reads it
///   instantaneously (<c>&gt; 500</c>); a cumulative counter could never describe "how far behind RIGHT
///   NOW". <see cref="RecordLag"/> raises a per-process high-water mark from the post-commit snapshot
///   path; the gauge reports it each collection cycle.</item>
///   <item><b>hash-mismatch on read</b> — a monotonic counter incremented where
///   <c>SnapshotStore.Verify</c> rejects a snapshot whose stored hash did not verify (§P6 (2) / §8.3).
///   <see cref="RecordHashMismatch"/> is called from the verify guard.</item>
/// </list>
/// These are pure RUNTIME/STORE emissions (ADR-PC-010 §P5): they never touch a pure handler, the
/// replayed fold, or rebuilt state — emitting a snapshot metric cannot change what an event folds to, so
/// replay determinism is unaffected. With no meter listener attached, an instrument's record/observe is a
/// near-zero-cost no-op. The instruments live on the shared <see cref="BabelstoneTelemetry.Meter"/> so a
/// host turns them on with one <c>AddMeter(BabelstoneTelemetry.MeterName)</c>.
/// </summary>
public static class SnapshotMetrics
{
    // The largest un-snapshotted event count observed since process start (the §P6 (1) lag high-water
    // mark). A long behind an interlocked guard: the post-commit snapshot path raises it, the observable
    // gauge reads it. It only ever rises within a process — a deep stream that snapshots does not "undo"
    // the fact the snapshotter was once that far behind; an operator's signal is the PEAK lag, and a
    // process restart re-establishes the baseline from live appends.
    private static long _maxLagEvents;

    private static readonly System.Diagnostics.Metrics.Counter<long> HashMismatch =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.SnapshotHashMismatchMetric,
            description: "Snapshot (state ‖ last_event_id) hash-verification failures on read (ADR-PC-003 §P6 (2)/§8.3 — a wrong snapshot rejected, the read fell back to a cold fold).");

    // Register the lag gauge once. Observable instruments are collected by the OTel cycle (or a test's
    // RecordObservableInstruments()); it reports the current high-water mark, starting at 0 (a healthy
    // snapshotter that has never fallen behind reads 0, well under the >500 warning threshold).
    static SnapshotMetrics() =>
        BabelstoneTelemetry.Meter.CreateObservableGauge(
            BabelstoneAttributes.SnapshotLagEventsMetric,
            observeValue: ObserveMaxLag,
            description: "Largest un-snapshotted event count observed across streams (ADR-PC-003 §P6 (1) snapshotter-health SLI; per-N default is 100, so >500 means ~5 thresholds behind).");

    /// <summary>
    /// Raise the lag high-water mark to <paramref name="eventsSinceSnapshot"/> if it exceeds the current
    /// peak. Called from the post-commit snapshot path with the un-snapshotted depth it just computed for
    /// the per-N trigger — so the gauge tracks how far behind the snapshotter has been seen to fall,
    /// whether or not THIS append snapshotted. Negative/zero depths are ignored (nothing to report).
    /// </summary>
    public static void RecordLag(long eventsSinceSnapshot)
    {
        if (eventsSinceSnapshot <= 0)
        {
            return;
        }

        // Lock-free monotone-max: CAS the peak upward, retrying only if a concurrent writer moved it.
        long current;
        do
        {
            current = System.Threading.Interlocked.Read(ref _maxLagEvents);
            if (eventsSinceSnapshot <= current)
            {
                return;
            }
        }
        while (System.Threading.Interlocked.CompareExchange(ref _maxLagEvents, eventsSinceSnapshot, current) != current);
    }

    /// <summary>Increment the §P6 (2) hash-mismatch counter — called where <c>SnapshotStore.Verify</c> rejects a snapshot.</summary>
    public static void RecordHashMismatch() => HashMismatch.Add(1);

    private static long ObserveMaxLag() => System.Threading.Interlocked.Read(ref _maxLagEvents);
}
