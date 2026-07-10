using System.Diagnostics;
using Babelstone.Cadence;
using Microsoft.Extensions.Logging;

namespace Babelstone.Orchestrator;

/// <summary>
/// The scheduled payout-landing reconciler's host shell (bd babelstone-qa92.2) — the reconciler-estate face of
/// the shared <see cref="CadenceWorker"/> (ADR-PC-036 §Decision 2 + ADR-IC-019 mechanism reuse), the exact
/// mirror of the notification estate's <c>NotificationWorker</c> and the lifecycle driver's
/// <c>LifecycleWorker</c>. A thin adapter that binds the generic clock-owning poll loop to the
/// <see cref="PayoutLandingReconciliationSchedulePass"/>. All of the loop behaviour — clock ownership
/// (ADR-PC-023 §6), the one-pass-per-tick cadence, and the backpressure/exponential-backoff retry — lives in
/// the shared <see cref="CadenceWorker"/>; this type adds no behaviour. It is not pure ceremony, though: its
/// <see cref="ILogger{T}"/> constructor parameter is what lets the host's plain
/// <c>AddHostedService&lt;PayoutLandingReconciliationWorker&gt;</c> resolve — the base <see cref="CadenceWorker"/>
/// takes a non-generic <c>ILogger</c>, which the default DI container does not register — and it gives the poll
/// loop a distinct <c>Babelstone.Orchestrator.PayoutLandingReconciliationWorker</c> log/trace category rather
/// than the generic <c>Babelstone.Cadence.CadenceWorker</c> one. (Its cadence knobs are the shared
/// <see cref="CadenceSchedulerOptions"/> bound directly — there is no reconciler-specific options subclass.)
/// </summary>
/// <remarks>
/// <b>This worker OWNS the clock; the classifier never does (ADR-PC-023 §6).</b> The loop reads the wall clock,
/// derives today, and drives the <see cref="PayoutLandingReconciliationSchedulePass"/> once per tick; the pass
/// hands that injected <c>asOf</c> straight into <see cref="PayoutLandingReconciler.Reconcile"/>, which stays
/// clock-free by construction. The clock lives HERE, in this driver shell — the same
/// <see cref="CadenceWorker"/> the notification scheduler and the lifecycle driver reuse, so all three cadences
/// are one tested mechanism. The optional injected <see cref="ActivitySource"/> is the SHARED
/// <c>Babelstone.Engine</c> source the host registers, so the per-tick <c>cadence.pass</c> span shows up in the
/// same trace surface as the engine, orchestrator saga path and notification worker (ADR-IC-007).
/// </remarks>
public sealed class PayoutLandingReconciliationWorker(
    PayoutLandingReconciliationSchedulePass schedulePass,
    CadenceSchedulerOptions options,
    TimeProvider clock,
    ILogger<PayoutLandingReconciliationWorker> logger,
    ActivitySource? activitySource = null)
    : CadenceWorker(schedulePass, options, clock, logger, activitySource);
