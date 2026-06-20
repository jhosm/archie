using Babelstone.LoadHarness;
using Babelstone.LoadHarness.Runner;

// The runnable v1 acceptance-gate host (ADR-PC-011 §G4 / bd babelstone-2e6q.1..4). In plain English:
// this drives synthetic deposit traffic at a configured rate against the live dev stack, measures how
// fast the engine commits projections (and, in replay mode, how fast it cold-rebuilds them and whether
// the rebuild diverges), and exits 0 only if every measured §8.3 / §8.2 band passed.

if (args.Contains("--help") || args.Contains("-h"))
{
    PrintUsage(Console.Out);
    return 0;
}

RunnerOptions options;
try
{
    options = RunnerOptions.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine($"argument error: {ex.Message}");
    PrintUsage(Console.Error);
    return 2; // usage error
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.WriteLine(
    $"Babelstone load test — profile={options.Profile.ToString().ToLowerInvariant()} "
    + $"measure={options.Measure.ToString().ToLowerInvariant()} seed={options.Seed} run-id={options.RunId} "
    + $"tps={options.TargetTps:F0} duration={options.Duration}");

var runner = new LoadRunner(options, Console.Out);

RunArtefact artefact;
try
{
    artefact = await runner.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("run cancelled.");
    return 130; // SIGINT convention
}

Console.WriteLine();
Console.WriteLine(artefact.Summary());
foreach (var verdict in artefact.Verdicts)
{
    Console.WriteLine($"  [{(verdict.Passed ? "PASS" : "FAIL")}] {verdict.Band.ProjectionClass}: {verdict.Reason}");
}

if (artefact.Throughput is { } t)
{
    Console.WriteLine($"  [{(t.Passed ? "PASS" : "FAIL")}] {t.Reason}");
}

if (artefact.Replay is { } r)
{
    Console.WriteLine($"  [{(r.Passed ? "PASS" : "FAIL")}] {r.Reason}");
}

if (artefact.NoDivergence is { } d)
{
    Console.WriteLine($"  [{(d.Passed ? "PASS" : "FAIL")}] {d.Reason}");
}

if (artefact.SnapshotReplay is { } sr)
{
    Console.WriteLine($"  [{(sr.Passed ? "PASS" : "FAIL")}] snapshot-replay: {sr.Reason}");
}

if (artefact.ReplicationLatency is { } rl)
{
    Console.WriteLine($"  [{(rl.Passed ? "PASS" : "FAIL")}] repl-latency: {rl.Reason}");
}

// Exit code IS the gate (the same binary outcome §8.3 demands): 0 = PASS, 1 = FAIL. A CI cadence step
// (bd babelstone-2e6q.6) keys off this.
return artefact.Passed ? 0 : 1;

static void PrintUsage(TextWriter w)
{
    w.WriteLine(
        """
        Babelstone load-test host (ADR-PC-011 §G4).

        Usage: dotnet run --project engine/load/Babelstone.LoadHarness.Runner -- [flags]

          --profile smoke|sustained|burst   Run shape (default smoke).
          --measure <mode>                  What to measure (default latency). One of:
                                              latency          §8.3 sync bands (+ throughput)
                                              replay           §8.2 cold-replay budget + no-divergence (L.3d)
                                              snapshot-replay  snapshot-vs-cold parity + speedup (L.5)
                                              repl-latency     sync-replication append cost §P1 (L.3e)
                                              discard-rebuild  discard populated snapshots, rebuild cold (L.6)
          --seed <int>                      RNG seed (default 1234; §8.5 reproduces a run).
          --run-id <guid>                   Stream-id namespace nonce (default fresh; set to reproduce).
          --warmup <int>                    Unmeasured warmup events before measuring (default 5).
          --tps <double>                    Sustained target TPS (default 50; §8.3 rig: 250).
          --burst-tps <double>              Burst TPS for the burst profile (default 1000).
          --duration <Ns|Nm|Nh|N>           Sustained drive duration (default 10s; rig: 24h).
          --burst-duration <Ns|Nm|Nh|N>     Burst hold duration (default 15m).
          --tolerance <0..1)                Acceptable TPS shortfall fraction (default 0.10).
          --pg <connstring>                 Event-store connection string.
          --bootstrap <host:port>           Redpanda bootstrap (default localhost:19092).
          --schema-registry <url>           Schema Registry URL (default http://localhost:18081).
          --no-bus                          Skip the Redpanda producer (in-process only).
          --irregular                       Use the §8.2 irregular (30s) replay budget (else 5s).
          --depth <int>                     L.5/L.6 deep-stream event depth (default 64; must snapshot).
          --repl-samples <int>              L.3e appends timed per side, sync on/off (default 50).
          --standby-confirmed               L.3e: running against a real warm standby (HA overlay) — makes
                                            the repl-latency verdict GATING; without it it is advisory.
          -h, --help                        Show this help.

        Exit code: 0 = PASS, 1 = FAIL, 2 = usage error.
        """);
}
