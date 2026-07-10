using System.Diagnostics.Metrics;
using Babelstone.Telemetry;

namespace Babelstone.Orchestrator;

/// <summary>
/// The scheduled payout-landing reconciler's operational metrics (bd babelstone-qa92.2; ADR-PC-043) — the
/// observability surface for a safety net whose whole point is to make an INVISIBLE failure visible. In plain
/// terms: <see cref="PayoutLandingReconciler"/> already knows how to spot a payout that dropped, doubled, or
/// landed at the wrong amount, but nothing ran it in production, so its signals reached no one. This host now
/// runs it on a cadence and records two things an operator needs from a dashboard — <i>which discrepancies
/// fired</i> (the per-<c>ReconciliationClass</c> signal counter the <c>payout-landing-reconciliation</c> alert
/// group reads) and <i>is the reconciler still ticking</i> (the tick-liveness heartbeat gauge, the same
/// freshness-plus-<c>absent()</c> health signal the lifecycle driver uses). All instruments live on the shared
/// <c>Babelstone.Engine</c> meter (<see cref="BabelstoneTelemetry.Meter"/>, ADR-IC-007 Layer 1) under the
/// <see cref="BabelstoneAttributes"/> name contract, so the host turns them on with one <c>AddMeter</c> and the
/// alert group reads them by their exact strings.
/// </summary>
/// <remarks>
/// <para>
/// <b>Signal only, never a Movement (ADR-PC-043 reconcile-signals-only).</b> This surface RAISES the fact for
/// a human/operator reconciliation process; it does not — and this host does not — invent or auto-correct a
/// Movement. Counting a Drop is advisory, exactly like the <c>ReconciliationSignal</c> it counts.
/// </para>
/// <para>
/// <b>Emitted from the impure driver shell only (OBS-2).</b> The reconciler classifier is clock-free and pure
/// (ADR-PC-023 §6 — <c>asOf</c> is injected, never read inside it); the wall-clock read for the heartbeat
/// stamp lives HERE, in the clock-owning driver shell, so nothing here touches the engine's replay
/// determinism. Every dimension is the structural <c>reconciliation_class</c> code — never PII (ADR-IC-007 /
/// <c>OBS_NO_PII_ATTRS</c>; the runtime guard's metric View admits exactly that key). With no meter listener
/// attached every record is a near-zero-cost no-op, so tests and un-observed hosts are unaffected.
/// </para>
/// <para>
/// <b>The heartbeat gauge emits nothing until the first completed pass</b> — the alert rules read
/// <c>absent()</c> as its own signal (a reconciler that never completed a pass), the same convention as the
/// lifecycle-driver tick-liveness gauge and the reconciliation drill-freshness gauge.
/// </para>
/// </remarks>
public static class PayoutReconciliationMetrics
{
    private static readonly Counter<long> Signals =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            BabelstoneAttributes.PayoutReconciliationSignalMetric,
            description: "Non-matched payout-landing reconciliation signals surfaced (ADR-PC-043 — Drop/Double/WrongAmount/OrphanLanding; signal only, never a Movement).");

    // Unix-epoch MILLISECONDS of the most recent COMPLETED reconciliation pass (−1 = none yet). A long under
    // Interlocked so the write is atomic on every platform; the observable heartbeat gauge reads it each
    // collection cycle and emits NOTHING until the first completed pass (absent() is the alert's own signal).
    private static long _lastPassSuccessUnixMs = -1;

    // Register the heartbeat gauge once, in the static initializer, on the shared meter — the same pattern as
    // the lifecycle driver's tick-liveness gauge and the engine's ReconciliationMetrics drill-freshness gauge.
    static PayoutReconciliationMetrics() =>
        BabelstoneTelemetry.Meter.CreateObservableGauge(
            BabelstoneAttributes.PayoutReconciliationPassFreshnessMetric,
            observeValues: ObservePassFreshness,
            unit: "s",
            description: "Unix-epoch seconds of the most recent COMPLETED payout-landing reconciliation pass — the safety net's tick-liveness heartbeat.");

    /// <summary>One non-matched signal surfaced: count it, tagged by its reconciliation class.</summary>
    public static void RecordSignal(ReconciliationClass classification) =>
        Signals.Add(1, ClassTag(classification));

    /// <summary>One reconciliation pass ran to completion — refresh the tick-liveness heartbeat.</summary>
    public static void RecordPassCompleted(TimeProvider? clock = null) =>
        Interlocked.Exchange(
            ref _lastPassSuccessUnixMs,
            (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds());

    private static IEnumerable<Measurement<double>> ObservePassFreshness()
    {
        var unixMs = Interlocked.Read(ref _lastPassSuccessUnixMs);
        if (unixMs >= 0)
        {
            yield return new Measurement<double>(unixMs / 1000.0);
        }
    }

    private static KeyValuePair<string, object?> ClassTag(ReconciliationClass classification) =>
        new(BabelstoneAttributes.PayoutReconciliationClassTag, ClassLabel(classification));

    /// <summary>
    /// The snake_case metric-label value for a reconciliation class — the exact string the alert rules match
    /// on (<c>drop</c> / <c>double</c> / <c>wrong_amount</c> / <c>orphan_landing</c> / <c>in_flight</c> /
    /// <c>matched</c>). A closed, structural vocabulary; a switch (not <c>enum.ToString()</c>) so the wire
    /// label is decoupled from the C# member name and cannot drift silently under a rename. Public so a test
    /// (and the alert-rule cross-check) can name the canonical label rather than hard-coding the string.
    /// </summary>
    public static string ClassLabel(ReconciliationClass classification) => classification switch
    {
        ReconciliationClass.Matched => "matched",
        ReconciliationClass.InFlight => "in_flight",
        ReconciliationClass.Drop => "drop",
        ReconciliationClass.Double => "double",
        ReconciliationClass.WrongAmount => "wrong_amount",
        ReconciliationClass.OrphanLanding => "orphan_landing",
        _ => "unknown",
    };
}
