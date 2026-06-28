using Babelstone.Cadence;

namespace Babelstone.Notification;

/// <summary>
/// The stable composite notification id (ADR-PC-025 slot 4) — a core primitive, family-agnostic. In plain
/// terms: every reminder needs an idempotency key so re-running the loop or replaying the log never
/// double-notifies a customer, and the key is the same three inputs every time. The notification estate's named
/// face of the shared <see cref="CompositeId"/> primitive (ADR-PC-036 §Decision 2 + ADR-IC-019 mechanism
/// reuse): it renders the three notification parts to their canonical strings and folds them into a UUIDv5-style
/// id with no clock and no randomness, so the SAME inputs always yield the SAME id across re-reads, projection
/// refreshes, and process restarts — exactly as slot 4 requires. The core stamps this onto every
/// <see cref="ReminderDecision"/> a family rule produces, so a family rule never reimplements idempotency.
/// </summary>
public static class NotificationId
{
    /// <summary>
    /// Compute the composite <c>notification_id</c> for a reminder, by rendering the three composite parts to
    /// their canonical strings (<c>instance_id</c> as <c>"D"</c>, <c>template_ref</c> verbatim,
    /// schedule-occurrence as <c>yyyy-MM-dd</c>) and folding them through <see cref="CompositeId.Compute"/> — a
    /// SHA-256 over the canonical <c>|</c>-joined form, stamped as an RFC-4122 v5 GUID.
    /// </summary>
    /// <param name="instanceId">The instance (stream) the reminder is for.</param>
    /// <param name="templateRef">The pack-namespaced template (e.g. <c>pt.notice.maturity</c>).</param>
    /// <param name="scheduleOccurrence">The schedule-occurrence-id (e.g. a deposit's <c>maturity_date</c>),
    /// fixed on the instance.</param>
    public static Guid Compute(Guid instanceId, string templateRef, DateOnly scheduleOccurrence) =>
        CompositeId.Compute(
            instanceId.ToString("D"),
            templateRef,
            scheduleOccurrence.ToString("yyyy-MM-dd"));
}
