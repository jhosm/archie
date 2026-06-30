using Confluent.Kafka;
using Xunit;
using Xunit.Abstractions;

namespace Babelstone.InboxConsumer.Tests;

/// <summary>
/// In plain English: this throws a flood of random and deliberately-broken byte strings at the bus
/// consumer's wire-format un-framer — the very first thing that touches a raw Kafka record's value —
/// and checks it always answers cleanly (a true/false decision), never crashes, never reads out of
/// bounds, and never hangs. A producer (or an attacker who can publish to a topic) controls these
/// bytes, so a malformed frame must be a clean rejection, not an exception that takes the consumer down.
///
/// <para>
/// Formally: a deterministic property-fuzz of <see cref="InboxPump.TryUnframe"/> — the Confluent
/// wire-format un-framing (magic byte 0x00 ‖ big-endian int32 schema_id ‖ Avro value, ADR-IC-002 §P3 /
/// ADR-IC-004 §P3) that <see cref="InboxPump.TryDecode"/> runs BEFORE the Avro value decode. The Avro
/// VALUE decode is fuzzed separately and deeply by <c>AvroDecodeFuzzTests</c> (the scheduled
/// <c>fuzz.yml</c> .NET leg); together they cover the full off-bus parse chain — the framing half here,
/// the value half there. This is a UNIT-lane test (ADR-IC-009 tiering): it boots NO broker, decodes no
/// Avro, and completes. The <c>[Trait("Category","Fuzz")]</c> confines it to the scheduled
/// <c>fuzz.yml</c> .NET leg — excluded from the per-PR <c>ci.yml</c> lanes and <c>mutation.yml</c>
/// (the maintainer directive — fuzz tests execute only in <c>fuzz.yml</c>).
/// </para>
///
/// <para>
/// The contract under test mirrors the Go/`fuzz.yml` family contract: malformed/garbage input yields a
/// CLEAN rejection — a well-typed <c>false</c> — never a panic/crash/hang, and a <c>true</c> result
/// always carries an in-bounds value slice (length == input length − 5). The corpus is generated from a
/// FIXED seed so the run is deterministic (a flaky fuzz is unacceptable, ADR-IC-009 unit tier);
/// <c>FUZZ_ITERATIONS</c> / <c>FUZZ_SEED</c> override the budget/seed for a deeper or fresh sweep.
/// </para>
/// </summary>
[Trait("Category", "Fuzz")]
public sealed class InboxWireFormatFuzzTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    /// <summary>How many un-framing attempts the per-PR lane runs. In-process and microsecond-cheap, so
    /// a generous default still stays well inside the unit budget; the scheduled leg can multiply it.</summary>
    private const int DefaultIterations = 50_000;

    /// <summary>The per-PR lane uses this FIXED seed so a failure is reproducible; a scheduled sweep can
    /// override it via FUZZ_SEED to explore fresh inputs. The effective seed is logged and named in the
    /// failure message, so any discovery stays reproducible by re-running with that FUZZ_SEED.</summary>
    private const int DefaultRngSeed = 0x_1B0C_0DEC;

    [Fact]
    public void Unframe_cleanly_accepts_or_rejects_every_byte_string_and_never_crashes()
    {
        var rngSeed = SeedValue();
        var rng = new Random(rngSeed);
        var iterations = IterationBudget();

        var failures = new List<string>();
        var accepted = 0;
        var rejected = 0;

        for (var i = 0; i < iterations; i++)
        {
            var input = NextFuzzInput(rng);
            try
            {
                if (InboxPump.TryUnframe(input, out var schemaId, out var avroValue))
                {
                    accepted++;

                    // A true result is only legal for a well-formed 5-byte header, and the recovered
                    // value MUST be exactly the bytes past the header — never a slice that runs past the
                    // input (the out-of-bounds read a hand-rolled span un-framer could leak).
                    if (input.Length < 5)
                    {
                        failures.Add($"iter {i}: accepted a {input.Length}-byte input (< 5-byte header)");
                    }
                    else if (avroValue.Length != input.Length - 5)
                    {
                        failures.Add(
                            $"iter {i}: value slice length {avroValue.Length} != input {input.Length} − 5; schemaId={schemaId}");
                    }
                }
                else
                {
                    rejected++;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"iter {i} ({input.Length}B): {ex.GetType().Name}: {ex.Message}");
            }
        }

        _output.WriteLine(
            $"unframe seed={rngSeed} iterations={iterations} → accepted={accepted} rejected={rejected} failures={failures.Count}");

        Assert.True(
            failures.Count == 0,
            $"TryUnframe must cleanly accept/reject every byte string, never crash, and never leak an "
                + $"out-of-bounds value slice (reproduce with FUZZ_SEED={rngSeed}); offending inputs:\n"
                + string.Join("\n", failures.Take(10)));
    }

    /// <summary>
    /// A tombstone (null OR zero-length value) must be detected without ever touching the un-framer, and
    /// the detection itself must never throw — Confluent.Kafka surfaces a compaction erasure as a null
    /// <c>Message.Value</c> (ADR-IC-002 §P4). Fuzzed here against random short values so the null/empty
    /// boundary stays a clean bool, never a NullReferenceException.
    /// </summary>
    [Fact]
    public void Tombstone_detection_never_crashes_on_null_empty_or_short_values()
    {
        var rng = new Random(SeedValue());
        for (var i = 0; i < 5_000; i++)
        {
            var value = (i % 17) switch
            {
                0 => (byte[]?)null,
                1 => [],
                _ => RandomBytes(rng, rng.Next(0, 8)),
            };
            var result = new ConsumeResult<byte[], byte[]>
            {
                Topic = "term_deposit",
                Message = new Message<byte[], byte[]> { Key = [0x01], Value = value! },
            };

            var isTombstone = InboxPump.IsTombstone(result);
            Assert.Equal(value is null || value.Length == 0, isTombstone);
        }
    }

    /// <summary>The next fuzz input: a mix of pure-random byte strings (various lengths, including the
    /// sub-header sizes that probe the bounds check) and validly-framed-then-corrupted records, so both
    /// the reject branch and the accept branch are exercised.</summary>
    private static byte[] NextFuzzInput(Random rng)
    {
        switch (rng.Next(0, 7))
        {
            case 0:
                return [];                                   // empty (tombstone-shaped)
            case 1:
                return RandomBytes(rng, rng.Next(1, 5));     // shorter than the 5-byte header
            case 2:
                return RandomBytes(rng, rng.Next(5, 64));    // header-sized-or-longer random noise
            case 3:
                return Repeat(0x00, rng.Next(0, 64));        // all-zero (valid magic, zero schema_id)
            case 4:
                return Repeat(0xFF, rng.Next(0, 64));        // all-0xFF (bad magic, max schema_id)
            case 5:
            {
                // A VALID frame (so the magic byte and header pass) carrying a random Avro value — drives
                // the accept branch and the value-slice bounds assertion.
                return WireFormat.Frame(schemaId: rng.Next(), avroValue: RandomBytes(rng, rng.Next(0, 48)));
            }
            default:
            {
                // A valid frame then a single-byte corruption somewhere — flips magic/header/value bits to
                // probe the boundary between accept and reject.
                var framed = WireFormat.Frame(schemaId: rng.Next(), avroValue: RandomBytes(rng, rng.Next(0, 48)));
                if (framed.Length > 0)
                {
                    framed[rng.Next(framed.Length)] ^= (byte)(1 << rng.Next(8));
                }

                return framed;
            }
        }
    }

    private static byte[] RandomBytes(Random rng, int length)
    {
        var bytes = new byte[length];
        rng.NextBytes(bytes);
        return bytes;
    }

    private static byte[] Repeat(byte value, int length)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, value);
        return bytes;
    }

    private static int IterationBudget()
        => int.TryParse(Environment.GetEnvironmentVariable("FUZZ_ITERATIONS"), out var n) && n > 0
            ? n
            : DefaultIterations;

    private static int SeedValue()
        => int.TryParse(Environment.GetEnvironmentVariable("FUZZ_SEED"), out var s)
            ? s
            : DefaultRngSeed;
}
