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
/// kind of failure — it means the engine itself is non-deterministic — and this record flags it.
/// </remarks>
/// <param name="Seed">The RNG seed the run was generated from (§8.5: bug reproductions cite the seed).</param>
/// <param name="CodeRevision">The engine code revision (e.g. the git SHA) the run executed against.</param>
/// <param name="Calibration">The §8.1 operator-calibration numbers the run was parameterised with.</param>
/// <param name="Verdicts">The per-band §8.3 latency outcomes the observer evaluated.</param>
/// <param name="EventsProduced">How many synthetic events the driver put on the bus.</param>
public sealed record RunArtefact(
    int Seed,
    string CodeRevision,
    Calibration Calibration,
    IReadOnlyList<LatencyVerdict> Verdicts,
    long EventsProduced)
{
    /// <summary>The run PASSES iff every evaluated §8.3 band passed (§8.3: the test is binary).</summary>
    public bool Passed => Verdicts.Count > 0 && Verdicts.All(v => v.Passed);

    /// <summary>A one-line human summary leading with the verdict, then the seed/revision to reproduce.</summary>
    public string Summary() =>
        $"{(Passed ? "PASS" : "FAIL")} — {EventsProduced} events, {Verdicts.Count(v => v.Passed)}/{Verdicts.Count} bands within budget; "
        + $"reproduce with seed={Seed}, revision={CodeRevision}.";
}
