using System.Diagnostics;
using Babelstone.Engine.Avro;

namespace Babelstone.LoadHarness.Runner;

/// <summary>
/// The composition root that turns the LoadHarness library primitives into a runnable load test
/// (ADR-PC-011 §G4): it wires <see cref="WorkloadGenerator"/> → (optional <see cref="WorkloadDriver"/>
/// onto live Redpanda, §G1) → the in-process <see cref="EngineProjectionRig"/> (which emits the engine's
/// own OTel spans) → the <see cref="LatencyObserver"/>, then folds the §8.3 verdicts into a PASS/FAIL
/// <see cref="RunArtefact"/>. One host satisfies the whole L.3 ladder: smoke (L.3a), sustained (L.3b),
/// burst (L.3c), and the replay/no-divergence measurement (L.3d).
/// </summary>
/// <remarks>
/// In plain English: this is the conductor. It generates synthetic deposit traffic, optionally pushes it
/// onto the real message bus to prove the production producer path, drives the engine's real append+
/// project code so the engine emits the same telemetry it does in production, reads how long each commit
/// took from that telemetry, and prints a single PASS/FAIL with the seed to reproduce it.
/// </remarks>
internal sealed class LoadRunner
{
    private readonly RunnerOptions _options;
    private readonly TextWriter _out;

    public LoadRunner(RunnerOptions options, TextWriter output)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _out = output ?? throw new ArgumentNullException(nameof(output));
    }

    /// <summary>Runs the configured profile/measurement and returns the PASS/FAIL artefact.</summary>
    public async Task<RunArtefact> RunAsync(CancellationToken ct = default)
    {
        var spec = WorkloadSpec.Default();
        var calibration = Calibration.V4Placeholder();
        var codeRevision = CodeRevision();

        return _options.Measure switch
        {
            MeasureMode.Replay => await RunReplayAsync(spec, calibration, codeRevision, ct),
            _ => await RunLatencyAsync(spec, calibration, codeRevision, ct),
        };
    }

    // The §8.3 sync-latency (+ throughput for non-smoke profiles) path.
    private async Task<RunArtefact> RunLatencyAsync(
        WorkloadSpec spec, Calibration calibration, string codeRevision, CancellationToken ct)
    {
        var rig = new EngineProjectionRig(_options.PostgresConnectionString, TimeProvider.System, _options.RunId);
        await using var driver = BuildBusDriver();

        // Warm the JIT + Npgsql connection pool BEFORE the observer starts, so the §8.3 p99 reflects
        // steady-state boundary-to-commit latency, not the process's one-off cold-start outlier (a
        // production p99 likewise excludes process startup). The warmup events are appended but not
        // measured.
        await WarmUpAsync(spec, rig, ct);

        // Construct the observer AFTER warmup so only steady-state spans are captured (§P2/§G2).
        using var observer = new LatencyObserver();

        var phases = PlanPhases();
        long produced = 0;

        // ONE seeded generator drives the WHOLE run (§8.5: the (seed, run-id) reproduces it). A single
        // continuous event stream is consumed across all phases, so two phases of a burst profile never
        // draw the SAME seed-derived deposit ids (which would collide on the optimistic-concurrency head).
        using var events = NewEventStream(spec).GetEnumerator();

        // The throughput verdict keys off the phase that NAMES the profile's requirement: the burst phase
        // for the burst profile (§8.3: 1000 TPS for 15 min), else the sustained phase (250 TPS). Each
        // phase is measured against its OWN target + wall-clock, so a slow recovery never dilutes the
        // burst rate (and vice-versa).
        var keyPhaseLabel = _options.Profile == RunProfile.Burst ? "burst" : "sustained";
        double keyPhaseAchieved = 0;
        double keyPhaseTarget = _options.Profile == RunProfile.Burst ? _options.BurstTps : _options.TargetTps;

        foreach (var phase in phases)
        {
            _out.WriteLine($"→ phase '{phase.Label}': {phase.TargetTps:F0} TPS target for {phase.Duration}");
            var (phaseProduced, phaseElapsed) = await DrivePhaseAsync(phase, spec, events, rig, driver, ct);
            produced += phaseProduced;
            if (phase.Label == keyPhaseLabel && phaseElapsed.TotalSeconds > 0)
            {
                keyPhaseAchieved = phaseProduced / phaseElapsed.TotalSeconds;
            }
        }

        // Drain the projection so the materialised belief exists (keeps the rig consistent for any
        // follow-on replay/no-divergence measurement and proves the projector kept up).
        await rig.DrainAsync(ct);

        var verdicts = SyncLatencyBand.Section83Bands().Select(observer.Evaluate).ToList();

        ThroughputVerdict? throughput = null;
        if (_options.Profile != RunProfile.Smoke)
        {
            throughput = new ThroughputVerdict(keyPhaseLabel, keyPhaseTarget, keyPhaseAchieved, _options.Tolerance);
            _out.WriteLine($"→ {throughput.Reason}");
        }

        return new RunArtefact(_options.Seed, codeRevision, calibration, verdicts, produced, throughput);
    }

    // The L.3d cold-replay budget + no-rebuild-divergence path. First populates the store with a short
    // workload (so there is something to rebuild), then measures.
    private async Task<RunArtefact> RunReplayAsync(
        WorkloadSpec spec, Calibration calibration, string codeRevision, CancellationToken ct)
    {
        using var observer = new LatencyObserver();
        var rig = new EngineProjectionRig(_options.PostgresConnectionString, TimeProvider.System, _options.RunId);
        await using var driver = BuildBusDriver();

        // Populate: a single short phase at the configured rate, then drain so a running belief exists.
        var populate = new DrivePhase("populate", _options.TargetTps, _options.Duration);
        _out.WriteLine($"→ populating event store: {populate.TargetTps:F0} TPS for {populate.Duration}");
        using var events = NewEventStream(spec).GetEnumerator();
        var (produced, _) = await DrivePhaseAsync(populate, spec, events, rig, driver, ct);
        await rig.DrainAsync(ct);

        // §8.2 cold-replay budget: time a cold rebuild of one stream THIS run populated against the
        // class budget (a prior run's single-event stream would understate the work).
        var streams = rig.AppendedStreams;
        if (streams.Count == 0)
        {
            _out.WriteLine("✗ no streams populated — cannot measure replay (explicit FAIL).");
            var emptyReplay = new ReplayVerdict(
                _options.IrregularReplayClass ? "irregular" : "with-a-plan", 0, double.PositiveInfinity,
                _options.IrregularReplayClass ? ReplayVerdict.IrregularBudgetMs : ReplayVerdict.WithAPlanBudgetMs);
            var emptyDiv = new NoDivergenceVerdict(0, 0, 0);
            return new RunArtefact(
                _options.Seed, codeRevision, calibration,
                SyncLatencyBand.Section83Bands().Select(observer.Evaluate).ToList(),
                produced, Replay: emptyReplay, NoDivergence: emptyDiv);
        }

        var (elapsedMs, refolded) = await rig.TimeColdReplayAsync(streams[0], ct);
        var replayClass = _options.IrregularReplayClass ? "irregular" : "with-a-plan";
        var budgetMs = _options.IrregularReplayClass
            ? ReplayVerdict.IrregularBudgetMs
            : ReplayVerdict.WithAPlanBudgetMs;
        var replay = new ReplayVerdict(replayClass, refolded, elapsedMs, budgetMs);
        _out.WriteLine($"→ {replay.Reason}");

        // §8.3 no-rebuild-divergence invariant: a cold rebuild of every stream matches the running belief.
        var (checked_, divergent, drillRefolded) = await rig.RunNoDivergenceDrillAsync(ct);
        var noDivergence = new NoDivergenceVerdict(checked_, divergent, drillRefolded);
        _out.WriteLine($"→ {noDivergence.Reason}");

        // The §8.3 sync bands still evaluate (the populate phase emitted spans) so the replay artefact is
        // a superset of the latency artefact, not a narrower one.
        var verdicts = SyncLatencyBand.Section83Bands().Select(observer.Evaluate).ToList();
        return new RunArtefact(
            _options.Seed, codeRevision, calibration, verdicts, produced,
            Replay: replay, NoDivergence: noDivergence);
    }

    // A single continuous, seeded synthetic-event stream for the whole run (§8.5: the (seed, run-id)
    // reproduces it). The window length is nominal — emit instants only shape the peak-envelope DATA
    // clustering inside the generator; the DRIVE-RATE peak shaping is applied separately by the phase
    // loop against wall-clock. A large count gives every profile (incl. a 24h sustained at the §8.3 rig
    // rate) ample headroom; the loops stop on wall-clock duration, not on exhausting the stream.
    private IEnumerable<SyntheticEvent> NewEventStream(WorkloadSpec spec)
    {
        var generator = new WorkloadGenerator(_options.Seed, spec, Calibration.V4Placeholder());
        var simulatedStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var annualPeakDay = new DateOnly(2026, 11, 27);
        return generator.Generate(int.MaxValue, simulatedStart, TimeSpan.FromHours(24), annualPeakDay);
    }

    // Drive one phase: a wall-clock-paced loop that holds the phase's BASE target TPS, scaled in real
    // time by PeakEnvelope.MultiplierAt as a DRIVE-RATE multiplier (L.3b: the peak shape raises the rate
    // actually hitting the engine/bus, not just the generator's data clustering). Each event is appended
    // in-process (emitting the engine span the observer reads) and, when a bus driver is present, also
    // produced onto live Redpanda (the §G1 production path). The phase pulls from the SHARED run stream
    // so two phases never replay the same seed-derived deposit ids.
    private async Task<(long Produced, TimeSpan Elapsed)> DrivePhaseAsync(
        DrivePhase phase, WorkloadSpec spec, IEnumerator<SyntheticEvent> events,
        EngineProjectionRig rig, WorkloadDriver? driver, CancellationToken ct)
    {
        var envelope = new PeakEnvelope(spec);
        var simulatedStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var annualPeakDay = new DateOnly(2026, 11, 27);

        var sw = Stopwatch.StartNew();
        long produced = 0;
        var nextDueTicks = 0L;

        while (sw.Elapsed < phase.Duration)
        {
            ct.ThrowIfCancellationRequested();

            // The drive-rate multiplier at the CURRENT simulated instant (mapped from elapsed fraction of
            // the phase onto a simulated day) — the peak shape raises real send rate (L.3b).
            var fraction = sw.Elapsed.TotalSeconds / phase.Duration.TotalSeconds;
            var simulatedNow = simulatedStart + TimeSpan.FromHours(24) * Math.Clamp(fraction, 0, 1);
            var multiplier = envelope.MultiplierAt(simulatedNow, annualPeakDay);
            var currentTps = phase.TargetTps * multiplier;
            var intervalTicks = (long)(Stopwatch.Frequency / Math.Max(currentTps, 1e-6));

            var wait = nextDueTicks - sw.ElapsedTicks;
            if (wait > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(wait * 1000.0 / Stopwatch.Frequency), ct);
            }

            if (!events.MoveNext())
            {
                break; // the run stream is exhausted (effectively never at int.MaxValue events)
            }

            var synthetic = events.Current;

            // The §G1 production producer path (onto live Redpanda) — proves the bytes-on-the-bus path.
            if (driver is not null)
            {
                await driver.ProduceAsync(synthetic, ct);
            }

            // The §G2 measured path (in-process append → engine span the observer reads).
            await rig.AppendWithSpanAsync(synthetic, ct);

            produced++;
            nextDueTicks += intervalTicks;
        }

        sw.Stop();
        return (produced, sw.Elapsed);
    }

    // Append a handful of events (UNmeasured) to warm the JIT + Npgsql connection pool before the
    // observer starts, so the steady-state percentiles are not skewed by the process's cold start. The
    // warmup generator uses a seed offset so its stream ids differ from the measured workload's.
    private async Task WarmUpAsync(WorkloadSpec spec, EngineProjectionRig rig, CancellationToken ct)
    {
        if (_options.WarmupEvents <= 0)
        {
            return;
        }

        var warmupGenerator = new WorkloadGenerator(_options.Seed ^ unchecked((int)0x5EED_C0DE), spec, Calibration.V4Placeholder());
        var simulatedStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var synthetic in warmupGenerator.Generate(
                     _options.WarmupEvents, simulatedStart, TimeSpan.FromMinutes(1), new DateOnly(2026, 11, 27)))
        {
            await rig.AppendWithSpanAsync(synthetic, ct);
        }

        _out.WriteLine($"→ warmed up {_options.WarmupEvents} events (unmeasured).");
    }

    private IReadOnlyList<DrivePhase> PlanPhases() => PlanPhases(_options);

    /// <summary>
    /// The phase plan per profile (§G4): smoke = one short low-TPS phase; sustained = one phase at the
    /// target rate for the duration; burst = sustained → burst → recovery, sequenced (L.3c). Pure and
    /// static so the §8.3 burst sequencing is unit-testable Docker-free.
    /// </summary>
    internal static IReadOnlyList<DrivePhase> PlanPhases(RunnerOptions options) => options.Profile switch
    {
        RunProfile.Smoke =>
        [
            new DrivePhase("smoke", options.TargetTps, options.Duration),
        ],
        RunProfile.Sustained =>
        [
            new DrivePhase("sustained", options.TargetTps, options.Duration),
        ],
        RunProfile.Burst =>
        [
            // §8.3: sustained baseline → 1000 TPS burst for 15 min → recovery back to baseline.
            new DrivePhase("sustained", options.TargetTps, options.Duration),
            new DrivePhase("burst", options.BurstTps, options.BurstDuration),
            new DrivePhase("recovery", options.TargetTps, options.Duration),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(options)),
    };

    // Build the live-Redpanda producer (the §G1 path) with the engine's OWN Avro codec + a real Schema
    // Registry resolver, or null when --no-bus selects the in-process-only path.
    private WorkloadDriver? BuildBusDriver()
    {
        if (string.IsNullOrWhiteSpace(_options.BootstrapServers))
        {
            _out.WriteLine("→ bus path SKIPPED (--no-bus): in-process append/projection only.");
            return null;
        }

        var catalog = new AvroSchemaCatalog();
        var resolver = ConfluentSchemaIdResolver.Create(catalog, _options.SchemaRegistryUrl, registerIfAbsent: true);
        var serializer = new AvroEventSerializer(catalog, resolver);
        _out.WriteLine($"→ bus path: producing onto {_options.BootstrapServers} (SR {_options.SchemaRegistryUrl}).");
        return new WorkloadDriver(serializer, catalog, _options.BootstrapServers);
    }

    private static string CodeRevision() =>
        Environment.GetEnvironmentVariable("BABELSTONE_REVISION")
        ?? Environment.GetEnvironmentVariable("GITHUB_SHA")
        ?? "local-unversioned";

    // One drive phase: a target rate held for a wall-clock duration (the peak envelope scales it).
    internal readonly record struct DrivePhase(string Label, double TargetTps, TimeSpan Duration);
}
