using System.Buffers.Binary;

namespace Babelstone.InboxConsumer.Tests;

/// <summary>
/// The Confluent wire-format framing the relay produces (ADR-IC-002 §P3 / ADR-IC-004 §P3), replicated
/// here so the consumer tests stand on the DOCUMENTED contract — magic byte 0x00 ‖ big-endian int32
/// schema_id ‖ Avro value — rather than reaching into <c>Babelstone.OutboxPublisher</c>'s internals.
/// The consumer lane stays decoupled from the producer assembly's private helpers; the bytes are the
/// contract both sides agree on.
/// </summary>
internal static class WireFormat
{
    private const byte MagicByte = 0x00;

    /// <summary>magic byte 0x00 ‖ big-endian int32 schema_id ‖ avro value (the exact bytes the relay emits).</summary>
    public static byte[] Frame(int schemaId, ReadOnlySpan<byte> avroValue)
    {
        var framed = new byte[5 + avroValue.Length];
        framed[0] = MagicByte;
        BinaryPrimitives.WriteInt32BigEndian(framed.AsSpan(1, 4), schemaId);
        avroValue.CopyTo(framed.AsSpan(5));
        return framed;
    }

    /// <summary>The reverse-DNS CloudEvents ce_type the relay emits (ADR-IC-008): "term_deposit.X" →
    /// "com.bank.deposits.X". Replicates OutboxDrainer.ReverseDnsType against the same documented form.</summary>
    public static string ReverseDnsType(string eventType)
    {
        var dot = eventType.IndexOf('.');
        var eventName = dot >= 0 ? eventType[(dot + 1)..] : eventType;
        return $"com.bank.deposits.{eventName}";
    }
}
