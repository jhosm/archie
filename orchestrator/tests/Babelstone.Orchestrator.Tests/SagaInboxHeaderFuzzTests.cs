using System.Text;
using Babelstone.Orchestrator.Inbox;
using Confluent.Kafka;
using Xunit;
using Xunit.Abstractions;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// In plain English: the saga consume loop reads a Kafka record's CloudEvents headers (its id, type,
/// subject, trace) to decide what to do — it never decodes the Avro payload. A producer, or anyone who
/// can publish to the topic, controls those header bytes. This throws a flood of broken, oversized,
/// mis-encoded, and missing headers at that decode and checks it always answers cleanly (a true/false
/// poison decision), never crashes, and never hangs. A garbled header is a poison record to skip, not an
/// exception that takes the consumer down.
///
/// <para>
/// Formally: a deterministic property-fuzz of <see cref="SagaConsumeLoop.TryDecodeHeaders"/> — the
/// payload-blind CloudEvents Binary-mode header decode (ADR-IC-015 / ADR-IC-018 §P5) that keys the saga
/// on the type name alone (ADR-IC-003 §P2), never the Avro value. A UNIT-lane test (ADR-IC-009 tiering):
/// it boots NO broker and NO PostgreSQL — it calls the internal static decode directly over a
/// synthetic <see cref="ConsumeResult{TKey,TValue}"/>. The <c>[Trait("Category","Fuzz")]</c> confines
/// it to the scheduled <c>fuzz.yml</c> .NET leg — excluded from the per-PR <c>ci.yml</c> lanes and
/// <c>mutation.yml</c> (the maintainer directive — fuzz tests execute only in <c>fuzz.yml</c>).
/// </para>
///
/// <para>
/// The contract under test, the same family contract <c>fuzz.yml</c> guards elsewhere: malformed input
/// yields a CLEAN result — a well-typed <c>false</c> (poison: skip) or a <c>true</c> with a coherent
/// <see cref="SagaInboxEvent"/> — never a panic/crash/hang. A <c>true</c> result MUST carry a parsed
/// <c>ce_id</c> and a non-empty event type (the two poison guards). The corpus is generated from a FIXED
/// seed so the run is deterministic (ADR-IC-009 unit tier); <c>FUZZ_SEED</c> / <c>FUZZ_ITERATIONS</c>
/// override the seed/budget for a fresh or deeper sweep.
/// </para>
/// </summary>
[Trait("Category", "Fuzz")]
public sealed class SagaInboxHeaderFuzzTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private const int DefaultIterations = 50_000;
    private const int DefaultRngSeed = 0x_5A6A_11B0;

    /// <summary>The standard CloudEvents / operational header keys the loop reads explicitly — the fuzz
    /// draws from these (with abused values) AND invents random <c>ce_*</c> extension keys, so both the
    /// known-header path and the extension-attribute projection are exercised.</summary>
    private static readonly string[] StandardKeys =
    [
        "ce_specversion", "ce_id", "ce_source", "ce_type", "ce_time",
        "ce_datacontenttype", "ce_subject", "ce_aggregatetype", "ce_correlationid", "traceparent",
    ];

    [Fact]
    public void Header_decode_cleanly_classifies_every_record_and_never_crashes()
    {
        var rngSeed = SeedValue();
        var rng = new Random(rngSeed);
        var iterations = IterationBudget();

        var failures = new List<string>();
        var decoded = 0;
        var poison = 0;

        for (var i = 0; i < iterations; i++)
        {
            var result = NextFuzzRecord(rng);
            try
            {
                if (SagaConsumeLoop.TryDecodeHeaders(result, out var message))
                {
                    decoded++;

                    // A true (non-poison) result must carry the two identities the poison guards require:
                    // a parsed ce_id (MessageId != Empty is NOT guaranteed — Guid.Empty is a legal parse of
                    // "00000000-..."; what IS guaranteed is that decoding SUCCEEDED only with a parseable
                    // ce_id) and a non-empty event type (the transition key).
                    if (message is null)
                    {
                        failures.Add($"iter {i}: returned true but message was null");
                    }
                    else if (string.IsNullOrEmpty(message.EventType))
                    {
                        failures.Add($"iter {i}: decoded with an EMPTY event type (should have been poison)");
                    }
                }
                else
                {
                    poison++;
                }

                // The sibling classifiers the loop calls on the same record must also never throw.
                _ = SagaConsumeLoop.IsTombstone(result);
            }
            catch (Exception ex)
            {
                failures.Add($"iter {i}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        _output.WriteLine(
            $"header-fuzz seed={rngSeed} iterations={iterations} → decoded={decoded} poison={poison} failures={failures.Count}");

        Assert.True(
            failures.Count == 0,
            $"TryDecodeHeaders must cleanly classify every record (poison-skip or decode), never crash, "
                + $"and never hang (reproduce with FUZZ_SEED={rngSeed}); offending records:\n"
                + string.Join("\n", failures.Take(10)));
    }

    /// <summary>The record-name extractor (the last dot-segment of a reverse-DNS ce_type) must never
    /// throw on any string — empty, dot-only, control chars, huge — it is on the same payload-blind path.</summary>
    [Fact]
    public void Record_name_extraction_never_crashes_on_any_ce_type_string()
    {
        var rng = new Random(SeedValue());
        for (var i = 0; i < 10_000; i++)
        {
            var ceType = RandomString(rng, rng.Next(0, 64), includeDots: true);
            var recordName = SagaConsumeLoop.RecordName(ceType);
            // The record name is always a suffix of the input (or the whole string when dot-free), so it
            // can never be longer than the input — a cheap invariant that catches an indexing slip.
            Assert.True(recordName.Length <= ceType.Length);
        }
    }

    /// <summary>The next fuzz record: a random subset of standard CloudEvents headers carrying abused
    /// values (sometimes valid GUIDs/types, usually garbage), plus random <c>ce_*</c> extension headers
    /// and the odd non-ce header, with values that are sometimes raw non-UTF-8 bytes — so the UTF-8
    /// header decode, the GUID parses, and the extension projection all see hostile input.</summary>
    private static ConsumeResult<byte[], byte[]> NextFuzzRecord(Random rng)
    {
        var headers = new Headers();

        // A random subset of the standard keys, each with an abused value.
        foreach (var key in StandardKeys)
        {
            if (rng.Next(0, 3) == 0)
            {
                continue; // omit this header (probe the missing-header poison/optional paths)
            }

            headers.Add(key, AbusedHeaderValue(rng, key));
        }

        // A handful of random ce_* extension attributes (ADR-IC-018 §P5) and the occasional non-ce header.
        var extras = rng.Next(0, 4);
        for (var e = 0; e < extras; e++)
        {
            var prefix = rng.Next(0, 4) == 0 ? string.Empty : "ce_";
            var key = prefix + RandomString(rng, rng.Next(0, 12), includeDots: false);
            if (key.Length == 0)
            {
                continue; // Confluent.Kafka rejects an empty header key
            }

            headers.Add(key, RandomBytes(rng, rng.Next(0, 256)));
        }

        // The record value is irrelevant to the header decode (it never reads the Avro), but a tombstone
        // (null/empty) is a distinct sibling path, so vary it.
        byte[]? value = rng.Next(0, 5) switch
        {
            0 => null,
            1 => [],
            _ => RandomBytes(rng, rng.Next(1, 16)),
        };

        return new ConsumeResult<byte[], byte[]>
        {
            Topic = "term_deposit",
            Message = new Message<byte[], byte[]>
            {
                Key = RandomBytes(rng, rng.Next(0, 8)),
                Value = value!,
                Headers = headers,
            },
        };
    }

    /// <summary>An abused value for a known header: sometimes a well-formed value (a real GUID for the
    /// id/subject/correlation keys, a reverse-DNS type for ce_type) so the accept branch is reached,
    /// usually garbage — a truncated GUID, an oversized string, or raw non-UTF-8 bytes.</summary>
    private static byte[] AbusedHeaderValue(Random rng, string key)
    {
        switch (rng.Next(0, 6))
        {
            case 0 when key is "ce_id" or "ce_subject" or "ce_correlationid":
                return Encoding.UTF8.GetBytes(GuidFrom(rng).ToString());
            case 0 when key == "ce_type":
                return Encoding.UTF8.GetBytes("com.bank.deposits." + RandomString(rng, rng.Next(1, 16), includeDots: false));
            case 1:
                return [];                                                  // empty value
            case 2:
                return Encoding.UTF8.GetBytes(RandomString(rng, 50_000, includeDots: true)); // oversized
            case 3:
                return RandomBytes(rng, rng.Next(0, 64));                   // raw, possibly non-UTF-8 bytes
            case 4:
                return Encoding.UTF8.GetBytes(GuidFrom(rng).ToString()[..rng.Next(1, 30)]); // truncated GUID-ish
            default:
                return Encoding.UTF8.GetBytes(RandomString(rng, rng.Next(0, 24), includeDots: true));
        }
    }

    // A deterministic Guid built from the seeded RNG (System.Guid.NewGuid() is non-deterministic and
    // would break the fixed-seed replay guarantee).
    private static Guid GuidFrom(Random rng)
    {
        var bytes = new byte[16];
        rng.NextBytes(bytes);
        return new Guid(bytes);
    }

    private static byte[] RandomBytes(Random rng, int length)
    {
        var bytes = new byte[length];
        rng.NextBytes(bytes);
        return bytes;
    }

    private static string RandomString(Random rng, int length, bool includeDots)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_ \t";
        var pool = includeDots ? alphabet + "." : alphabet;
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = pool[rng.Next(pool.Length)];
        }

        return new string(chars);
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
