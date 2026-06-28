using Babelstone.Cadence;
using Microsoft.Extensions.Logging;

namespace Babelstone.Notification;

/// <summary>
/// The notification service's host shell — the notification-estate face of the shared
/// <see cref="CadenceWorker"/> (ADR-PC-036 §Decision 2 + ADR-IC-019 mechanism reuse). A thin adapter that binds
/// the generic clock-owning poll loop to the family-agnostic <see cref="NotificationSchedulePass"/>. All of the
/// loop behaviour — clock ownership (ADR-PC-023 §6), the one-pass-per-tick cadence, and the
/// backpressure/exponential-backoff retry — lives in the shared <see cref="CadenceWorker"/>; this type adds no
/// behaviour. It is not pure ceremony, though: its <see cref="ILogger{T}"/> constructor parameter is what lets
/// the host's plain <c>AddHostedService&lt;NotificationWorker&gt;</c> resolve — the base <see cref="CadenceWorker"/>
/// takes a non-generic <c>ILogger</c>, which the default DI container does not register — and it gives the poll
/// loop a distinct <c>Babelstone.Notification.NotificationWorker</c> log/trace category rather than the generic
/// <c>Babelstone.Cadence.CadenceWorker</c> one. (Its cadence knobs are the shared
/// <see cref="CadenceSchedulerOptions"/> bound directly — there is no notification-specific options subclass.)
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
    CadenceSchedulerOptions options,
    TimeProvider clock,
    ILogger<NotificationWorker> logger)
    : CadenceWorker(schedulePass, options, clock, logger);
