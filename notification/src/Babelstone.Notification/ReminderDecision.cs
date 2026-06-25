namespace Babelstone.Notification;

/// <summary>
/// One "a notification is due" decision a family's <see cref="INotificationScheduleRule"/> produces
/// (ADR-PC-025 slot 2 semantics: which template, with what structural values, on what date) — BEFORE the
/// core stamps it with its composite id and dedupes it. It carries NO PII: the recipient and any name/NIF a
/// template interpolates are resolved by reference at render time (ADR-PC-025 PII rule); the
/// <see cref="Amounts"/> are the structural interpolation values only (integer cents — ADR-PC-010 §P1).
/// </summary>
/// <param name="InstanceId">The instance (stream) the reminder is for — the <c>instance_id</c> in the
/// ADR-PC-025 composite notification key.</param>
/// <param name="TemplateRef">The pack-namespaced template (e.g. <c>pt.notice.maturity</c>) — family-owned,
/// one of the three parts of the composite key.</param>
/// <param name="OccurrenceKey">The schedule-occurrence-id the composite <c>notification_id</c> is keyed on
/// (ADR-PC-025 slot 4). For a temporal reminder it is the occurrence date the schedule marks (e.g. a
/// deposit's <c>maturity_date</c>), fixed on the instance, so the same decision re-derives the same id
/// across re-reads and replay.</param>
/// <param name="DueAt">The valid date of the decision — the as-of date the pass ran for.</param>
/// <param name="Amounts">The structural interpolation values, keyed by name (e.g.
/// <c>total_payout_cents</c>), integer cents, NO PII.</param>
public sealed record ReminderDecision(
    Guid InstanceId,
    string TemplateRef,
    DateOnly OccurrenceKey,
    DateOnly DueAt,
    IReadOnlyDictionary<string, long> Amounts);

/// <summary>
/// A <see cref="ReminderDecision"/> the core has stamped with its stable composite
/// <c>notification_id</c> (ADR-PC-025 slot 4) and admitted past the dedupe ledger — i.e. a NEW reminder
/// this pass, not an idempotent replay. Turning it into an emitted <c>NotificationDue</c> over the outbox is
/// the sibling child bd babelstone-60n8.3 (blocked on the downstream-producer schema home, bd babelstone-ta8d).
/// </summary>
/// <param name="NotificationId">The stable composite idempotency key (ADR-PC-025 slot 4).</param>
/// <param name="InstanceId">The instance (stream) the reminder is for.</param>
/// <param name="TemplateRef">The pack-namespaced template.</param>
/// <param name="OccurrenceKey">The schedule-occurrence-id the id is keyed on.</param>
/// <param name="DueAt">The as-of date the pass ran for.</param>
/// <param name="Amounts">The structural interpolation values (integer cents, NO PII).</param>
public sealed record RaisedReminder(
    Guid NotificationId,
    Guid InstanceId,
    string TemplateRef,
    DateOnly OccurrenceKey,
    DateOnly DueAt,
    IReadOnlyDictionary<string, long> Amounts);
