using Babelstone.Engine;
using Babelstone.Families.TermDeposit;
using Babelstone.FinancialTypes;

namespace Babelstone.LoadHarness;

/// <summary>
/// The §P1 GENERATOR: a deterministic, seeded function <c>(seed, calibration, simulated_window) →
/// event stream</c> producing the §8.2 event mix with correct <c>partition_key</c>s and the
/// daily/monthly/annual peak envelope (ADR-PC-011 §P1). It emits ONLY the harness-emitted classes
/// (the externally-ingested ~85% + the operational ~2%); engine-generated lifecycle (~10%) and
/// cross-mode (~3%) are produced by the engine when the clock advances (§8.4 "not via internal entry
/// points"), so this generator never fabricates them.
/// </summary>
/// <remarks>
/// <para>
/// In plain English: this is the synthetic-customer-traffic factory. Give it a seed and it produces
/// the exact same stream of deposit events every time — the SAME seed always yields the SAME sequence,
/// so a failing test can be reproduced from its seed alone (§8.5 / §G3). Different seeds make different
/// shapes (uniform vs clustered activity, normal vs heavy-tailed amounts), which is how the suite
/// exercises more than one data profile.
/// </para>
/// <para>
/// Determinism note (ADR-PC-011 §G3 / §8.5): all randomness comes from the single seeded
/// <see cref="Random"/> below — no <c>Guid.NewGuid()</c>, no clock read inside the draw. Partition keys
/// are derived deterministically from the seeded RNG so the (seed) fully reproduces the key stream too.
/// This generator is the impure-but-deterministic harness shell; it is NOT an engine fold handler, so
/// the purity rule that bars clock/RNG from handlers does not apply to it (it is the test driver).
/// </para>
/// <para>
/// Today the engine's only catalogued family is <c>term_deposit</c>, whose on-bus events are
/// <c>DepositConstituted</c> / <c>DepositMatured</c> / <c>InterestPaid</c>. The §8.2 mix names
/// current-account classes (<c>CardTxnSettled</c>, <c>TransferPosted</c>, …) that the engine does not
/// yet model; the generator binds each harness-emitted mix class to the catalogued event it maps to
/// today (constitution as the dominant externally-ingested class) while preserving the §8.2 SHAPE —
/// shares, sync/async split, peak envelope. New catalogued event classes drop in by extending
/// <see cref="DrawEvent"/>, with no change to the shape logic (Residual risk: the harness must not
/// foreclose the deferred classes, and this binding does not).
/// </para>
/// </remarks>
public sealed class WorkloadGenerator
{
    private readonly WorkloadSpec _spec;
    private readonly Calibration _calibration;
    private readonly Random _rng;
    private readonly IReadOnlyList<EventMixClass> _emittedClasses;
    private readonly double[] _cumulativeWeights;

    /// <summary>
    /// Creates a generator bound to a FIXED <paramref name="seed"/> (§8.5: every run names its RNG
    /// seed; rerunning with the same seed produces the same event sequence).
    /// </summary>
    public WorkloadGenerator(int seed, WorkloadSpec spec, Calibration calibration)
    {
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
        _rng = new Random(seed);

        // Only the harness-emitted classes participate in the emission distribution; their shares are
        // re-normalised to sum to 1 so the draw is a proper categorical distribution over what the
        // driver actually puts on the bus (the engine-generated/cross-mode shares stay documented in
        // the full mix but are produced by the engine, §P1).
        _emittedClasses = spec.Mix.Where(c => c.HarnessEmitted).ToList();
        if (_emittedClasses.Count == 0)
        {
            throw new ArgumentException("Workload spec has no harness-emitted event classes.", nameof(spec));
        }

        var totalShare = _emittedClasses.Sum(c => c.Share);
        _cumulativeWeights = new double[_emittedClasses.Count];
        var running = 0.0;
        for (var i = 0; i < _emittedClasses.Count; i++)
        {
            running += _emittedClasses[i].Share / totalShare;
            _cumulativeWeights[i] = running;
        }
    }

    /// <summary>
    /// Generates a deterministic stream of <paramref name="count"/> synthetic events across the
    /// simulated window <c>[windowStart, windowStart + windowLength)</c>, distributed by the §8.2 peak
    /// envelope so emit-instants cluster in the daily/monthly/annual peaks. The stream is reproducible:
    /// the SAME (seed, count, window) yields the SAME sequence (§8.5 / §G3).
    /// </summary>
    public IEnumerable<SyntheticEvent> Generate(
        int count, DateTimeOffset windowStart, TimeSpan windowLength, DateOnly annualPeakDay)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Event count cannot be negative.");
        }

        var envelope = new PeakEnvelope(_spec);

        for (var i = 0; i < count; i++)
        {
            var mixClass = DrawClass();
            var emitInstant = DrawEmitInstant(envelope, windowStart, windowLength, annualPeakDay);
            yield return DrawEvent(mixClass, emitInstant);
        }
    }

    private EventMixClass DrawClass()
    {
        var roll = _rng.NextDouble();
        for (var i = 0; i < _cumulativeWeights.Length; i++)
        {
            if (roll <= _cumulativeWeights[i])
            {
                return _emittedClasses[i];
            }
        }

        return _emittedClasses[^1];
    }

    // Rejection-sample an instant within the window weighted by the peak multiplier, so events cluster
    // in peaks. Bounded retries keep it deterministic and terminating; on exhaustion fall back to a
    // uniform draw (the multiplier is >= 1 everywhere, so the fallback only loses a little peak shaping).
    private DateTimeOffset DrawEmitInstant(
        PeakEnvelope envelope, DateTimeOffset windowStart, TimeSpan windowLength, DateOnly annualPeakDay)
    {
        const int maxAttempts = 8;
        var maxMultiplier = Math.Max(
            _spec.AnnualPeakMultiplier, Math.Max(_spec.MonthlyPeakMultiplier, _spec.DailyPeakMultiplier));

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var candidate = windowStart + windowLength * _rng.NextDouble();
            var acceptance = envelope.MultiplierAt(candidate, annualPeakDay) / maxMultiplier;
            if (_rng.NextDouble() <= acceptance)
            {
                return candidate;
            }
        }

        return windowStart + windowLength * _rng.NextDouble();
    }

    // Map a harness-emitted mix class to the catalogued event it produces today. The current catalogued
    // family is term_deposit; constitution is the dominant externally-ingested constructive event. The
    // amounts/keys are seeded so the (seed) reproduces them. Heavy-tailed amounts (§8.2) come from a
    // log-shaped draw; the partition key is the deposit/aggregate id.
    private SyntheticEvent DrawEvent(EventMixClass mixClass, DateTimeOffset emitInstant)
    {
        var depositId = DeterministicGuid();
        var startDate = DateOnly.FromDateTime(emitInstant.UtcDateTime);
        var termDays = TermDays();
        var principal = HeavyTailedPrincipal();
        var tanBasisPoints = _rng.Next(50, 400); // 0.50%–4.00% TAN

        var constituted = new DepositConstituted(
            DepositId: depositId,
            Principal: principal,
            TanBasisPoints: tanBasisPoints,
            RateSheetVersionId: "rs-load-2026-01",
            TermDays: termDays,
            StartDate: startDate,
            MaturityDate: startDate.AddDays(termDays),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            PaymentPeriodMonths: 0,
            ProductCode: ProductCodeFor(mixClass),
            Role: "standard",
            FundingAccount: $"acct-token-{depositId:N}");

        // partition_key == aggregate (deposit) id, so the driver keys the Kafka message by it and
        // per-partition_key delivery order matches event-store order (§8.3 reliability invariant).
        return new SyntheticEvent(depositId, constituted, mixClass.Name, emitInstant);
    }

    // A heavy-tailed principal (§8.2: normal vs heavy-tailed amounts): an exponential-ish draw over a
    // realistic deposit band (€500 – ~€500k). The whole euro amount is computed in full precision and
    // crosses the Money decimal→cents boundary exactly ONCE via Money.FromCents (ADR-PC-010 §P2 — round
    // HALF_EVEN once at the boundary, never mid-calculation).
    private Money HeavyTailedPrincipal()
    {
        var u = _rng.NextDouble();
        // -ln(1-u) is exponential; scale into a [€500, €500k] band.
        var euros = 500.0 + (-Math.Log(1.0 - u) * 25_000.0);
        euros = Math.Min(euros, 500_000.0);
        return Money.FromCents((decimal)euros * 100m);
    }

    private int TermDays()
    {
        // Common PT term lengths (~3, 6, 12, 24 months) drawn uniformly.
        int[] terms = [91, 182, 364, 728];
        return terms[_rng.Next(terms.Length)];
    }

    private static string ProductCodeFor(EventMixClass mixClass) => mixClass.Name switch
    {
        "card_transactions" => "dpz_pt_12m_card_seed",
        "transfers_direct_debits" => "dpz_pt_12m_transfer_seed",
        "operational" => "dpz_pt_op_seed",
        _ => "dpz_pt_seed",
    };

    // A deterministic GUID derived from the seeded RNG (NOT Guid.NewGuid(), which would break
    // reproducibility, §8.5). 16 seeded bytes → a GUID; the (seed) fully reproduces the key stream.
    private Guid DeterministicGuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        _rng.NextBytes(bytes);
        return new Guid(bytes);
    }

    /// <summary>The §8.1 calibration this generator was parameterised with (echoed into the run artefact).</summary>
    public Calibration Calibration => _calibration;
}
