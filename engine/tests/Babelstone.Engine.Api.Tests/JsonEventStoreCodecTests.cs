using System.Text;
using Babelstone.Engine;
using Babelstone.Engine.Api;
using Xunit;

namespace Babelstone.Engine.Api.Tests;

/// <summary>
/// The decided event-store payload codec (ADR-PC-028, bd babelstone-36mk): <see cref="JsonEventSerializer"/>
/// is the behavioural half of <c>EVENT_STORE_PAYLOAD_SELF_DESCRIBING</c> — it decodes the book of record from
/// the bytes alone, with <b>no Schema Registry</b> and no writer-schema resolution, and it is deterministic so
/// replay reads identical bytes back. These promote it from the walking-skeleton "deferred-Avro stand-in" to a
/// tested, decided store codec. (The structural half — the replay spine takes no registry dependency — lives in
/// <c>Babelstone.Engine.Tests.EventStorePayloadSelfDescribingTests</c>.)
/// </summary>
public sealed class JsonEventStoreCodecTests
{
    // A representative stored event: the substrate types a payload must carry (Guid, integer-cents, string,
    // DateOnly). No byte[] here — records compare byte[] by reference, which would defeat value-equality;
    // ciphertext bytes are exercised separately below.
    private sealed record SampleStored(Guid Id, long AmountCents, string Note, DateOnly On) : DomainEvent;

    private static SampleStored Sample() => new(
        Id: Guid.Parse("11111111-2222-3333-4444-555555555555"),
        AmountCents: 1_000_000,
        Note: "constituição",
        On: new DateOnly(2026, 6, 10));

    [Fact]
    public void Round_trips_the_store_payload_with_no_schema_registry()
    {
        // The codec is constructed with NOTHING — no registry client, no resolver — and decodes from the
        // bytes alone. That is what "self-describing" buys: the book of record reads with the registry down.
        var codec = new JsonEventSerializer();
        var original = Sample();

        var encoded = codec.Encode(original);
        var decoded = (SampleStored)codec.Decode(encoded.Bytes, typeof(SampleStored));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Is_deterministic_same_event_encodes_to_identical_bytes()
    {
        // Replay reads stored bytes back; a deterministic codec keeps the book of record stable and makes the
        // future store↔bus equivalence check (STORE_BUS_ENCODING_EQUIVALENCE) reproducible.
        var codec = new JsonEventSerializer();

        Assert.Equal(codec.Encode(Sample()).Bytes, codec.Encode(Sample()).Bytes);
    }

    // A field that carries opaque bytes — the shape PII ciphertext takes once the real protector lands
    // (ADR-PC-004 §P2). Today the NullPiiProtector means events carry no ciphertext, so this is the codec-level
    // guarantee that, when they do, the bytes ride inside the JSON payload as base64 and survive the round trip.
    private sealed record Encrypted(byte[] Ciphertext) : DomainEvent;

    [Fact]
    public void Carries_ciphertext_bytes_as_base64_inside_the_json_payload()
    {
        var codec = new JsonEventSerializer();
        byte[] ciphertext = [0xDE, 0xAD, 0xBE, 0xEF];

        var bytes = codec.Encode(new Encrypted(ciphertext)).Bytes;
        var json = Encoding.UTF8.GetString(bytes);
        Assert.Contains(Convert.ToBase64String(ciphertext), json);

        var decoded = (Encrypted)codec.Decode(bytes, typeof(Encrypted));
        Assert.Equal(ciphertext, decoded.Ciphertext); // xUnit compares byte[] element-wise
    }
}
