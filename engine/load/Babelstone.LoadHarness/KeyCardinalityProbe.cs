using System.Diagnostics;
using Babelstone.Pii;

namespace Babelstone.LoadHarness;

/// <summary>
/// The §M.6 OpenBao transit KEY-CARDINALITY probe (bd c14p.2). Crypto-shredding (ADR-PC-004 §P2/§P3)
/// gives every data subject their OWN named transit key (<c>pii-&lt;subjectId&gt;</c>), so a retail bank
/// at v4 scale holds MILLIONS of named keys. ADR-PC-004 is silent on whether OpenBao carries that many,
/// and with Integrated Storage (Raft) the whole keyspace is memory-resident — so steady-state RAM,
/// snapshot size, unseal/join time, and per-key op latency all scale with key count. This probe seeds a
/// growing population of per-subject keys through the SAME <see cref="IPiiKeyStore"/> the engine uses,
/// and samples encrypt / decrypt / destroy latency at checkpoints AS cardinality climbs — so the
/// falsifiable signal is whether per-key op latency stays FLAT (cardinality-independent) or DEGRADES
/// (the v4-scale risk realised).
/// </summary>
/// <remarks>
/// <para>
/// In plain English: every customer who can ask to be forgotten gets their own encryption key in
/// OpenBao. A bank has millions of customers, so that is millions of keys, and nobody has checked
/// OpenBao stays healthy at that many. This probe makes a lot of keys and times how long encrypt /
/// decrypt / "forget this person" take as the key count grows — if the times stay flat the design
/// scales; if they climb with the key count we have found the ceiling and need a mitigation.
/// </para>
/// <para>
/// It runs against the engine's real key-store seam (<see cref="IPiiKeyStore"/> →
/// <c>OpenBaoTransitClient</c>), so the bytes and the API calls are production's, not a parallel
/// re-implementation — the same §S2 ecosystem-coherence posture ADR-PC-011 takes for the event harness.
/// The CI integration lane runs it at a bounded N against a dev-mode OpenBao container (which proves the
/// per-key-op slope but NOT the Raft snapshot/unseal dimensions — those need a Raft-backed cluster and
/// are recorded as the residual HA/DR sizing budget per ADR-PC-004 / ADR-PC-005).
/// </para>
/// </remarks>
public sealed class KeyCardinalityProbe(IPiiKeyStore keyStore)
{
    // A single fixed plaintext is encrypted under each key — the probe measures the COST of the key
    // population, not of varying payloads, so a constant payload isolates the cardinality variable.
    private static readonly byte[] Probe = "cardinality-probe"u8.ToArray();

    /// <summary>
    /// Seeds <paramref name="totalSubjects"/> per-subject keys (each one encrypt, creating the key), and
    /// at every <paramref name="checkpointEvery"/> keys takes a latency sample of encrypt / decrypt /
    /// destroy over a fresh probe subject — so the returned checkpoints show how op latency moves AS the
    /// resident key population grows. Subject ids are derived deterministically from
    /// <paramref name="seed"/> so a run reproduces (the §8.5/§P4 reproducibility posture).
    /// </summary>
    /// <param name="totalSubjects">The target resident key population to grow to (the v4-scale knob).</param>
    /// <param name="checkpointEvery">Sample op latency every this many seeded keys (must divide cleanly enough to yield ≥1 checkpoint).</param>
    /// <param name="seed">Seed for the deterministic subject-id stream (reproducibility).</param>
    /// <param name="sampleSize">How many op repetitions to time per checkpoint (the p99 sample depth).</param>
    public async Task<KeyCardinalityReport> SeedAndMeasureAsync(
        int totalSubjects,
        int checkpointEvery,
        int seed,
        int sampleSize = 20,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalSubjects);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(checkpointEvery);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleSize);

        var checkpoints = new List<KeyCardinalityCheckpoint>();
        var prefix = $"card-{seed:x8}";

        for (var seeded = 1; seeded <= totalSubjects; seeded++)
        {
            ct.ThrowIfCancellationRequested();
            // Encrypting creates the key if absent (EnsureKeyAsync) — one resident named key per subject.
            await keyStore.EncryptAsync($"{prefix}-{seeded}", Probe, ct);

            if (seeded % checkpointEvery == 0 || seeded == totalSubjects)
            {
                checkpoints.Add(await SampleAsync(prefix, residentKeys: seeded, sampleSize, ct));
            }
        }

        return new KeyCardinalityReport(seed, totalSubjects, checkpoints);
    }

    // One latency checkpoint: encrypt/decrypt/destroy a transient probe subject sampleSize times against
    // the current resident key population, and record each op's p99. The probe subject is destroyed each
    // iteration so it does not itself inflate the population being measured.
    private async Task<KeyCardinalityCheckpoint> SampleAsync(
        string prefix, int residentKeys, int sampleSize, CancellationToken ct)
    {
        var encrypt = new double[sampleSize];
        var decrypt = new double[sampleSize];
        var destroy = new double[sampleSize];

        for (var i = 0; i < sampleSize; i++)
        {
            ct.ThrowIfCancellationRequested();
            var subject = $"{prefix}-probe-{residentKeys}-{i}";

            var sw = Stopwatch.StartNew();
            var ciphertext = await keyStore.EncryptAsync(subject, Probe, ct);
            encrypt[i] = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            _ = await keyStore.DecryptAsync(subject, ciphertext, ct);
            decrypt[i] = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            await keyStore.DestroyKeyAsync(subject, ct);
            destroy[i] = sw.Elapsed.TotalMilliseconds;
        }

        return new KeyCardinalityCheckpoint(
            ResidentKeys: residentKeys,
            EncryptP99Ms: P99(encrypt),
            DecryptP99Ms: P99(decrypt),
            DestroyP99Ms: P99(destroy));
    }

    // Nearest-rank p99 over an unsorted sample (reuses the observer's deterministic, no-interpolation
    // convention so the two harness measurement paths agree on what "p99" means).
    internal static double P99(double[] samples)
    {
        var ascending = samples.OrderBy(d => d).ToArray();
        return LatencyObserver.Percentile(ascending, 0.99);
    }
}

/// <summary>
/// One cardinality checkpoint: the encrypt / decrypt / destroy p99 (ms) measured when
/// <see cref="ResidentKeys"/> per-subject keys were resident in OpenBao. A SET of these across a growing
/// population is what reveals the cardinality slope.
/// </summary>
public sealed record KeyCardinalityCheckpoint(
    int ResidentKeys, double EncryptP99Ms, double DecryptP99Ms, double DestroyP99Ms);

/// <summary>
/// The §M.6 key-cardinality measurement artefact (bd c14p.2): the per-checkpoint op-latency series as
/// the resident key population grew, plus the derived verdict on whether per-key op latency stayed flat.
/// This is the falsifiable input to the ADR-PC-004 / ADR-PC-005 residual-risk budget — a flat slope
/// supports per-subject named keys at v4 cardinality; a rising slope is the trigger for a sharding /
/// destroyable-per-subject-DEK mitigation.
/// </summary>
/// <remarks>
/// In plain English: the report says "with this many keys, encrypt/decrypt/forget each took this long",
/// and a one-line verdict on whether those times grew as the key count grew. Flat = the design scales;
/// growing = we hit the ceiling.
/// </remarks>
public sealed record KeyCardinalityReport(
    int Seed,
    int TotalSubjects,
    IReadOnlyList<KeyCardinalityCheckpoint> Checkpoints)
{
    /// <summary>
    /// True iff op latency did NOT degrade materially with cardinality. The verdict compares the
    /// HIGHEST-cardinality checkpoint against the FIRST: a real cardinality slope (the v4-scale risk) is
    /// worst at the largest resident population, so degradation shows up as the last/biggest checkpoint
    /// climbing past <paramref name="toleranceFactor"/>× the baseline. Judging the slope by the
    /// max-cardinality point (not "every intermediate checkpoint") makes a single transient mid-run spike
    /// on a shared/contended container non-fatal while still catching a genuine sustained climb. With a
    /// single checkpoint there is no slope to judge, so it is vacuously flat (the caller seeds enough to
    /// get ≥2 for a real verdict).
    /// </summary>
    public bool LatencyIsFlat(double toleranceFactor = 4.0)
    {
        if (Checkpoints.Count < 2)
        {
            return true;
        }

        var first = Checkpoints[0];
        // The degradation direction: compare against the checkpoint with the MOST resident keys, where a
        // cardinality slope is by definition worst. A transient spike at a smaller-cardinality checkpoint
        // is noise, not a slope, and does not flip the verdict.
        var peak = Checkpoints.MaxBy(c => c.ResidentKeys)!;
        return WithinTolerance(peak.EncryptP99Ms, first.EncryptP99Ms, toleranceFactor) &&
            WithinTolerance(peak.DecryptP99Ms, first.DecryptP99Ms, toleranceFactor) &&
            WithinTolerance(peak.DestroyP99Ms, first.DestroyP99Ms, toleranceFactor);
    }

    // A near-zero baseline (sub-ms in-memory dev op) would make any ratio explode on trivial jitter, so
    // compare against an absolute noise floor: only a genuinely large climb — tens of ms, the shape a real
    // cardinality slope takes — counts as degradation, never HTTP round-trip variance on a dev container.
    private const double NoiseFloorMs = 5.0;

    private static bool WithinTolerance(double later, double baseline, double factor)
        => later <= Math.Max(baseline, NoiseFloorMs) * factor;

    /// <summary>A one-line human summary leading with the verdict, then the cardinality span measured.</summary>
    public string Summary()
    {
        var last = Checkpoints.Count > 0 ? Checkpoints[^1] : null;
        var verdict = LatencyIsFlat() ? "FLAT" : "DEGRADING";
        var tail = last is null
            ? "no checkpoints"
            : $"at {last.ResidentKeys} keys: encrypt p99={last.EncryptP99Ms:F1}ms, decrypt p99={last.DecryptP99Ms:F1}ms, destroy p99={last.DestroyP99Ms:F1}ms";
        return $"{verdict} — seeded {TotalSubjects} per-subject keys (seed={Seed}); {tail}.";
    }
}
