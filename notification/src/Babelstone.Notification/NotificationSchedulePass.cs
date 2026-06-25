using Microsoft.Extensions.Logging;

namespace Babelstone.Notification;

/// <summary>
/// The notification core's generic per-tick engine (ADR-IC-019 §D2) — family-agnostic. In plain terms: once
/// a day's worth of family rules have each decided "these instances are due", THIS is the shared machinery
/// that turns those decisions into raised reminders without double-notifying. It enumerates every registered
/// family <see cref="INotificationScheduleRule"/>, stamps each returned <see cref="ReminderDecision"/> with
/// its stable composite <c>notification_id</c> (<see cref="NotificationId"/>), and admits it past the dedupe
/// ledger (ADR-PC-025 slot 4) — so re-running a pass over the same world raises nothing twice.
/// </summary>
/// <remarks>
/// The pass owns the id-derivation and the dedupe — the two idempotency primitives — so no family rule
/// reimplements them (a family rule just decides which instances are due and which template they carry,
/// ADR-IC-019 §D1/Amendment-A1). The clock lives one layer up, in <see cref="NotificationWorker"/>
/// (ADR-PC-023 §6); this pass is a deterministic function of the as-of date and the registered rules, so it
/// is trivially testable with a fake rule and the in-memory ledger.
/// </remarks>
public sealed class NotificationSchedulePass(
    IEnumerable<INotificationScheduleRule> rules,
    INotificationDedupeLedger dedupeLedger,
    ILogger<NotificationSchedulePass>? logger = null)
{
    private readonly IReadOnlyList<INotificationScheduleRule> _rules =
        (rules ?? throw new ArgumentNullException(nameof(rules))).ToList();

    private readonly INotificationDedupeLedger _dedupeLedger =
        dedupeLedger ?? throw new ArgumentNullException(nameof(dedupeLedger));

    /// <summary>
    /// Run ONE scheduling pass as-of <paramref name="asOf"/>: ask every registered family rule which
    /// reminders are due, stamp each with its composite <c>notification_id</c>, and return only the NEW
    /// ones (the dedupe ledger absorbs repeats). Running it again over the same world returns an empty list
    /// — the slot-4 "re-runs don't re-notify" guarantee.
    /// </summary>
    /// <param name="asOf">Today, supplied by the caller — the clock lives in the worker loop
    /// (ADR-PC-023 §6), never read here, so the pass is deterministic for a given date.</param>
    public async Task<IReadOnlyList<RaisedReminder>> RunOnceAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var raised = new List<RaisedReminder>();

        foreach (var rule in _rules)
        {
            var decisions = await rule.EvaluateAsync(asOf, ct);
            foreach (var decision in decisions)
            {
                var notificationId = NotificationId.Compute(
                    decision.InstanceId, decision.TemplateRef, decision.OccurrenceKey);

                // Dedupe on the composite key (ADR-PC-025 slot 4): a key already in the ledger was raised
                // on a prior pass (or a projection refresh re-surfaced the same instance), so re-deriving
                // the SAME key here is the expected idempotent case — not a second notification.
                if (!await _dedupeLedger.TryReserveAsync(notificationId, ct))
                {
                    continue;
                }

                raised.Add(new RaisedReminder(
                    NotificationId: notificationId,
                    InstanceId: decision.InstanceId,
                    TemplateRef: decision.TemplateRef,
                    OccurrenceKey: decision.OccurrenceKey,
                    DueAt: decision.DueAt,
                    Amounts: decision.Amounts));
            }
        }

        if (raised.Count > 0)
        {
            logger?.LogInformation(
                "Notification schedule pass raised {Count} new reminder(s) as-of {AsOf} across {RuleCount} " +
                "family rule(s) (ADR-PC-025 slot-4 dedupe).", raised.Count, asOf, _rules.Count);
        }

        return raised;
    }
}
