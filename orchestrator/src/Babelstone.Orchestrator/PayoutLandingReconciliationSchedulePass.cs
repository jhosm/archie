using Babelstone.Cadence;
using Microsoft.Extensions.Logging;

namespace Babelstone.Orchestrator;

/// <summary>
/// The scheduled payout-landing reconciler's per-tick engine (bd babelstone-qa92.2; ADR-PC-043) — the driver
/// that finally RUNS the safety net. In plain terms: <see cref="PayoutLandingReconciler"/> already knows how to
/// classify a payout as matched / in-flight / dropped / doubled / wrong-amount / orphan-landing, but nothing
/// called it in production. THIS pass does, once per tick: it reads the source payouts and the CA landings
/// as-of today, reconciles them, and surfaces every non-matched signal to the operator sink so a human sees a
/// drop, a double, or a wrong-amount landing. It NEVER invents or auto-corrects a Movement — it emits SIGNAL
/// ONLY (ADR-PC-043 reconcile-signals-only).
/// </summary>
/// <remarks>
/// <para>
/// It is the reconciler estate's <see cref="ISchedulePass"/> — the per-tick pass the shared clock-owning
/// <see cref="CadenceWorker"/> drives (ADR-PC-036 §Decision 2 + ADR-IC-019 mechanism reuse), the exact shape of
/// the notification core's <c>NotificationSchedulePass</c> and the lifecycle driver's
/// <c>LifecycleSchedulePass</c>. The clock lives one layer up, in the worker (ADR-PC-023 §6): this pass is a
/// deterministic function of the injected <c>asOf</c> date and whatever the
/// <see cref="IPayoutLandingSource"/> returns, so it is trivially testable with a fake source, a fixed date,
/// and a capturing sink — no real wall-clock wait.
/// </para>
/// <para>
/// <b>Clock-free at the classifier boundary (ADR-PC-023 §6).</b> The pass hands the reconciler the same
/// injected <c>asOf</c> it received — the DROP SLA age is measured against that date, never a clock read inside
/// <see cref="PayoutLandingReconciler.Reconcile"/>. So the classification is deterministic for a given date and
/// a re-run over the same world raises the same signals: re-emitting a counter/log is the idempotent
/// at-least-once case (a rate an operator reads), never a second correction — there is no correction to double.
/// </para>
/// <para>
/// <b>Signal only, backpressure-safe.</b> A sink failure propagates to the worker as backpressure (the worker
/// backs off and the next pass re-reads the same world and re-emits); nothing is lost because nothing is a
/// durable side effect the retry could double — the reconciler moves no money (ADR-PC-043). The tick-liveness
/// heartbeat (<see cref="PayoutReconciliationMetrics.RecordPassCompleted"/>) refreshes only on a pass that ran
/// to completion, so a pass that threw leaves the heartbeat stale — exactly the signal the
/// <c>PayoutReconciliationTickStale</c> alert reads.
/// </para>
/// </remarks>
public sealed class PayoutLandingReconciliationSchedulePass(
    IPayoutLandingSource source,
    IReconciliationSignalSink sink,
    int? dropSlaDays = null,
    ILogger<PayoutLandingReconciliationSchedulePass>? logger = null) : ISchedulePass
{
    private readonly IPayoutLandingSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly IReconciliationSignalSink _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    /// <summary>
    /// Run ONE reconciliation pass as-of <paramref name="asOf"/>: read the source payouts and CA landings
    /// as-of that date, reconcile them (<see cref="PayoutLandingReconciler.Reconcile"/>), and surface every
    /// non-matched <see cref="ReconciliationSignal"/> to the sink. Returns the outcomes for callers/tests that
    /// want them; the worker discards the result. Running it again over the same world re-derives the same
    /// outcomes and re-emits the same signals — idempotent by construction (a counter/log re-emit is the
    /// at-least-once case, never a doubled correction; the reconciler moves no money, ADR-PC-043).
    /// </summary>
    /// <param name="asOf">Today, supplied by the caller — the clock lives in the worker loop (ADR-PC-023 §6),
    /// never read here nor inside the classifier, so the pass is deterministic for a given date.</param>
    /// <param name="ct">Cancellation propagated from the host's stopping token.</param>
    public async Task<IReadOnlyList<ReconciliationOutcome>> RunOnceAsync(
        DateOnly asOf, CancellationToken ct = default)
    {
        var sourcePayouts = await _source.ReadSourcePayoutsAsync(asOf, ct);
        var caLandings = await _source.ReadCaLandingsAsync(asOf, ct);

        // Inject asOf into the classifier — never a clock read inside it (ADR-PC-023 §6). The DROP SLA horizon
        // is the reconciler's DefaultDropSlaDays unless the host overrode it (dropSlaDays), so the interim
        // Q-AG-pending SLA is a single configured value, not a literal restated here.
        var outcomes = PayoutLandingReconciler.Reconcile(sourcePayouts, caLandings, asOf, dropSlaDays);

        var signalled = 0;
        foreach (var outcome in outcomes)
        {
            // A matched pair carries no signal (Signal is null exactly when Matched); an in-SLA InFlight also
            // carries none. Only the non-matched cases (Drop / Double / WrongAmount / OrphanLanding) reach the
            // operator sink — SURFACED, never corrected (ADR-PC-043 reconcile-signals-only).
            if (outcome.Signal is not null)
            {
                await _sink.EmitAsync(outcome.Signal, ct);
                signalled++;
            }
        }

        if (signalled > 0)
        {
            logger?.LogInformation(
                "Payout-landing reconciliation pass surfaced {Signalled} non-matched signal(s) as-of {AsOf} " +
                "across {Outcomes} reconciled intent(s) (ADR-PC-043 — signal only, no Movement invented).",
                signalled, asOf, outcomes.Count);
        }

        // The tick-liveness heartbeat: this pass ran to COMPLETION — every intent reconciled, every non-matched
        // signal emitted. A pass that threw above (a source read or a sink emit failing) never reaches this, so
        // the heartbeat goes stale while the worker backs off — exactly the PayoutReconciliationTickStale signal.
        PayoutReconciliationMetrics.RecordPassCompleted();

        return outcomes;
    }

    /// <summary>
    /// The shared <see cref="ISchedulePass"/> tick the clock-owning <see cref="CadenceWorker"/> drives: run one
    /// pass as-of <paramref name="asOf"/> and discard the per-tick result (the worker only needs the tick to
    /// run; callers that want the outcomes use the public <see cref="RunOnceAsync"/>).
    /// </summary>
    async Task ISchedulePass.RunOnceAsync(DateOnly asOf, CancellationToken ct) =>
        await RunOnceAsync(asOf, ct);
}
