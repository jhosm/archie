namespace Babelstone.LoadHarness;

/// <summary>
/// The §P4 reproducibility artefact: a run is named by <c>(seed, code revision)</c> and produces a
/// pass/fail report plus the raw OTel metric series (ADR-PC-011 §P4). A failure that does NOT reproduce
/// from <c>(seed, code revision)</c> is escalated above a deterministic failure — it implies
/// engine-level non-determinism the production engine cannot tolerate (§8.5).
/// </summary>
/// <remarks>
/// In plain English: every test run carries its seed and the exact engine version it ran against, so a
/// failure can be replayed exactly. If a failure can't be reproduced from that pair, that is the WORST
/// kind of failure — it means the engine itself is non-deterministic — and this record flags it. The
/// artefact is the SHARED report every L.3 slice extends: L.3a folds the three §8.3 sync bands; L.3b
/// adds a sustained-throughput verdict; L.3c a burst verdict; L.3d the replay + no-divergence verdicts.
/// The later verdict lists default to empty, so an artefact that only ran the L.3a sync bands is
/// unchanged.
/// </remarks>
/// <param name="Seed">The RNG seed the run was generated from (§8.5: bug reproductions cite the seed).</param>
/// <param name="CodeRevision">The engine code revision (e.g. the git SHA) the run executed against.</param>
/// <param name="Calibration">The §8.1 operator-calibration numbers the run was parameterised with.</param>
/// <param name="Verdicts">The per-band §8.3 latency outcomes the observer evaluated.</param>
/// <param name="EventsProduced">How many synthetic events the driver put on the bus / drove in-process.</param>
/// <param name="Throughput">
/// The §8.3 sustained/burst throughput outcome (achieved-vs-target TPS), or <see langword="null"/> when
/// the run did not measure throughput (e.g. the L.3a low-TPS smoke). Added by L.3b/L.3c.
/// </param>
/// <param name="Replay">
/// The §8.2 cold-replay-budget outcome (rebuild time vs the 5s/30s budget), or <see langword="null"/>
/// when the run did not measure replay. Added by L.3d.
/// </param>
/// <param name="NoDivergence">
/// The §8.3 no-rebuild-divergence reliability invariant (a cold rebuild reproduces the running belief
/// byte-for-byte), or <see langword="null"/> when the run did not measure it. Added by L.3d.
/// </param>
public sealed record RunArtefact(
    int Seed,
    string CodeRevision,
    Calibration Calibration,
    IReadOnlyList<LatencyVerdict> Verdicts,
    long EventsProduced,
    ThroughputVerdict? Throughput = null,
    ReplayVerdict? Replay = null,
    NoDivergenceVerdict? NoDivergence = null)
{
    /// <summary>
    /// The run PASSES iff every evaluated §8.3 band passed AND every OTHER verdict that ran also passed.
    /// A verdict that did not run (<see langword="null"/>) does not fail the run — only a verdict that
    /// ran and breached does (§8.3: the test is binary, but only over what was actually measured).
    /// </summary>
    public bool Passed =>
        Verdicts.Count > 0
        && Verdicts.All(v => v.Passed)
        && (Throughput is null || Throughput.Passed)
        && (Replay is null || Replay.Passed)
        && (NoDivergence is null || NoDivergence.Passed);

    /// <summary>A one-line human summary leading with the verdict, then the seed/revision to reproduce.</summary>
    public string Summary()
    {
        var extras = new List<string>();
        if (Throughput is not null)
        {
            extras.Add($"throughput {(Throughput.Passed ? "PASS" : "FAIL")} ({Throughput.AchievedTps:F0}/{Throughput.TargetTps:F0} TPS)");
        }

        if (Replay is not null)
        {
            extras.Add($"replay {(Replay.Passed ? "PASS" : "FAIL")} ({Replay.ObservedMs:F0}/{Replay.BudgetMs:F0} ms)");
        }

        if (NoDivergence is not null)
        {
            extras.Add($"no-divergence {(NoDivergence.Passed ? "PASS" : "FAIL")}");
        }

        var extraText = extras.Count == 0 ? string.Empty : "; " + string.Join(", ", extras);
        return $"{(Passed ? "PASS" : "FAIL")} — {EventsProduced} events, {Verdicts.Count(v => v.Passed)}/{Verdicts.Count} bands within budget{extraText}; "
            + $"reproduce with seed={Seed}, revision={CodeRevision}.";
    }
}
