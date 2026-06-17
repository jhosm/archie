using System.Diagnostics;
using Babelstone.Telemetry;

namespace Babelstone.LoadHarness;

/// <summary>
/// The §P1 OBSERVER: reads boundary-to-commit latency from the engine's OWN OpenTelemetry spans and
/// evaluates the §8.3 thresholds. Per §P2 / §G2 the latency is the engine's span DURATION
/// (event-receipt-at-boundary → projection-committed), NEVER the driver's publish-confirm clock — for
/// an async projection there is no synchronous response to time.
/// </summary>
/// <remarks>
/// <para>
/// In plain English: to know how long the engine took to update a projection, this listens to the
/// engine's own telemetry (the spans it already emits in production) rather than guessing from when the
/// test sent the event. That is the §8.4 promise — no test-only instrumentation that disappears at
/// production cutover; the test reads the same signal that diagnoses production.
/// </para>
/// <para>
/// The <see cref="ActivityListener"/> on <c>BabelstoneTelemetry.ActivitySourceName</c> captures spans
/// exactly as a real OTel tracer provider's <c>AddSource(...)</c> would (the same pattern the engine's
/// own span fitness test uses). At full v4 scale the spans flow to Grafana LGTM (ADR-IC-007) and the
/// observer reads the p50/p95/p99 from there; this in-process listener is the same surface, read
/// locally, so the smoke test needs no LGTM stack.
/// </para>
/// </remarks>
public sealed class LatencyObserver : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _captured = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// Starts listening to the engine's <c>ActivitySource</c>. Any span the engine emits while this
    /// observer is alive is captured for §8.3 evaluation. Construct it BEFORE driving the workload.
    /// </summary>
    public LatencyObserver()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BabelstoneTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = OnStopped,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    private void OnStopped(Activity activity)
    {
        lock (_gate)
        {
            _captured.Add(activity);
        }
    }

    /// <summary>Every span captured so far (a snapshot copy).</summary>
    public IReadOnlyList<Activity> CapturedSpans
    {
        get
        {
            lock (_gate)
            {
                return _captured.ToArray();
            }
        }
    }

    /// <summary>
    /// The p50/p95/p99 latency for one span operation name (e.g. <c>BabelstoneAttributes.SpanAccrualComputed</c>),
    /// computed from the captured spans' DURATIONS — the §P2 boundary-to-commit quantity. Returns null
    /// if no span of that name was captured (the caller treats "no data" as a distinct outcome from a
    /// breached threshold).
    /// </summary>
    public LatencyPercentiles? PercentilesFor(string operationName)
    {
        ArgumentException.ThrowIfNullOrEmpty(operationName);

        double[] durationsMs;
        lock (_gate)
        {
            durationsMs = _captured
                .Where(a => a.OperationName == operationName)
                .Select(a => a.Duration.TotalMilliseconds)
                .OrderBy(d => d)
                .ToArray();
        }

        if (durationsMs.Length == 0)
        {
            return null;
        }

        return new LatencyPercentiles(
            Count: durationsMs.Length,
            P50Ms: Percentile(durationsMs, 0.50),
            P95Ms: Percentile(durationsMs, 0.95),
            P99Ms: Percentile(durationsMs, 0.99));
    }

    /// <summary>
    /// Evaluates one §8.3 sync-latency band against the captured spans: a pass requires data AND every
    /// percentile under its budget. "No data" is a distinct, explicit failure (a sync projection that
    /// never emitted a span did not run — that is a test failure, not a vacuous pass).
    /// </summary>
    public LatencyVerdict Evaluate(SyncLatencyBand band)
    {
        ArgumentNullException.ThrowIfNull(band);
        var p = PercentilesFor(band.SpanName);
        if (p is null)
        {
            return new LatencyVerdict(band, Observed: null, Passed: false, Reason: "no spans captured for this band");
        }

        var passed = p.P50Ms < band.P50BudgetMs && p.P95Ms < band.P95BudgetMs && p.P99Ms < band.P99BudgetMs;
        var reason = passed
            ? "within budget"
            : $"breach: p50={p.P50Ms:F1}/{band.P50BudgetMs} p95={p.P95Ms:F1}/{band.P95BudgetMs} p99={p.P99Ms:F1}/{band.P99BudgetMs} (ms)";
        return new LatencyVerdict(band, p, passed, reason);
    }

    // Nearest-rank percentile (deterministic, no interpolation) over an ascending array.
    internal static double Percentile(double[] ascending, double q)
    {
        if (ascending.Length == 1)
        {
            return ascending[0];
        }

        var rank = (int)Math.Ceiling(q * ascending.Length);
        var index = Math.Clamp(rank - 1, 0, ascending.Length - 1);
        return ascending[index];
    }

    public void Dispose() => _listener.Dispose();
}

/// <summary>The p50/p95/p99 (and sample count) of an engine span's boundary-to-commit duration, in ms.</summary>
public sealed record LatencyPercentiles(int Count, double P50Ms, double P95Ms, double P99Ms);

/// <summary>
/// One §8.3 synchronous-latency band: the engine span whose duration IS the boundary-to-commit latency,
/// and the p50/p95/p99 budgets it must stay under (e.g. current_balance: 20/80/200 ms).
/// </summary>
/// <param name="ProjectionClass">The §8.2 projection name (e.g. "current_balance").</param>
/// <param name="SpanName">The engine span operation name whose duration is the latency (e.g.
/// <c>BabelstoneAttributes.SpanAccrualComputed</c>).</param>
public sealed record SyncLatencyBand(
    string ProjectionClass, string SpanName, double P50BudgetMs, double P95BudgetMs, double P99BudgetMs)
{
    /// <summary>
    /// The §8.3 sync-latency bands, bound to the engine spans available today. The engine emits
    /// <c>accrual.computed</c> / <c>withholding.applied</c> product-semantic spans (ADR-IC-007 / the
    /// engine span fitness test); as the sync-projection spans (<c>current_balance</c>,
    /// <c>available_credit</c>, <c>hold_freeze_ledger</c>) are instrumented they bind here by name with
    /// no change to the observer. The budgets are the §8.3 table verbatim.
    /// </summary>
    public static IReadOnlyList<SyncLatencyBand> Section83Bands() =>
    [
        // §8.3: current_balance / available_credit — p50 < 20 ms, p95 < 80 ms, p99 < 200 ms.
        new SyncLatencyBand("current_balance", BabelstoneAttributes.SpanAccrualComputed, 20, 80, 200),
        // §8.3: hold_freeze_ledger — p50 < 30 ms, p95 < 100 ms, p99 < 250 ms (looser, less write contention).
        new SyncLatencyBand("hold_freeze_ledger", BabelstoneAttributes.SpanWithholdingApplied, 30, 100, 250),
    ];
}

/// <summary>The outcome of evaluating one §8.3 band: what was observed and whether it passed (with why).</summary>
public sealed record LatencyVerdict(SyncLatencyBand Band, LatencyPercentiles? Observed, bool Passed, string Reason);
