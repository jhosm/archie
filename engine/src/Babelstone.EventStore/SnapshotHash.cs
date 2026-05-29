using System.Security.Cryptography;

namespace Babelstone.EventStore;

/// <summary>
/// The §8.3 snapshot hash: SHA-256 over the serialized state followed by the
/// <c>last_event_id</c> the snapshot covers. Folding the last event id in is what
/// lets a rebuild detect a snapshot taken at a different point in history. Pure and
/// deterministic — no clock, no randomness — so the same (state, lastEventId)
/// always yields the same digest across runs and machines.
/// </summary>
public static class SnapshotHash
{
    public static string Compute(ReadOnlySpan<byte> state, Guid lastEventId)
    {
        // Incremental so arbitrarily large state never lands on the stack or in a
        // throwaway concatenation buffer.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(state);

        Span<byte> idBytes = stackalloc byte[16];
        // Big-endian, culture-free byte layout for the id keeps the digest stable.
        lastEventId.TryWriteBytes(idBytes, bigEndian: true, out _);
        hash.AppendData(idBytes);

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        hash.GetCurrentHash(digest);
        return Convert.ToHexStringLower(digest);
    }
}
