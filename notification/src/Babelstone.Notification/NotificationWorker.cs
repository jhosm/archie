using Babelstone.Cadence;
using Microsoft.Extensions.Logging;

namespace Babelstone.Notification;

/// <summary>
/// The notification service's host shell — the notification-estate face of the shared
/// <see cref="CadenceWorker"/> (ADR-PC-036 §Decision 2 + ADR-IC-019 mechanism reuse). A thin adapter: it carries
/// the notification estate's own logger category and its <see cref="NotificationSchedulerOptions"/>, and binds
/// the generic clock-owning poll loop to the family-agnostic <see cref="NotificationSchedulePass"/>. All of the
/// loop behaviour — clock ownership (ADR-PC-023 §6), the one-pass-per-tick cadence, and the
/// backpressure/exponential-backoff retry — lives in the shared <see cref="CadenceWorker"/>; this type adds no
/// behaviour, it just names the notification-specific pass and options so the host registers
/// <c>AddHostedService&lt;NotificationWorker&gt;</c> exactly as before.
/// </summary>
/// <remarks>
/// <b>Family-agnostic by construction (ADR-IC-019 §D2/Amendment-A1).</b> The worker drives the core's generic
/// <see cref="NotificationSchedulePass"/>, which enumerates the registered family
/// <see cref="INotificationScheduleRule"/>s — it names no family and embeds no family rule. Adding a family is a
/// new module at the host edge, zero core diff. The clock-owning loop is the
/// <see cref="CadenceWorker"/> the lifecycle-command driver (ADR-PC-036) reuses, so the proven notification
/// cadence and the driver's cadence are one tested mechanism.
/// </remarks>
public sealed class NotificationWorker(
    NotificationSchedulePass schedulePass,
    NotificationSchedulerOptions options,
    TimeProvider clock,
    ILogger<NotificationWorker> logger)
    : CadenceWorker(schedulePass, options, clock, logger);

/// <summary>
/// The notification scheduler's cadence knobs — the notification-estate face of
/// <see cref="CadenceSchedulerOptions"/>, owned by the notification service, not the engine (ADR-PC-023 §6: read
/// cadence, retry and backoff are the downstream scheduler's). Notification reminders are latency-tolerant, so
/// it inherits the shared generous one-hour default <see cref="CadenceSchedulerOptions.PollInterval"/>; an
/// operator tunes it from configuration at the host composition root.
/// </summary>
public sealed class NotificationSchedulerOptions : CadenceSchedulerOptions;
