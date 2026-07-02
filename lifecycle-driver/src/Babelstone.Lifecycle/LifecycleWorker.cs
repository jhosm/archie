using System.Diagnostics;
using Babelstone.Cadence;
using Microsoft.Extensions.Logging;

namespace Babelstone.Lifecycle;

/// <summary>
/// The lifecycle-command driver's host shell — the driver-estate face of the shared
/// <see cref="CadenceWorker"/> (ADR-PC-036 §Decision 2 + ADR-IC-019 mechanism reuse), the exact mirror of the
/// notification estate's <c>NotificationWorker</c>. A thin adapter that binds the generic clock-owning poll
/// loop to the <see cref="LifecycleSchedulePass"/>. All of the loop behaviour — clock ownership (ADR-PC-023
/// §6), the one-pass-per-tick cadence, and the backpressure/exponential-backoff retry — lives in the shared
/// <see cref="CadenceWorker"/>; this type adds no behaviour. It is not pure ceremony, though: its
/// <see cref="ILogger{T}"/> constructor parameter is what lets the host's plain
/// <c>AddHostedService&lt;LifecycleWorker&gt;</c> resolve — the base <see cref="CadenceWorker"/> takes a
/// non-generic <c>ILogger</c>, which the default DI container does not register — and it gives the poll loop a
/// distinct <c>Babelstone.Lifecycle.LifecycleWorker</c> log/trace category rather than the generic
/// <c>Babelstone.Cadence.CadenceWorker</c> one. (Its cadence knobs are the shared
/// <see cref="CadenceSchedulerOptions"/> bound directly — there is no lifecycle-specific options subclass.)
/// </summary>
/// <remarks>
/// <b>This worker OWNS the clock; the engine never does (ADR-PC-023 §6, NO_CLOCK_DRIVEN_ENGINE_SIGNAL).</b> The
/// loop reads the wall clock, derives today, and drives the <see cref="LifecycleSchedulePass"/> once per tick;
/// the pass enumerates the registered family <see cref="ILifecycleCommandRule"/>s and POSTs each due command to
/// the engine's ADR-PC-029 surface. The clock lives HERE, in a downstream sibling host — never inside the
/// engine assembly (a timer there trips BENG004) and never inside the read-only notification context. The
/// clock-owning loop is the same <see cref="CadenceWorker"/> the notification scheduler reuses, so the proven
/// notification cadence and the driver's cadence are one tested mechanism.
/// </remarks>
public sealed class LifecycleWorker(
    LifecycleSchedulePass schedulePass,
    CadenceSchedulerOptions options,
    TimeProvider clock,
    ILogger<LifecycleWorker> logger,
    // The SHARED Babelstone.Engine ActivitySource the host registers (from Babelstone.Telemetry) so the base
    // CadenceWorker opens its per-tick `cadence.pass` span on the one estate-wide source — the driver's ticks
    // show up in the same trace surface as the engine, orchestrator and notification worker (ADR-IC-007).
    ActivitySource activitySource)
    : CadenceWorker(schedulePass, options, clock, logger, activitySource);
