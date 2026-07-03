namespace Babelstone.Notification.Delivery;

/// <summary>
/// Why a notification became due — the delivery-side face of the governed <c>NotificationDue</c>
/// <c>trigger_kind</c> taxonomy (ADR-PC-025 §6; <c>contracts/avro/operations/NotificationDue.avsc</c>).
/// The wire rendering is SCREAMING_SNAKE_CASE (<c>EVENT_DRIVEN</c> / <c>SCHEDULED</c> /
/// <c>PRE_CONTRACTUAL</c>), matching the Avro enum symbols exactly.
/// </summary>
public enum NotificationTriggerKind
{
    /// <summary>A domain event implies the notification (e.g. a matured deposit implies a maturity
    /// notice). The engine emits these onto the post-commit bus; the delivery leg renders the
    /// instance-pinned template with render-time PII resolution before sending (bd babelstone-60n8.7).</summary>
    EventDriven,

    /// <summary>A date arriving implies the notification. Produced DOWNSTREAM by the notification
    /// scheduler off the engine's projections (ADR-PC-023 — the engine emits no clock-driven signal);
    /// the SCHEDULED delivery leg is bd babelstone-60n8.4.</summary>
    Scheduled,

    /// <summary>The FIN record copy whose legal gate was discharged synchronously inside the
    /// constitution saga (ADR-PC-025 "FIN is a saga step") — never the gate itself.</summary>
    PreContractual,
}

/// <summary>
/// One customer notification the delivery transport owes the communications-system consumer — the
/// delivery-side CLR mirror of the governed <c>NotificationDue</c> contract (ADR-PC-025 Decision 1;
/// <c>contracts/avro/operations/NotificationDue.avsc</c>, bd babelstone-60n8.3). BOTH legs produce this one
/// shape — the SCHEDULED leg maps the scheduler's <c>RaisedReminder</c> into it, the EVENT_DRIVEN leg maps
/// the consumed bus message into it — which is what makes the transport ONE machine parameterised by
/// <see cref="TriggerKind"/> (bd babelstone-60n8.7: shared, not duplicated).
/// </summary>
/// <remarks>
/// <b>NO PII, ever (ADR-PC-004 §P2 / ADR-PC-025 Decision 1).</b> <see cref="CustomerRef"/> is an opaque
/// subject REFERENCE resolved at render time against the engine's PII-resolve surface;
/// <see cref="Data"/> carries STRUCTURAL interpolation values only (amounts as integer-cent strings,
/// dates, rates). The signal is persisted in the delivery outbox across retries, so anything on it is
/// durable — which is exactly why resolved PII never lands here (it materialises transiently, per
/// attempt, at render time — ADR-PC-025 §PII).
/// </remarks>
/// <param name="NotificationId">The stable composite idempotency key (ADR-PC-025 slot 4) — identical
/// across outbox redelivery AND replay; the consumer's dedupe anchor and this outbox's enqueue key.</param>
/// <param name="InstanceId">The instance (stream) the notification is about.</param>
/// <param name="CustomerRef">The recipient REFERENCE (never the name/NIF/contact itself). Carried when the
/// source supplies it (the EVENT_DRIVEN bus message does); <see langword="null"/> for the v1 SCHEDULED leg,
/// whose read surface exposes no recipient reference yet — a named residual, not a contract change.</param>
/// <param name="TemplateRef">The pack-namespaced template to render (pack-owned namespace, ADR-PC-007).</param>
/// <param name="TemplatePackVersion">The pack version pinned on the instance (ADR-PC-007/ADR-PC-009).</param>
/// <param name="TriggerKind">EVENT_DRIVEN | SCHEDULED | PRE_CONTRACTUAL (ADR-PC-025 §6).</param>
/// <param name="CausationId">The causing domain event for EVENT_DRIVEN; <see langword="null"/> for
/// SCHEDULED (a date arriving has no causing domain event — ADR-PC-023).</param>
/// <param name="Data">Structural interpolation values only, keyed by template field name — no PII.</param>
/// <param name="DueAt">The date the notification is due (= valid_time, ADR-PC-025 Decision 1).</param>
public sealed record NotificationDueSignal(
    Guid NotificationId,
    Guid InstanceId,
    Guid? CustomerRef,
    string TemplateRef,
    string TemplatePackVersion,
    NotificationTriggerKind TriggerKind,
    Guid? CausationId,
    IReadOnlyDictionary<string, string> Data,
    DateOnly DueAt);
