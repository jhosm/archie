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
    /// <param name="sampleSize">
    /// How many op repetitions to time per checkpoint. The slope verdict is judged on the MEDIAN of this
    /// sample (a robust central statistic), so it need not be huge; but keep it ≥100 if the reported p99
    /// tail is to be a genuine percentile rather than the single slowest request — nearest-rank p99 only
    /// stops being the max once the sample reaches 100 (<c>ceil(0.99·100)=99</c> ⇒ the 2nd-slowest).
    /// </param>
    /// <param name="warmupIterations">
    /// Op cycles run and DISCARDED before the measured sample at each checkpoint. The first ops against a
    /// freshly-started container pay cold-start, JIT and HTTP connection-pool establishment costs that are
    /// not the per-key op cost; discarding them keeps every checkpoint comparable so a cold first
    /// checkpoint cannot manufacture (or mask) a cardinality slope.
    /// </param>
    public async Task<KeyCardinalityReport> SeedAndMeasureAsync(
        int totalSubjects,
        int checkpointEvery,
        int seed,
        int sampleSize = 50,
        int warmupIterations = 5,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalSubjects);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(checkpointEvery);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleSize);
        ArgumentOutOfRangeException.ThrowIfNegative(warmupIterations);

        var checkpoints = new List<KeyCardinalityCheckpoint>();
        var prefix = $"card-{seed:x8}";

        for (var seeded = 1; seeded <= totalSubjects; seeded++)
        {
            ct.ThrowIfCancellationRequested();
            // Encrypting creates the key if absent (EnsureKeyAsync) — one resident named key per subject.
            await keyStore.EncryptAsync($"{prefix}-{seeded}", Probe, ct);

            if (seeded % checkpointEvery == 0 || seeded == totalSubjects)
            {
                checkpoints.Add(await SampleAsync(prefix, residentKeys: seeded, sampleSize, warmupIterations, ct));
            }
        }

        return new KeyCardinalityReport(seed, totalSubjects, checkpoints);
    }

    // One latency checkpoint: encrypt/decrypt/destroy a transient probe subject against the current
    // resident key population, recording each op's median and p99. A handful of warm-up cycles run first
    // and are discarded (see warmupIterations) so cold-start/connection-pool spikes never pollute the
    // baseline. The probe subject is destroyed each iteration so it does not itself inflate the population
    // being measured.
    private async Task<KeyCardinalityCheckpoint> SampleAsync(
        string prefix, int residentKeys, int sampleSize, int warmupIterations, CancellationToken ct)
    {
        for (var w = 0; w < warmupIterations; w++)
        {
            ct.ThrowIfCancellationRequested();
            _ = await RunOpCycleAsync($"{prefix}-warmup-{residentKeys}-{w}", ct); // timings discarded
        }

        var encrypt = new double[sampleSize];
        var decrypt = new double[sampleSize];
        var destroy = new double[sampleSize];

        for (var i = 0; i < sampleSize; i++)
        {
            ct.ThrowIfCancellationRequested();
            (encrypt[i], decrypt[i], destroy[i]) = await RunOpCycleAsync($"{prefix}-probe-{residentKeys}-{i}", ct);
        }

        return new KeyCardinalityCheckpoint(
            ResidentKeys: residentKeys,
            Encrypt: OpLatency.From(encrypt),
            Decrypt: OpLatency.From(decrypt),
            Destroy: OpLatency.From(destroy));
    }

    // One encrypt → decrypt → destroy cycle over a transient probe subject, returning each op's elapsed
    // milliseconds. Destroying the subject each cycle keeps it from inflating the resident population.
    private async Task<(double Encrypt, double Decrypt, double Destroy)> RunOpCycleAsync(
        string subject, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var ciphertext = await keyStore.EncryptAsync(subject, Probe, ct);
        var encrypt = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        _ = await keyStore.DecryptAsync(subject, ciphertext, ct);
        var decrypt = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        await keyStore.DestroyKeyAsync(subject, ct);
        var destroy = sw.Elapsed.TotalMilliseconds;

        return (encrypt, decrypt, destroy);
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
/// One operation's latency at a checkpoint, reduced to two nearest-rank statistics: the MEDIAN — the
/// robust central statistic the cardinality-slope verdict is judged on — and the P99 tail, reported as
/// context (see <see cref="KeyCardinalityReport.LatencyIsFlat"/> for why the tail is reported but not the
/// CI gate). Both use the no-interpolation convention of <see cref="LatencyObserver.Percentile"/>.
/// </summary>
public sealed record OpLatency(double MedianMs, double P99Ms)
{
    /// <summary>Reduces a raw latency sample to its median + p99 (both nearest-rank, sorted once).</summary>
    public static OpLatency From(double[] samples)
    {
        var ascending = samples.OrderBy(d => d).ToArray();
        return new OpLatency(
            MedianMs: LatencyObserver.Percentile(ascending, 0.50),
            P99Ms: LatencyObserver.Percentile(ascending, 0.99));
    }
}

/// <summary>
/// One cardinality checkpoint: the encrypt / decrypt / destroy latency (median + p99, ms) measured when
/// <see cref="ResidentKeys"/> per-subject keys were resident in OpenBao. A SET of these across a growing
/// population is what reveals the cardinality slope.
/// </summary>
public sealed record KeyCardinalityCheckpoint(
    int ResidentKeys, OpLatency Encrypt, OpLatency Decrypt, OpLatency Destroy);

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
    /// HIGHEST-cardinality checkpoint against the FIRST — a real cardinality slope (the v4-scale risk) is
    /// worst at the largest resident population — and judges the slope on each op's MEDIAN.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why the median, not the p99: a genuine cardinality slope shifts the WHOLE distribution (every op
    /// gets slower as the resident keyspace grows — GC/page pressure on a memory-resident Raft keyspace),
    /// so it moves the median. A single slow HTTP round-trip to a dev-mode, single-node OpenBao container
    /// moves only the tail and leaves the median put. Judging on the median therefore keeps the falsifiable
    /// ADR-PC-004 §P2/§P3 signal while NOT flaking on single-sample jitter. The previous verdict compared
    /// p99s computed over a sample of 15, where nearest-rank p99 (<c>ceil(0.99·15)=15</c>) IS the single
    /// slowest request — so one contended round-trip flipped FLAT→DEGRADING (observed on PR #316, whose
    /// diff was disjoint from the crypto path).
    /// </para>
    /// <para>
    /// The p99 tail is still recorded on every checkpoint and surfaced in <see cref="Summary"/>, so the
    /// human reading CI logs and the production v4-cardinality sizing pass (which reuses this code against a
    /// Raft-backed cluster, where the tail is meaningful) keep the tail as DATA. It is deliberately NOT a
    /// CI gate: on the single-node dev-mode container the tail is dominated by container/HTTP jitter, and
    /// slope detection does not need it. With a single checkpoint there is no slope to judge, so the report
    /// is vacuously flat (the caller seeds enough to get ≥2 for a real verdict).
    /// </para>
    /// </remarks>
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
        return WithinTolerance(peak.Encrypt.MedianMs, first.Encrypt.MedianMs, toleranceFactor) &&
            WithinTolerance(peak.Decrypt.MedianMs, first.Decrypt.MedianMs, toleranceFactor) &&
            WithinTolerance(peak.Destroy.MedianMs, first.Destroy.MedianMs, toleranceFactor);
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
            : $"at {last.ResidentKeys} keys: encrypt median={last.Encrypt.MedianMs:F1}ms (p99 {last.Encrypt.P99Ms:F1}), " +
              $"decrypt median={last.Decrypt.MedianMs:F1}ms (p99 {last.Decrypt.P99Ms:F1}), " +
              $"destroy median={last.Destroy.MedianMs:F1}ms (p99 {last.Destroy.P99Ms:F1})";
        return $"{verdict} — seeded {TotalSubjects} per-subject keys (seed={Seed}); {tail}.";
    }
}
