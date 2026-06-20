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
/// The L.5 snapshot-accelerated replay outcome (bd babelstone-0uau.1): after a load run populated the
/// store AND snapshots were generated for it, this measures the snapshot-then-tail rebuild over the SAME
/// deep stream the cold <see cref="ReplayVerdict"/> measured, and folds two facts — (1) the
/// snapshot-accelerated state is BYTE-IDENTICAL to the cold-fold state (no divergence, the §P3
/// correctness invariant), and (2) the snapshot path stayed within the §8.2 budget AND was demonstrably
/// faster than cold. Honours ADR-PC-003 §P3 (snapshots accelerate, never gate correctness).
/// </summary>
/// <remarks>
/// In plain English: snapshots are a cache that lets the engine rebuild a long account's view by starting
/// from a saved checkpoint instead of replaying every event from the beginning. Once snapshots are real,
/// we must prove they actually help AND never lie. This verdict times the cold rebuild and the
/// snapshot-accelerated rebuild over the same stream, confirms the two produce the EXACT same state, and
/// reports the speedup. A faster-but-wrong snapshot is the worst failure mode — so identity is the
/// PASS-gating fact; the speedup is reported and must clear the budget, but a snapshot that diverges from
/// cold fails the run outright, no matter how fast it was.
/// </remarks>
/// <param name="ColdMs">The cold (snapshot-free, from-sequence-0) rebuild wall-clock, in milliseconds.</param>
/// <param name="SnapshotMs">The snapshot-accelerated (snapshot-then-tail) rebuild wall-clock, in milliseconds.</param>
/// <param name="BudgetMs">The §8.2 budget the snapshot path must also clear (5000 with-a-plan / 30000 irregular).</param>
/// <param name="StateIdentical">
/// Whether the snapshot-accelerated state's hash equals the cold-fold state's hash (the §P3 invariant).
/// </param>
/// <param name="SnapshotsApplied">
/// How many snapshots the accelerated path actually read for the measured stream. ZERO means the
/// acceleration did not engage (no snapshot was generated deep enough to skip events) — an explicit FAIL,
/// because a "speedup" claim over a path that read no snapshot is vacuous (the same anti-vacuous-pass
/// posture the latency observer and no-divergence verdict take).
/// </param>
/// <param name="EventsRefolded">Events the COLD path re-folded (the work the speedup is measured against).</param>
public sealed record SnapshotReplayVerdict(
    double ColdMs,
    double SnapshotMs,
    double BudgetMs,
    bool StateIdentical,
    int SnapshotsApplied,
    int EventsRefolded)
{
    /// <summary>
    /// The minimum speedup the accelerated path must demonstrate over cold on a deep stream. A snapshot
    /// that saved nothing measurable (≤ 1.0×) did not earn its keep; ADR-PC-003 §8.2 frames snapshots as
    /// the mechanism that keeps a deep irregular replay inside budget, so on a stream deep enough to have
    /// a snapshot the accelerated fold must be genuinely faster, not merely not-slower.
    /// </summary>
    public const double MinSpeedup = 1.0;

    /// <summary>The observed speedup factor (cold / snapshot); ≥ 1 when the snapshot path is faster.</summary>
    public double Speedup => SnapshotMs > 0 ? ColdMs / SnapshotMs : double.PositiveInfinity;

    /// <summary>
    /// Passes iff (1) at least one snapshot was applied (the acceleration engaged), (2) the
    /// snapshot-accelerated state is byte-identical to the cold fold (§P3: accelerate, never lie),
    /// (3) the snapshot path cleared the §8.2 budget, AND (4) it was demonstrably faster than cold. A
    /// divergence FAILS regardless of speed — a fast-but-wrong snapshot is the worst event-sourcing
    /// failure mode (§P4), so correctness gates, performance only qualifies.
    /// </summary>
    public bool Passed =>
        SnapshotsApplied > 0
        && StateIdentical
        && SnapshotMs <= BudgetMs
        && Speedup > MinSpeedup;

    /// <summary>A one-line human reason leading with the identity verdict, then the speedup.</summary>
    public string Reason => SnapshotsApplied == 0
        ? "no snapshot applied — the accelerated path read no snapshot, so a speedup claim is vacuous (FAIL)"
        : !StateIdentical
            ? "DIVERGENCE: snapshot-accelerated state did NOT match the cold fold byte-for-byte (a fast-but-wrong snapshot — §P3/§P4)"
            : Passed
                ? $"identical + {Speedup:F1}× faster: snapshot {SnapshotMs:F0} ms vs cold {ColdMs:F0} ms (budget {BudgetMs:F0} ms, {EventsRefolded} events, {SnapshotsApplied} snapshot(s))"
                : $"identical but not faster enough: snapshot {SnapshotMs:F0} ms vs cold {ColdMs:F0} ms ({Speedup:F1}×, budget {BudgetMs:F0} ms)";
}

/// <summary>
/// The L.3e synchronous-replication append-latency outcome (bd babelstone-2e6q.5 / ADR-PC-005 §P1): the
/// extra write-path latency the RPO≈0 safety guarantee costs, measured as append p50/p99 with
/// synchronous replication ON vs OFF, plus the delta. Closes ADR-PC-005's "known gap, no Test ID yet
/// wired" by giving the §P1 claim a falsifiable measurement; folded into <see cref="RunArtefact"/> by the
/// L.3e repl-latency measurement.
/// </summary>
/// <remarks>
/// <para>
/// In plain English: to never lose a committed event, the database is configured so every write must be
/// confirmed by a standby copy BEFORE it returns — that safety costs time on every write. This measures
/// how much, by timing appends with that "wait for the standby" guarantee on versus off, and reporting
/// the difference. The delta is the price of the RPO≈0 promise ADR-PC-005 §P1 makes — a real trade-off
/// the ADR requires the harness to validate, not assume.
/// </para>
/// <para>
/// LIVE-VERIFICATION GAP (carried deliberately, ADR-PC-005 §P1 Residual Risk 2): the full guarantee needs
/// a real warm standby that must confirm each commit — the HA k8s overlay (<c>infra/k8s/overlays/ha</c>),
/// NOT the single-node dev stack. Against a single node the measurement still runs (toggling
/// <c>synchronous_commit</c> on the session), but with no named standby the "on" side does not actually
/// block on a second node, so the delta is a FLOOR, not the production cost. The verdict records whether
/// it ran against a real standby (<see cref="StandbyConfirmed"/>); without one it is an advisory,
/// non-gating measurement (the same posture <c>KeyCardinalityProbe</c> takes on the Raft-vs-dev-mode
/// OpenBao gap).
/// </para>
/// </remarks>
/// <param name="SyncOffP50Ms">Append p50 with synchronous replication OFF (synchronous_commit relaxed).</param>
/// <param name="SyncOffP99Ms">Append p99 with synchronous replication OFF.</param>
/// <param name="SyncOnP50Ms">Append p50 with synchronous replication ON (commit waits for the standby).</param>
/// <param name="SyncOnP99Ms">Append p99 with synchronous replication ON.</param>
/// <param name="Samples">How many appends were timed per side (the p99 sample depth).</param>
/// <param name="StandbyConfirmed">
/// True only when the measurement ran against a real named warm standby (the HA overlay), so the "on"
/// side genuinely blocked on a second node. False against the single-node dev stack: the delta is then a
/// floor and the verdict is advisory (non-gating), with the live-cluster sizing flagged as a residual.
/// </param>
public sealed record ReplicationLatencyVerdict(
    double SyncOffP50Ms,
    double SyncOffP99Ms,
    double SyncOnP50Ms,
    double SyncOnP99Ms,
    int Samples,
    bool StandbyConfirmed)
{
    /// <summary>The §P1 cost: extra p50 append latency the RPO≈0 guarantee imposes (on − off), in ms.</summary>
    public double DeltaP50Ms => SyncOnP50Ms - SyncOffP50Ms;

    /// <summary>The §P1 cost: extra p99 append latency the RPO≈0 guarantee imposes (on − off), in ms.</summary>
    public double DeltaP99Ms => SyncOnP99Ms - SyncOffP99Ms;

    /// <summary>
    /// The measurement is GATING only when it ran against a real standby (the HA overlay): then the §8.3
    /// p99 sync-band budget (200 ms) bounds the sync-ON append p99 — a synchronous-replication cost that
    /// pushes commit p99 past the band is a real budget breach. Against the single-node dev stack the
    /// "on" side did not block on a second node, so the delta is a floor and the verdict is ADVISORY
    /// (always passes, carrying the floor) — gating on a non-representative number would be a false
    /// signal. <see cref="RunArtefact"/> still surfaces the numbers either way.
    /// </summary>
    public bool Passed => !StandbyConfirmed || SyncOnP99Ms <= GatingP99BudgetMs;

    /// <summary>The §8.3 current_balance p99 sync band the sync-ON append p99 is gated against on a real standby.</summary>
    public const double GatingP99BudgetMs = 200;

    /// <summary>A one-line human reason leading with the §P1 delta, then the gating context.</summary>
    public string Reason
    {
        get
        {
            var head = $"sync-repl p99 cost +{DeltaP99Ms:F1} ms (on {SyncOnP99Ms:F1} vs off {SyncOffP99Ms:F1}), p50 cost +{DeltaP50Ms:F1} ms, n={Samples}/side";
            return StandbyConfirmed
                ? Passed
                    ? $"{head}; warm standby confirmed, sync-ON p99 within the {GatingP99BudgetMs:F0} ms §8.3 band"
                    : $"{head}; warm standby confirmed, sync-ON p99 {SyncOnP99Ms:F1} ms BREACHED the {GatingP99BudgetMs:F0} ms §8.3 band"
                : $"{head}; ADVISORY (single-node, no named standby — the delta is a FLOOR; the production cost needs the HA overlay, ADR-PC-005 §P1).";
        }
    }
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
