namespace Babelstone.LoadHarness;

/// <summary>
/// The §8.3 throughput outcome: the achieved aggregate TPS over a sustained/burst drive window vs the
/// target, with a tolerance band (a wall-clock-paced producer never hits the target to the decimal).
/// Folded into <see cref="RunArtefact"/> by the L.3b sustained loop (bd babelstone-2e6q.2) and the L.3c
/// burst profile (bd babelstone-2e6q.3).
/// </summary>
/// <remarks>
/// In plain English: did the harness actually hold the traffic rate it promised? §8.3 requires 250 TPS
/// sustained for 24h and 1000 TPS burst for 15 min. This records the rate we genuinely achieved and
/// whether it landed within tolerance of the target — a producer that silently fell to 40 TPS would
/// "pass" the latency bands vacuously, so the throughput verdict is what stops that false green.
/// </remarks>
/// <param name="Profile">A human label for the phase (e.g. "sustained" / "burst").</param>
/// <param name="TargetTps">The §8.3 target rate this phase aimed to hold.</param>
/// <param name="AchievedTps">The measured rate: events produced / wall-clock seconds of the drive.</param>
/// <param name="ToleranceFraction">
/// How far below target is acceptable (0.10 = within 90% of target). A wall-clock-paced single producer
/// can dip a little under the nominal rate without failing the engine's capability claim.
/// </param>
public sealed record ThroughputVerdict(
    string Profile, double TargetTps, double AchievedTps, double ToleranceFraction)
{
    /// <summary>Passes iff the achieved rate is within <see cref="ToleranceFraction"/> below the target.</summary>
    public bool Passed => AchievedTps >= TargetTps * (1.0 - ToleranceFraction);

    /// <summary>A one-line human reason leading with the achieved-vs-target rate.</summary>
    public string Reason => Passed
        ? $"{Profile}: held {AchievedTps:F0} TPS (target {TargetTps:F0}, ≥{TargetTps * (1.0 - ToleranceFraction):F0} required)"
        : $"{Profile}: only {AchievedTps:F0} TPS (target {TargetTps:F0}, ≥{TargetTps * (1.0 - ToleranceFraction):F0} required)";
}

/// <summary>
/// The §8.2 cold-replay-budget outcome: how long a projection cold-rebuild over the populated event
/// store took, vs the named budget for the replay CLASS. Folded into <see cref="RunArtefact"/> by the
/// L.3d replay measurement (bd babelstone-2e6q.4).
/// </summary>
/// <remarks>
/// In plain English: after a load run has filled the event store, rebuilding the read-model views from
/// scratch must finish inside a budget — 5 seconds for a v1 "with-a-plan" family, 30 seconds for a v4
/// "irregular" 5-year account ([event-store §8.2](../feature-design-event-store-projections.md)). This
/// records the rebuild time and whether it beat the budget. It does NOT assert the rebuilt state is
/// correct — that is the separate <see cref="NoDivergenceVerdict"/>.
/// </remarks>
/// <param name="ReplayClass">The §8.2 budget class ("with-a-plan" 5s / "irregular" 30s).</param>
/// <param name="EventsRefolded">Events the cold rebuild re-folded (the work the budget is over).</param>
/// <param name="ObservedMs">The measured rebuild wall-clock time, in milliseconds.</param>
/// <param name="BudgetMs">The §8.2 budget for this class, in milliseconds (5000 / 30000).</param>
public sealed record ReplayVerdict(string ReplayClass, int EventsRefolded, double ObservedMs, double BudgetMs)
{
    /// <summary>The §8.2 budget classes verbatim: with-a-plan v1 (5s) and irregular v4 (30s).</summary>
    public const double WithAPlanBudgetMs = 5_000;

    /// <summary>The §8.2 irregular-family v4 budget (a 5-year account, ~250–1000 events): 30s.</summary>
    public const double IrregularBudgetMs = 30_000;

    /// <summary>Passes iff the cold rebuild finished within the budget.</summary>
    public bool Passed => ObservedMs <= BudgetMs;

    /// <summary>A one-line human reason leading with the observed-vs-budget time.</summary>
    public string Reason => Passed
        ? $"{ReplayClass}: cold-rebuilt {EventsRefolded} events in {ObservedMs:F0} ms (budget {BudgetMs:F0} ms)"
        : $"{ReplayClass}: cold-rebuild {ObservedMs:F0} ms exceeded budget {BudgetMs:F0} ms ({EventsRefolded} events)";
}

/// <summary>
/// The §8.3 no-rebuild-divergence reliability invariant: a cold rebuild of a projection from the event
/// log reproduces the running projection's belief byte-for-byte (the [event-store §7.2](../feature-design-event-store-projections.md)
/// drill, run as the load test's final step). Folded into <see cref="RunArtefact"/> by L.3d
/// (bd babelstone-2e6q.4). This is the SAME invariant the monthly rebuild drill (bd babelstone-j67l)
/// asserts — the harness drives it; the invariant is owned by ADR-PC-002 §P4 / ADR-PC-010 §P5.
/// </summary>
/// <remarks>
/// In plain English: after the run, throw away the derived views and rebuild them purely from the raw
/// event log; the rebuilt views must match what was running, exactly. A match PROVES the event log is
/// the single source of truth and no slow, quiet bug crept into how the views are computed. A mismatch
/// is the worst event-sourcing failure mode caught before a customer or regulator sees it.
/// </remarks>
/// <param name="StreamsChecked">How many streams the drill rebuilt and compared.</param>
/// <param name="DivergentStreams">How many rebuilt beliefs did NOT match the running belief (0 = clean).</param>
/// <param name="EventsRefolded">Events the rebuild re-folded across the checked streams.</param>
public sealed record NoDivergenceVerdict(int StreamsChecked, int DivergentStreams, int EventsRefolded)
{
    /// <summary>
    /// Passes iff at least one stream was checked AND none diverged. Zero streams checked is an explicit
    /// FAIL (a no-divergence claim over nothing is vacuous — the same posture the latency observer takes
    /// on "no spans captured").
    /// </summary>
    public bool Passed => StreamsChecked > 0 && DivergentStreams == 0;

    /// <summary>A one-line human reason leading with the divergence count.</summary>
    public string Reason => StreamsChecked == 0
        ? "no streams checked — a no-divergence claim over zero streams is vacuous"
        : DivergentStreams == 0
            ? $"clean: {StreamsChecked} streams cold-rebuilt byte-identical ({EventsRefolded} events re-folded)"
            : $"DIVERGENCE: {DivergentStreams}/{StreamsChecked} streams' cold rebuild did not match the running belief";
}
