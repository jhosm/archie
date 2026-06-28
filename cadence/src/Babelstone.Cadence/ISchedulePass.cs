namespace Babelstone.Cadence;

/// <summary>
/// One tick's worth of work, abstracted away from what that work is (ADR-PC-036 §Decision 2 + ADR-IC-019 §D2).
/// In plain terms: the <see cref="CadenceWorker"/> owns the clock and the loop, but it must NOT know whether a
/// tick raises customer reminders (the notification scheduler) or POSTs a due lifecycle command (the
/// ADR-PC-036 lifecycle-command driver). It just hands the pass "today" and asks it to run once. A consumer
/// implements this with its own per-tick engine (e.g. <c>NotificationSchedulePass</c> enumerating family
/// scheduling rules) and registers it; the worker drives it.
/// </summary>
/// <remarks>
/// The pass is a deterministic function of the as-of date and whatever it reads — the clock lives one layer up,
/// in <see cref="CadenceWorker"/> (ADR-PC-023 §6), never inside a pass — so it is trivially testable with a
/// fixed date and no real wall-clock wait. Idempotency is the pass's own concern (it derives a stable
/// <see cref="CompositeId"/> per unit of work and admits it past an <see cref="IDedupeLedger"/>), which is what
/// makes the worker's retry-on-failure safe: re-running a pass over the same world acts on nothing twice.
/// </remarks>
public interface ISchedulePass
{
    /// <summary>
    /// Run ONE pass as-of <paramref name="asOf"/>. The caller (the worker loop) supplies the date — the pass
    /// never reads the clock itself (ADR-PC-023 §6), so it is deterministic for a given date and a re-run over
    /// the same world is the idempotent case, not a double-action.
    /// </summary>
    /// <param name="asOf">Today, supplied by the clock-owning worker loop.</param>
    /// <param name="ct">Cancellation propagated from the host's stopping token.</param>
    Task RunOnceAsync(DateOnly asOf, CancellationToken ct = default);
}
