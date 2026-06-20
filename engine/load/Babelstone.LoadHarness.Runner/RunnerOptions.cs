using System.Globalization;

namespace Babelstone.LoadHarness.Runner;

/// <summary>
/// Which run shape the host executes. The profiles map 1:1 to the L.3 acceptance slices so a single
/// host satisfies the whole ladder from one entry point (ADR-PC-011 §G4).
/// </summary>
public enum RunProfile
{
    /// <summary>L.3a: a short, low-TPS smoke that proves the wiring and the three §8.3 sync bands.</summary>
    Smoke,

    /// <summary>L.3b: hold a steady target TPS for a configured duration (250 TPS sustained).</summary>
    Sustained,

    /// <summary>L.3c: sustained → burst(1000 TPS / 15 min) → recovery, sequenced (bd babelstone-2e6q.3).</summary>
    Burst,
}

/// <summary>
/// What the host measures and folds into the <see cref="RunArtefact"/>. Latency is the default (the
/// §8.3 sync bands); replay is the L.3d cold-rebuild budget + no-divergence invariant (bd babelstone-2e6q.4).
/// </summary>
public enum MeasureMode
{
    /// <summary>The §8.3 sync-latency bands (+ a throughput verdict for the non-smoke profiles).</summary>
    Latency,

    /// <summary>The §8.2 cold-replay budget + the §8.3 no-rebuild-divergence invariant (L.3d).</summary>
    Replay,
}

/// <summary>
/// The parsed, validated command-line options for the load-test host — a pure value object so the arg
/// parsing is unit-testable Docker-free (the same posture the library's smoke test takes). Every run
/// names its seed (§8.5: a failure reproduces from <c>(seed, code revision)</c>).
/// </summary>
/// <remarks>
/// In plain English: this is just the run's settings, parsed from the command line — how fast to push
/// events, for how long, with which random seed, and against which database/broker. Keeping it a plain
/// object (not buried in <c>Main</c>) lets a test check the parsing without a live stack.
/// </remarks>
public sealed record RunnerOptions
{
    /// <summary>The run shape (§G4): smoke / sustained / burst.</summary>
    public RunProfile Profile { get; init; } = RunProfile.Smoke;

    /// <summary>What the run measures: the §8.3 latency bands or the §8.2/§8.3 replay budget.</summary>
    public MeasureMode Measure { get; init; } = MeasureMode.Latency;

    /// <summary>The §8.5 RNG seed; a failure reproduces from <c>(seed, code revision)</c>.</summary>
    public int Seed { get; init; } = 1234;

    /// <summary>
    /// The per-run stream-id namespace nonce. Defaults to a fresh GUID so repeated runs against one
    /// populated store never collide on the (deterministic, seed-derived) deposit ids; pass an explicit
    /// <c>--run-id</c> to reproduce a prior run's exact stream ids ((seed, run-id, revision)).
    /// </summary>
    public Guid RunId { get; init; } = Guid.NewGuid();

    /// <summary>The sustained target rate the drive loop holds, in events/second (§8.3: 250 TPS).</summary>
    public double TargetTps { get; init; } = 50.0;

    /// <summary>How long the sustained drive holds the target rate.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The burst rate the §8.3 burst phase ramps to (1000 TPS).</summary>
    public double BurstTps { get; init; } = 1000.0;

    /// <summary>How long the §8.3 burst phase holds the burst rate (15 min on the rig).</summary>
    public TimeSpan BurstDuration { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How far below target the achieved TPS may dip and still pass (0.10 = within 90%). A wall-clock
    /// single producer never hits the nominal rate to the decimal.
    /// </summary>
    public double Tolerance { get; init; } = 0.10;

    /// <summary>The live event-store connection string the host drives the engine append/replay path against.</summary>
    public string PostgresConnectionString { get; init; } =
        "Host=localhost;Port=5432;Database=babelstone;Username=babelstone;Password=babelstone";

    /// <summary>The Redpanda bootstrap the §G1 producer drives, or null to skip the bus (in-process only).</summary>
    public string? BootstrapServers { get; init; } = "localhost:19092";

    /// <summary>The Schema Registry URL the §G1 producer's Avro codec registers/resolves schema ids against.</summary>
    public string SchemaRegistryUrl { get; init; } = "http://localhost:18081";

    /// <summary>The §8.2 replay budget class for the L.3d measurement: with-a-plan (5s) or irregular (30s).</summary>
    public bool IrregularReplayClass { get; init; }

    /// <summary>
    /// How many unmeasured events to append before the observer starts, to warm the JIT + connection
    /// pool so the §8.3 p99 reflects steady state, not the process cold start (default 5; 0 disables).
    /// </summary>
    public int WarmupEvents { get; init; } = 5;

    /// <summary>
    /// Parses argv into a validated <see cref="RunnerOptions"/>. Unknown flags throw so a typo fails
    /// loud rather than silently running the default profile (a vacuous pass risk, §8.3).
    /// </summary>
    public static RunnerOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var o = new RunnerOptions();

        for (var i = 0; i < args.Count; i++)
        {
            var flag = args[i];
            switch (flag)
            {
                case "--profile":
                    o = o with { Profile = ParseProfile(Next(args, ref i, flag)) };
                    break;
                case "--measure":
                    o = o with { Measure = ParseMeasure(Next(args, ref i, flag)) };
                    break;
                case "--seed":
                    o = o with { Seed = int.Parse(Next(args, ref i, flag), CultureInfo.InvariantCulture) };
                    break;
                case "--run-id":
                    o = o with { RunId = Guid.Parse(Next(args, ref i, flag)) };
                    break;
                case "--tps":
                    o = o with { TargetTps = ParsePositiveDouble(Next(args, ref i, flag), flag) };
                    break;
                case "--burst-tps":
                    o = o with { BurstTps = ParsePositiveDouble(Next(args, ref i, flag), flag) };
                    break;
                case "--duration":
                    o = o with { Duration = ParseDuration(Next(args, ref i, flag)) };
                    break;
                case "--burst-duration":
                    o = o with { BurstDuration = ParseDuration(Next(args, ref i, flag)) };
                    break;
                case "--tolerance":
                    o = o with { Tolerance = ParseFraction(Next(args, ref i, flag), flag) };
                    break;
                case "--pg":
                    o = o with { PostgresConnectionString = Next(args, ref i, flag) };
                    break;
                case "--bootstrap":
                    o = o with { BootstrapServers = Next(args, ref i, flag) };
                    break;
                case "--schema-registry":
                    o = o with { SchemaRegistryUrl = Next(args, ref i, flag) };
                    break;
                case "--no-bus":
                    o = o with { BootstrapServers = null };
                    break;
                case "--irregular":
                    o = o with { IrregularReplayClass = true };
                    break;
                case "--warmup":
                    o = o with { WarmupEvents = int.Parse(Next(args, ref i, flag), CultureInfo.InvariantCulture) };
                    break;
                default:
                    throw new ArgumentException($"Unknown flag '{flag}'. See --help.", nameof(args));
            }
        }

        return o.Validate();
    }

    private RunnerOptions Validate()
    {
        if (TargetTps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TargetTps), TargetTps, "Target TPS must be positive.");
        }

        if (Duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Duration), Duration, "Duration must be positive.");
        }

        if (Tolerance is < 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Tolerance), Tolerance, "Tolerance must be in [0, 1).");
        }

        return this;
    }

    private static string Next(IReadOnlyList<string> args, ref int i, string flag)
    {
        if (i + 1 >= args.Count)
        {
            throw new ArgumentException($"Flag '{flag}' requires a value.", nameof(args));
        }

        return args[++i];
    }

    private static RunProfile ParseProfile(string value) => value.ToLowerInvariant() switch
    {
        "smoke" => RunProfile.Smoke,
        "sustained" => RunProfile.Sustained,
        "burst" => RunProfile.Burst,
        _ => throw new ArgumentException($"Unknown --profile '{value}' (smoke|sustained|burst)."),
    };

    private static MeasureMode ParseMeasure(string value) => value.ToLowerInvariant() switch
    {
        "latency" => MeasureMode.Latency,
        "replay" => MeasureMode.Replay,
        _ => throw new ArgumentException($"Unknown --measure '{value}' (latency|replay)."),
    };

    private static double ParsePositiveDouble(string value, string flag)
    {
        var d = double.Parse(value, CultureInfo.InvariantCulture);
        return d > 0 ? d : throw new ArgumentException($"Flag '{flag}' must be positive (got {value}).");
    }

    private static double ParseFraction(string value, string flag)
    {
        var d = double.Parse(value, CultureInfo.InvariantCulture);
        return d is >= 0 and < 1 ? d : throw new ArgumentException($"Flag '{flag}' must be in [0, 1) (got {value}).");
    }

    // Accepts "60s", "15m", "24h", or a bare number of seconds ("90"). A simple, deterministic parse so
    // `--duration 250 --duration 60s` reads the same on every machine.
    internal static TimeSpan ParseDuration(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();
        var unit = trimmed[^1];
        var numberText = char.IsLetter(unit) ? trimmed[..^1] : trimmed;
        var number = double.Parse(numberText, CultureInfo.InvariantCulture);
        if (number < 0)
        {
            throw new ArgumentException($"Duration cannot be negative (got {value}).", nameof(value));
        }

        return char.IsLetter(unit)
            ? unit switch
            {
                's' or 'S' => TimeSpan.FromSeconds(number),
                'm' or 'M' => TimeSpan.FromMinutes(number),
                'h' or 'H' => TimeSpan.FromHours(number),
                _ => throw new ArgumentException($"Unknown duration unit '{unit}' in '{value}' (s|m|h)."),
            }
            : TimeSpan.FromSeconds(number);
    }
}
