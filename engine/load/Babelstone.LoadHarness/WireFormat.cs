using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Confluent.Kafka;

namespace Babelstone.LoadHarness;

/// <summary>
/// The Confluent wire-format framing (ADR-IC-002 §P3 / ADR-IC-004 §P3) and the CloudEvents 1.0
/// Binary-mode header set (ADR-IC-015) the outbox relay puts on every record. The relay's own
/// framing/header helpers (<c>OutboxDrainer.ToConfluentWireFormat</c> / <c>BuildHeadersCore</c>) are
/// <c>internal</c> to <c>Babelstone.OutboxPublisher</c>; rather than widen the engine's public surface
/// (or modify the engine assembly), the harness mirrors the SAME fixed envelope contract here — the
/// drift-prone part (the Avro VALUE bytes) still comes from the engine's own
/// <c>AvroEventSerializer.Encode</c>, so the bytes the test puts on the bus are production bytes (§G1).
/// </summary>
/// <remarks>
/// In plain English: Kafka messages carry a tiny standard prefix (a magic byte plus the schema id) so
/// consumers know how to read them, plus a set of CloudEvents headers describing the event. Both are a
/// fixed published contract, not engine business logic — this file reproduces exactly that contract so
/// the harness's messages look identical to the engine's. The actual event payload bytes are NOT
/// reproduced here; those are produced by the engine's own serializer.
/// </remarks>
internal static class WireFormat
{
    // Confluent wire format (ADR-IC-002 §P3): magic byte 0x00, big-endian int32 schema_id, then the
    // bare Avro value. Byte-for-byte identical to OutboxDrainer.ToConfluentWireFormat.
    private const byte MagicByte = 0x00;

    /// <summary>magic byte 0x00 ‖ big-endian int32 schema_id ‖ avro value (the relay's framing).</summary>
    public static byte[] ToConfluentWireFormat(int schemaId, ReadOnlySpan<byte> avroValue)
    {
        var framed = new byte[5 + avroValue.Length];
        framed[0] = MagicByte;
        BinaryPrimitives.WriteInt32BigEndian(framed.AsSpan(1, 4), schemaId);
        avroValue.CopyTo(framed.AsSpan(5));
        return framed;
    }

    /// <summary>
    /// The CloudEvents 1.0 Binary-mode Kafka headers (ADR-IC-015), built to match the relay's
    /// <c>BuildHeadersCore</c>: the same standard <c>ce_*</c> set, plus any family-declared extension
    /// headers from <c>DomainEvent.IntegrationHeaders</c> promoted to <c>ce_&lt;key&gt;</c>.
    /// </summary>
    /// <param name="eventId">The event/message id — <c>ce_id</c>.</param>
    /// <param name="source">The producing service URI — <c>ce_source</c> (e.g. "urn:babelstone:loadharness").</param>
    /// <param name="eventType">The dotted event type (e.g. "term_deposit.DepositConstituted").</param>
    /// <param name="aggregateType">The aggregate type / topic (e.g. "term_deposit") — <c>ce_aggregatetype</c>.</param>
    /// <param name="partitionKey">The aggregate/partition key — <c>ce_subject</c>.</param>
    /// <param name="time">The event time — <c>ce_time</c> (ISO-8601 round-trip).</param>
    /// <param name="extensionHeaders">Family-declared CE extension attributes (key WITHOUT the ce_ prefix).</param>
    public static Headers BuildCloudEventHeaders(
        Guid eventId,
        string source,
        string eventType,
        string aggregateType,
        Guid partitionKey,
        DateTimeOffset time,
        IReadOnlyDictionary<string, string>? extensionHeaders)
    {
        var headers = new Headers();
        Add(headers, "ce_specversion", "1.0");
        Add(headers, "ce_id", eventId.ToString());
        Add(headers, "ce_source", source);
        Add(headers, "ce_type", ReverseDnsType(eventType));
        Add(headers, "ce_time", time.ToString("O", CultureInfo.InvariantCulture));
        Add(headers, "ce_datacontenttype", "application/avro");
        Add(headers, "ce_subject", partitionKey.ToString());
        Add(headers, "ce_aggregatetype", aggregateType);

        if (extensionHeaders is { } extensions)
        {
            foreach (var (key, value) in extensions)
            {
                Add(headers, $"ce_{key}", value);
            }
        }

        return headers;
    }

    private static void Add(Headers headers, string key, string value) =>
        headers.Add(key, Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// Reverse-DNS CloudEvents type (ADR-IC-015), mirroring <c>OutboxDrainer.ReverseDnsType</c>:
    /// "term_deposit.DepositConstituted" → "com.bank.deposits.DepositConstituted".
    /// </summary>
    public static string ReverseDnsType(string eventType)
    {
        var dot = eventType.IndexOf('.', StringComparison.Ordinal);
        var eventName = dot >= 0 ? eventType[(dot + 1)..] : eventType;
        return $"com.bank.deposits.{eventName}";
    }
}
