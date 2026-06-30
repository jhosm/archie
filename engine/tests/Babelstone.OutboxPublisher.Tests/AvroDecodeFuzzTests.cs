using System.Collections.Concurrent;
using Babelstone.Engine;
using Babelstone.Engine.Avro;
using Babelstone.Families.TermDeposit;
using Babelstone.FinancialTypes;
using Xunit;
using Xunit.Abstractions;

namespace Babelstone.OutboxPublisher.Tests;

/// <summary>
/// In plain English: this throws a flood of mutated and garbage Avro bytes at the engine's
/// event-decode path — the code that parses raw bytes off the bus — and asserts it NEVER
/// crashes the process or hangs. Every input must either decode into a real event or be
/// rejected cleanly with a well-typed decode failure. There is no third outcome.
/// </summary>
/// <remarks>
/// The companion to the Go <c>FuzzLoad</c>/<c>FuzzRun</c> targets over pack-validate
/// (<c>.github/workflows/fuzz.yml</c>, bd archie-2t16.6): those fuzz the pack surface; THIS fuzzes
/// the engine's <see cref="AvroEventSerializer.Decode(ReadOnlyMemory{byte}, Type)"/> — the bytes a
/// consumer parses off Redpanda after the Confluent wire-format prefix is stripped (ADR-IC-004 §P3,
/// ADR-IC-002). The InboxConsumer's poison path (G.2) relies on this being a CLEAN rejection: a
/// well-typed decode failure routes the record to the poison sink; an UNCAUGHT crash (or a hang
/// allocating against an attacker-chosen length prefix) would take the pump down instead.
///
/// CONTRACT under test (the .NET twin of ADR-PC-007 §169's "garbage yields a clean rejection,
/// never a panic/crash/hang"): for ANY byte sequence, <c>Decode</c> either
///   (a) returns a non-null <see cref="DomainEvent"/> (the bytes happened to form a valid record), or
///   (b) throws a RECOGNISED decode-failure exception (see <see cref="IsCleanDecodeRejection"/>).
/// It must NOT throw an unrecognised exception type, must NOT return null, and must NOT hang.
///
/// This is PROPERTY-BASED / corpus fuzzing — the deterministic baseline. It runs ONLY on the
/// scheduled <c>fuzz.yml</c> .NET leg: the <c>[Trait("Category","Fuzz")]</c> excludes it from the
/// per-PR <c>ci.yml</c> lanes and from <c>mutation.yml</c> (the maintainer directive — fuzz tests
/// execute only in <c>fuzz.yml</c>). SharpFuzz/libFuzzer coverage-guided fuzzing is the STRETCH (a deferred follow-up; see the
/// class-tail note). The corpus is seeded from real encoded payloads of every catalogued
/// term-deposit event, then mutated; pure random byte strings cover the unstructured tail.
///
/// Sibling follow-ups in this lane build ON this scaffold (do NOT collapse them in here): richer
/// mutation operators (bd 2t16.16/.17/.19) extend <see cref="Mutators"/>, and a JSON-envelope leg
/// (bd 2t16.20) adds a parallel fuzz class over the envelope decode rather than the Avro value.
/// </remarks>
[Trait("Category", "Fuzz")]
public sealed class AvroDecodeFuzzTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    // How many fuzz iterations the deterministic per-PR lane runs. Kept modest so the default
    // `dotnet test` lane stays fast; the scheduled fuzz.yml leg multiplies it via FUZZ_ITERATIONS
    // for a deeper sweep without changing code.
    private const int DefaultIterations = 20_000;

    // The per-PR lane uses this FIXED seed so a failure is reproducible. The scheduled fuzz.yml leg
    // overrides it via FUZZ_SEED (derived from the run id) so each weekly sweep explores fresh inputs
    // instead of replaying the same bytes; the effective seed is logged below and named in the
    // failure message, so a discovery stays reproducible by re-running with that FUZZ_SEED.
    private const int DefaultRngSeed = 0x5EED_F0_0D;

    // A hard per-decode wall-clock budget. A malformed varint length prefix can make Apache.Avro's
    // BinaryDecoder attempt a huge read/allocation; a true HANG (not just a slow decode) is as much a
    // failure as a crash. Each decode runs on a worker with this timeout; exceeding it fails LOUD.
    private static readonly TimeSpan PerDecodeBudget = TimeSpan.FromSeconds(5);

    // The catalogued, decodable events (the three with a committed .avsc under contracts/avro/, after
    // the ADR-IC-017 §P4 promotion pass: DepositConstituted, InterestPaid, DepositMatured). These are
    // the only record names AvroEventSerializer can resolve, so they define the decode surface — the
    // de-promoted InterestAccrued/WithholdingApplied are now schemaless/store-only and cannot be encoded
    // or decoded as Avro at all.
    private static readonly (Type Type, Func<DomainEvent> Sample)[] DecodableEvents =
    [
        (typeof(DepositConstituted), () => new DepositConstituted(
            DepositId: Guid.NewGuid(),
            Principal: new Money(1_000_000),
            TanBasisPoints: 300,
            RateSheetVersionId: "rs-2026-01",
            TermDays: 364,
            StartDate: new DateOnly(2026, 1, 1),
            MaturityDate: new DateOnly(2026, 12, 31),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            PaymentPeriodMonths: 3,
            ProductCode: "dpz_pt_12m_juros_venc")),
        (typeof(InterestPaid), () => new InterestPaid(
            DepositId: Guid.NewGuid(),
            GrossInterest: new Money(30_417),
            WithholdingTax: new Money(8_517),
            NetInterest: new Money(21_900),
            PaidOn: new DateOnly(2026, 12, 31))),
        (typeof(DepositMatured), () => new DepositMatured(
            PrincipalReturned: new Money(1_000_000),
            NetInterestPaid: new Money(21_900),
            TotalPayout: new Money(1_021_900),
            MaturedOn: new DateOnly(2026, 12, 31))),
    ];

    private sealed class StubSchemaIdResolver : ISchemaIdResolver
    {
        public int ResolveSchemaId(string eventType) => 1;
    }

    private static AvroEventSerializer NewSerializer()
        => new(new AvroSchemaCatalog(), new StubSchemaIdResolver());

    /// <summary>
    /// The corpus + mutation fuzz. Builds known-valid encoded payloads for every catalogued event,
    /// then for each iteration mutates one (or substitutes pure random bytes) and decodes it as
    /// EVERY decodable type — so a mutated DepositConstituted is also thrown at the InterestAccrued
    /// reader, etc. Every outcome must be a clean decode or a clean rejection; the harness records any
    /// uncaught crash or timeout and fails with a reproducer.
    /// </summary>
    [Fact]
    public void Decode_never_crashes_or_hangs_on_mutated_or_garbage_avro_bytes()
    {
        var serializer = NewSerializer();
        var seeds = BuildSeedCorpus(serializer);
        var rngSeed = SeedValue();
        var rng = new Random(rngSeed);
        var iterations = IterationBudget();

        var failures = new ConcurrentBag<string>();
        var cleanDecodes = 0;
        var cleanRejections = 0;

        for (var i = 0; i < iterations; i++)
        {
            var input = NextFuzzInput(rng, seeds);

            foreach (var (type, _) in DecodableEvents)
            {
                var outcome = DecodeUnderBudget(serializer, input, type);
                switch (outcome.Kind)
                {
                    case OutcomeKind.Decoded:
                        cleanDecodes++;
                        break;
                    case OutcomeKind.CleanRejection:
                        cleanRejections++;
                        break;
                    default:
                        failures.Add(
                            $"iter {i} type {type.Name}: {outcome.Detail}\n  input ({input.Length}B): {Convert.ToHexString(input)}");
                        break;
                }
            }
        }

        _output.WriteLine(
            $"fuzz seed={rngSeed} iterations={iterations} seeds={seeds.Count} → cleanDecodes={cleanDecodes} cleanRejections={cleanRejections} failures={failures.Count}");

        Assert.True(
            failures.IsEmpty,
            $"Decode must never crash or hang on fuzzed Avro bytes (reproduce with FUZZ_SEED={rngSeed}); offending inputs:\n"
                + string.Join("\n", failures.Take(10)));
    }

    /// <summary>
    /// A targeted regression corpus: hand-picked byte shapes that historically break binary decoders —
    /// empty input, a length prefix claiming a gigantic string/array (the allocation-bomb shape), a
    /// truncated record, all-0xFF (a non-terminating varint), and the Confluent-framed payload fed RAW
    /// (the codec expects the bare value, so the 5-byte magic+id prefix is itself "garbage"). Each must
    /// clean-reject or clean-decode — never crash/hang. These also serve as permanent seeds the
    /// property loop keeps exercising.
    /// </summary>
    [Theory]
    [MemberData(nameof(HandPickedMalformations))]
    public void Decode_cleanly_rejects_each_hand_picked_malformation(string name, byte[] input)
    {
        var serializer = NewSerializer();

        foreach (var (type, _) in DecodableEvents)
        {
            var outcome = DecodeUnderBudget(serializer, input, type);
            Assert.True(
                outcome.Kind is OutcomeKind.Decoded or OutcomeKind.CleanRejection,
                $"malformation '{name}' decoded as {type.Name}: {outcome.Detail}");
        }
    }

    public static IEnumerable<object[]> HandPickedMalformations()
    {
        yield return ["empty", Array.Empty<byte>()];
        yield return ["single-zero", new byte[] { 0x00 }];
        yield return ["all-0xFF-32B", Enumerable.Repeat((byte)0xFF, 32).ToArray()];
        // A zig-zag long that decodes to a huge positive length — the classic "claim a 2GB string"
        // allocation bomb. 0xFE 0xFF…0x0F is a large varint; the decoder must reject, not OOM.
        yield return ["giant-length-prefix", new byte[] { 0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F }];
        yield return ["negative-length", new byte[] { 0x01 }]; // zig-zag for -1: an invalid length
        yield return ["truncated-record", new byte[] { 0x80, 0x80 }]; // an incomplete varint
        yield return ["confluent-framed-raw", ConfluentFramedSample()];
    }

    // ----- corpus + mutation -----------------------------------------------------------------------

    /// <summary>One known-valid encoded payload per catalogued event — the high-quality fuzz seeds.</summary>
    private static List<byte[]> BuildSeedCorpus(AvroEventSerializer serializer)
    {
        var seeds = new List<byte[]>();
        foreach (var (_, sample) in DecodableEvents)
        {
            seeds.Add(serializer.Encode(sample()).Bytes);
        }

        // A bare-bytes seed and a couple of structural malformations keep the unstructured tail in
        // the corpus the mutators draw from.
        seeds.Add(Array.Empty<byte>());
        seeds.Add([0x00]);
        return seeds;
    }

    /// <summary>
    /// The next fuzz input: usually a mutated seed, sometimes a pure random byte string. Keeping the
    /// strategy here (not inline) is what lets bd 2t16.16/.17/.19 add mutation operators in one place.
    /// </summary>
    private static byte[] NextFuzzInput(Random rng, IReadOnlyList<byte[]> seeds)
    {
        // ~15% pure random bytes (unstructured), ~85% mutated seeds (structured-but-broken). The
        // mutated path is where the decoder's varint/length/union handling gets stressed.
        if (rng.Next(100) < 15)
        {
            var len = rng.Next(0, 64);
            var buf = new byte[len];
            rng.NextBytes(buf);
            return buf;
        }

        var seed = seeds[rng.Next(seeds.Count)];
        var mutator = Mutators[rng.Next(Mutators.Length)];
        return mutator(rng, seed);
    }

    // Mutation operators over a seed payload. Each returns a NEW array (seeds stay pristine). This is
    // the extension point for the richer mutation siblings (bd 2t16.16/.17/.19) — add operators here.
    private static readonly Func<Random, byte[], byte[]>[] Mutators =
    [
        // Flip one random bit.
        static (rng, seed) =>
        {
            if (seed.Length == 0) return RandomBytes(rng, 1);
            var copy = (byte[])seed.Clone();
            var idx = rng.Next(copy.Length);
            copy[idx] ^= (byte)(1 << rng.Next(8));
            return copy;
        },
        // Replace one random byte.
        static (rng, seed) =>
        {
            if (seed.Length == 0) return RandomBytes(rng, 1);
            var copy = (byte[])seed.Clone();
            copy[rng.Next(copy.Length)] = (byte)rng.Next(256);
            return copy;
        },
        // Truncate to a random shorter length (an incomplete record off the wire).
        static (rng, seed) =>
        {
            if (seed.Length == 0) return seed;
            return seed[..rng.Next(seed.Length)];
        },
        // Append random trailing bytes (a record with junk after it).
        static (rng, seed) =>
        {
            var extra = RandomBytes(rng, rng.Next(1, 16));
            var copy = new byte[seed.Length + extra.Length];
            seed.CopyTo(copy, 0);
            extra.CopyTo(copy, seed.Length);
            return copy;
        },
        // Splice: overwrite a run of bytes with 0xFF (drives length-prefix / varint overflow paths).
        static (rng, seed) =>
        {
            if (seed.Length == 0) return RandomBytes(rng, 4);
            var copy = (byte[])seed.Clone();
            var start = rng.Next(copy.Length);
            var end = Math.Min(copy.Length, start + rng.Next(1, 8));
            for (var i = start; i < end; i++) copy[i] = 0xFF;
            return copy;
        },
    ];

    private static byte[] RandomBytes(Random rng, int len)
    {
        var buf = new byte[len];
        rng.NextBytes(buf);
        return buf;
    }

    // ----- the bounded decode + outcome classification ---------------------------------------------

    private enum OutcomeKind { Decoded, CleanRejection, Crash, Timeout }

    private readonly record struct Outcome(OutcomeKind Kind, string Detail);

    /// <summary>
    /// Run one decode under <see cref="PerDecodeBudget"/>. A returned event is <c>Decoded</c>; a
    /// recognised decode-failure exception is a <c>CleanRejection</c>; an unrecognised exception is a
    /// <c>Crash</c>; exceeding the budget is a <c>Timeout</c> (the hang case). The decode runs on a
    /// worker task so a runaway allocation/loop can be bounded by the wait rather than wedging the run.
    /// </summary>
    private static Outcome DecodeUnderBudget(AvroEventSerializer serializer, byte[] input, Type type)
    {
        // Capture the result on a worker so the timeout is observable. NOTE: .NET cannot forcibly abort
        // a managed thread, so a TRUE infinite loop would leak this task — acceptable for a test signal
        // (the run still fails LOUD on the timeout). In practice Apache.Avro's decoder terminates by
        // throwing on a bad/over-long read; the budget exists to convert a pathological slow-path into a
        // failure rather than an apparent hang.
        Outcome result = default;
        var task = Task.Run(() =>
        {
            try
            {
                var decoded = serializer.Decode(input, type);
                result = decoded is null
                    ? new Outcome(OutcomeKind.Crash, "Decode returned null (neither an event nor a thrown rejection)")
                    : new Outcome(OutcomeKind.Decoded, type.Name);
            }
            catch (Exception ex) when (IsCleanDecodeRejection(ex))
            {
                result = new Outcome(OutcomeKind.CleanRejection, $"{ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                result = new Outcome(OutcomeKind.Crash, $"UNEXPECTED {ex.GetType().FullName}: {ex.Message}");
            }
        });

        if (!task.Wait(PerDecodeBudget))
        {
            return new Outcome(OutcomeKind.Timeout, $"decode exceeded {PerDecodeBudget.TotalSeconds}s budget (possible hang)");
        }

        return result;
    }

    /// <summary>
    /// True when an exception is a WELL-TYPED decode rejection (outcome (b) of the contract) — the
    /// kinds Apache.Avro and the codec's own binding raise on malformed/garbage bytes. An exception
    /// OUTSIDE this set is treated as a crash so the suite surfaces any new failure mode rather than
    /// silently swallowing it. Notably it does NOT whitelist <see cref="OutOfMemoryException"/> or
    /// <see cref="StackOverflowException"/> — an allocation/recursion bomb is a CRASH, not a clean
    /// rejection.
    /// </summary>
    private static bool IsCleanDecodeRejection(Exception ex) => ex switch
    {
        // Apache.Avro's own decode failures: AvroException (and AvroTypeException : AvroException) on a
        // schema/encoding mismatch; the avro-lib type lives in the Avro namespace.
        _ when IsAvroLibException(ex) => true,
        // The codec's own binding guards (FromRecord/RequireField) throw these on a record that
        // resolves but does not bind to the target type.
        InvalidOperationException => true,
        ArgumentException => true,        // bad arg into the decoder/binder
        FormatException => true,          // malformed logical-type value
        IndexOutOfRangeException => true, // a decoder read past a too-short buffer
        OverflowException => true,        // a varint/length that overflows the target integer
        EndOfStreamException => true,     // the stream ran out mid-record
        IOException => true,              // underlying stream read failure on truncated input
        _ => false,
    };

    // Apache.Avro throws Avro.AvroException / Avro.AvroTypeException. Match by namespace so the test
    // does not need a compile-time using of every avro-lib exception type (and is robust to the lib
    // adding subtypes), while still NOT matching unrelated exceptions.
    private static bool IsAvroLibException(Exception ex)
    {
        for (var t = ex.GetType(); t is not null; t = t.BaseType)
        {
            if (t.Namespace == "Avro" && t.Name.Contains("Exception", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // ----- helpers ---------------------------------------------------------------------------------

    // Iteration budget: DefaultIterations on the per-PR lane; FUZZ_ITERATIONS overrides it for the
    // scheduled fuzz.yml .NET leg (a deeper sweep without a code change).
    private static int IterationBudget()
        => int.TryParse(Environment.GetEnvironmentVariable("FUZZ_ITERATIONS"), out var n) && n > 0
            ? n
            : DefaultIterations;

    // RNG seed: DefaultRngSeed on the per-PR lane (reproducible); FUZZ_SEED overrides it on the
    // scheduled leg so each run explores fresh inputs. fuzz.yml folds the run id into int32 range
    // before exporting it; the chosen value is logged by the fuzz body so a discovery is reproducible.
    private static int SeedValue()
        => int.TryParse(Environment.GetEnvironmentVariable("FUZZ_SEED"), out var s)
            ? s
            : DefaultRngSeed;

    // A real DepositConstituted payload, PREFIXED with the 5-byte Confluent wire-format frame
    // (magic 0x00 + a 4-byte schema id). The codec's Decode expects the BARE Avro value (the relay
    // strips the frame, ADR-IC-004 §P3), so feeding the framed bytes raw is a realistic "wrong layer"
    // malformation that must clean-reject rather than crash.
    private static byte[] ConfluentFramedSample()
    {
        var bare = NewSerializer().Encode(DecodableEvents[0].Sample()).Bytes;
        var framed = new byte[5 + bare.Length];
        framed[0] = 0x00;                  // Confluent magic byte
        framed[1] = 0x00;
        framed[2] = 0x00;
        framed[3] = 0x00;
        framed[4] = 0x01;                  // schema id = 1
        bare.CopyTo(framed, 5);
        return framed;
    }
}

// STRETCH (deferred follow-up, noted per the task): SharpFuzz/libFuzzer coverage-guided fuzzing of
// the same Decode entrypoint. SharpFuzz instruments the assembly and lets libFuzzer drive inputs
// toward new coverage, finding deeper malformations than this RNG-driven property loop. It needs a
// dedicated `dotnet sharpfuzz` instrumentation step + a separate executable harness, so it is tracked
// as its own issue rather than wired here; the corpus/seed shape above transfers directly to it.
