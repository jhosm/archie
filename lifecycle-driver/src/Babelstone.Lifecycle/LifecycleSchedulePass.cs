using Babelstone.Cadence;
using Microsoft.Extensions.Logging;

namespace Babelstone.Lifecycle;

/// <summary>
/// The lifecycle-command driver's per-tick engine (ADR-PC-036 §Decision 2) — the write-side twin of the
/// notification core's <c>NotificationSchedulePass</c>. In plain terms: once a tick's worth of family
/// rules have each decided "these occurrences are due today", THIS is the shared machinery that derives each
/// one's canonical number-pinned id, skips the ones already fired, POSTs the new ones to the engine, and
/// records them — so re-running a pass over the same world fires nothing twice. It enumerates every registered
/// family <see cref="ILifecycleCommandRule"/>, and for each due <see cref="LifecycleCommandDecision"/> it:
/// derives the dispatch id (<see cref="LifecycleDispatchLedger.DispatchId"/>), checks the dispatch ledger
/// (skip a re-tick), POSTs through the <see cref="ILifecycleCommandSink"/>, and — only on success — records the
/// dispatch.
/// </summary>
/// <remarks>
/// <para>
/// It is the lifecycle driver's <see cref="ISchedulePass"/> — the per-tick pass the shared clock-owning
/// <see cref="LifecycleWorker"/> (a <c>Babelstone.Cadence.CadenceWorker</c>) drives (ADR-PC-036 §Decision 2 +
/// ADR-IC-019 mechanism reuse). The clock lives one layer up, in the worker (ADR-PC-023 §6); this pass is a
/// deterministic function of the as-of date and the registered rules, so it is trivially testable with a fake
/// rule, the real <see cref="LifecycleDispatchLedger"/>, and a fake sink.
/// </para>
/// <para>
/// <b>Check-then-POST-then-record ordering (idempotent and outage-safe).</b> The dispatch ledger is consulted
/// BEFORE the POST so a re-tick costs nothing, and the dispatch is recorded only AFTER the POST succeeds — a
/// failed POST leaves the occurrence un-recorded and propagates, so the worker backs off and the next pass
/// retries it. The engine's <c>command_dedup</c> (ADR-PC-029 slot 4, <c>ENGINE_COMMAND_IDEMPOTENT</c>) makes
/// any such retry — or a crash between POST-success and record — safe: the number-pinned key dedupes to one
/// money leg. A POST failure aborts the rest of THIS pass (it bubbles to the worker as backpressure); the next
/// pass re-derives every still-due occurrence and resumes — correct backfill by construction (ADR-PC-036 §S2).
/// </para>
/// <para>
/// The interface's <see cref="ISchedulePass.RunOnceAsync"/> is satisfied explicitly by delegating to the
/// public, richer-typed <see cref="RunOnceAsync"/> below (which returns the dispatched commands for
/// callers/tests that want them); the worker discards the result and only needs the tick to run.
/// </para>
/// </remarks>
public sealed class LifecycleSchedulePass(
    IEnumerable<ILifecycleCommandRule> rules,
    LifecycleDispatchLedger dispatchLedger,
    ILifecycleCommandSink sink,
    ILogger<LifecycleSchedulePass>? logger = null) : ISchedulePass
{
    private readonly IReadOnlyList<ILifecycleCommandRule> _rules =
        (rules ?? throw new ArgumentNullException(nameof(rules))).ToList();

    private readonly LifecycleDispatchLedger _dispatchLedger =
        dispatchLedger ?? throw new ArgumentNullException(nameof(dispatchLedger));

    private readonly ILifecycleCommandSink _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    /// <summary>
    /// Run ONE driver pass as-of <paramref name="asOf"/>: ask every registered family rule which lifecycle
    /// commands are due, and for each NEW (not-yet-dispatched) occurrence derive its canonical number-pinned
    /// id, POST it through the sink, record the dispatch, and return it. Running it again over the same world
    /// returns an empty list — the dispatch ledger absorbs the re-ticks. A sink failure propagates (the worker
    /// treats it as backpressure), leaving the failed occurrence un-recorded for the next pass to retry.
    /// </summary>
    /// <param name="asOf">Today, supplied by the caller — the clock lives in the worker loop (ADR-PC-023 §6),
    /// never read here, so the pass is deterministic for a given date.</param>
    /// <param name="ct">Cancellation propagated from the host's stopping token.</param>
    public async Task<IReadOnlyList<DispatchedCommand>> RunOnceAsync(
        DateOnly asOf, CancellationToken ct = default)
    {
        var dispatched = new List<DispatchedCommand>();

        foreach (var rule in _rules)
        {
            var decisions = await rule.EvaluateAsync(asOf, ct);
            foreach (var decision in decisions)
            {
                // Skip a re-tick BEFORE touching the engine: a due occurrence keeps surfacing on the forward
                // calendar until the engine event that satisfies it lands, so an already-dispatched one is the
                // expected idempotent case — not a second firing (ADR-PC-036 §Decision 2/3).
                if (_dispatchLedger.HasDispatched(decision))
                {
                    continue;
                }

                // The dispatch id IS the engine Idempotency-Key — the SAME canonical, server-derived,
                // number-pinned value, derived the way the engine derives it (LCD-1, ADR-PC-036 §Decision 1+3).
                var commandId = LifecycleDispatchLedger.DispatchId(decision);

                // POST first; record only on success. A non-success engine response throws out of the sink and
                // bubbles to the worker as backpressure, leaving the occurrence un-recorded so the next pass
                // retries it (the engine's command_dedup makes the re-POST safe — ADR-PC-029 slot 4).
                await _sink.DispatchAsync(decision, commandId, ct);
                _dispatchLedger.RecordDispatched(decision);

                dispatched.Add(new DispatchedCommand(
                    CommandId: commandId,
                    InstanceId: decision.InstanceId,
                    CommandKind: decision.CommandKind,
                    OccurrenceKey: decision.OccurrenceKey,
                    RequestPath: decision.RequestPath,
                    DueAt: decision.DueAt));
            }
        }

        if (dispatched.Count > 0)
        {
            logger?.LogInformation(
                "Lifecycle driver dispatched {Count} due command(s) as-of {AsOf} across {RuleCount} family " +
                "rule(s) (ADR-PC-036 §Decision 2; number-pinned idempotency at command_dedup).",
                dispatched.Count, asOf, _rules.Count);
        }

        return dispatched;
    }

    /// <summary>
    /// The shared <see cref="ISchedulePass"/> tick the clock-owning <see cref="LifecycleWorker"/> drives: run
    /// one pass as-of <paramref name="asOf"/> and discard the per-tick result (the worker only needs the tick
    /// to run; callers that want the dispatched commands use the public <see cref="RunOnceAsync"/>).
    /// </summary>
    async Task ISchedulePass.RunOnceAsync(DateOnly asOf, CancellationToken ct) =>
        await RunOnceAsync(asOf, ct);
}
