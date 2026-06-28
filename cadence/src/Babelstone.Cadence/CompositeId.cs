using System.Security.Cryptography;
using System.Text;

namespace Babelstone.Cadence;

/// <summary>
/// A stable composite idempotency id (ADR-PC-036 §Decision 2 + ADR-IC-019 / ADR-PC-025 slot 4) — a generic
/// primitive. In plain terms: a cadence pass needs an idempotency key so re-running the loop or replaying a log
/// never acts twice, and the key must be the same canonical inputs every time. This computes a deterministic
/// UUIDv5-style id from an ordered list of string parts: no clock, no randomness, so the SAME parts always
/// yield the SAME id across re-reads, refreshes, and process restarts. The notification scheduler keys on
/// <c>(instance_id, template_ref, schedule-occurrence)</c>; the ADR-PC-036 lifecycle driver keys on
/// <c>(instance_id, command_kind, stable_occurrence_key)</c> — both are just an ordered part list to this
/// primitive, which is what lets the machinery stay product-unaware.
/// </summary>
public static class CompositeId
{
    /// <summary>
    /// Compute a composite id from <paramref name="parts"/>. The parts are joined canonically with a
    /// <c>|</c> separator (so the caller is responsible for a stable, collision-free part order and rendering),
    /// hashed with SHA-256 over the UTF-8 bytes, and folded into an RFC-4122 version 5 (name-based) GUID — never
    /// a v4 random one, so the value is deterministic and replay-stable.
    /// </summary>
    /// <param name="parts">The ordered composite-key parts, each already rendered to its canonical string
    /// (e.g. a GUID as <c>"D"</c>, a date as <c>yyyy-MM-dd</c>). Order is load-bearing — the id distinguishes
    /// the parts positionally.</param>
    public static Guid Compute(params string[] parts)
    {
        var canonical = string.Join('|', parts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        // Take the first 16 bytes and stamp RFC-4122 version 5 (name-based, SHA-1/SHA-256) + the
        // variant bits, so the value is a well-formed, deterministic GUID — never a v4 random one.
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC-4122 variant
        return new Guid(bytes);
    }
}
