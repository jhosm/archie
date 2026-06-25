using System.Security.Cryptography;
using System.Text;

namespace Babelstone.Notification;

/// <summary>
/// The stable composite notification id (ADR-PC-025 slot 4) — a core primitive, family-agnostic. In plain
/// terms: every reminder needs an idempotency key so re-running the loop or replaying the log never
/// double-notifies a customer, and the key is the same three inputs every time. A deterministic
/// UUIDv5-style hash of <c>instance_id + template_ref + schedule-occurrence-id</c>: no clock, no
/// randomness, so the SAME inputs always yield the SAME id across re-reads, projection refreshes, and
/// process restarts — exactly as slot 4 requires. The core stamps this onto every
/// <see cref="ReminderDecision"/> a family rule produces, so a family rule never reimplements idempotency.
/// </summary>
public static class NotificationId
{
    /// <summary>
    /// Compute the composite <c>notification_id</c> for a reminder. Computed from a SHA-256 over the
    /// canonical UTF-8 join, folded into a RFC-4122 v5 (name-based) GUID.
    /// </summary>
    /// <param name="instanceId">The instance (stream) the reminder is for.</param>
    /// <param name="templateRef">The pack-namespaced template (e.g. <c>pt.notice.maturity</c>).</param>
    /// <param name="scheduleOccurrence">The schedule-occurrence-id (e.g. a deposit's <c>maturity_date</c>),
    /// fixed on the instance.</param>
    public static Guid Compute(Guid instanceId, string templateRef, DateOnly scheduleOccurrence)
    {
        var canonical = $"{instanceId:D}|{templateRef}|{scheduleOccurrence:yyyy-MM-dd}";
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
