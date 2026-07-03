using Babelstone.Cadence;
using Microsoft.Extensions.Logging;

namespace Babelstone.Lifecycle;

/// <summary>
/// The lifecycle-command driver's per-tick engine (ADR-PC-036 §Decision 2) — the write-side twin of the
/// notification core's <c>NotificationSchedulePass</c>. In plain terms: once a tick's worth of family
/// rules have each decided "these occurrences are due today", THIS is the shared machinery that derives each
/// one's canonical number-pinned id, claims it on the dispatch ledger (so of N replicas exactly ONE fires
/// it), POSTs the claimed ones to the engine, and records them — so re-running a pass over the same world,
/// on this replica or any other, fires nothing twice. It enumerates every registered family
/// <see cref="ILifecycleCommandRule"/>, and for each due <see cref="LifecycleCommandDecision"/> it:
/// derives the dispatch id (<see cref="LifecycleDispatchId.Of"/>), claims the occurrence on the
/// <see cref="ILifecycleDispatchLedger"/> (skip a re-tick, an already-dispatched row, or a peer's in-flight
/// claim), POSTs through the <see cref="ILifecycleCommandSink"/>, and — only on success — records the
/// dispatch as the claim commits.
/// </summary>
/// <remarks>
/// <para>
/// It is the lifecycle driver's <see cref="ISchedulePass"/> — the per-tick pass the shared clock-owning
/// <see cref="LifecycleWorker"/> (a <c>Babelstone.Cadence.CadenceWorker</c>) drives (ADR-PC-036 §Decision 2 +
/// ADR-IC-019 mechanism reuse). The clock lives one layer up, in the worker (ADR-PC-023 §6); this pass is a
/// deterministic function of the as-of date, the registered rules, and the ledger's claim answers, so it is
/// trivially testable with a fake rule, the <see cref="InMemoryLifecycleDispatchLedger"/>, and a fake sink.
/// </para>
/// <para>
/// <b>Claim-then-POST-then-record ordering (single-firing, idempotent and outage-safe — ADR-PC-038
/// §Decision 2+3).</b> The ledger claim is taken BEFORE the POST: a re-tick, a restart, or a competing
/// replica gets no claim and skips the occurrence (with the durable
/// <see cref="PostgresLifecycleDispatchLedger"/>, that claim is the <c>FOR UPDATE SKIP LOCKED</c>
/// competing-consumers guard — <c>LIFECYCLE_DRIVER_SINGLE_FIRING</c>). The dispatch is recorded only AFTER
/// the POST succeeds — a failed POST releases the un-recorded claim and propagates, so the worker backs off
/// and the next pass retries it. The engine's <c>command_dedup</c> (ADR-PC-029 slot 4,
/// <c>ENGINE_COMMAND_IDEMPOTENT</c>) makes any such retry — or a crash between POST-success and record —
/// safe: the number-pinned key dedupes to one money leg. A POST failure aborts the rest of THIS pass (it
/// bubbles to the worker as backpressure); the next pass re-derives every still-due occurrence and resumes —
/// correct backfill by construction (ADR-PC-036 §S2).
/// </para>
/// <para>
/// The interface's <see cref="ISchedulePass.RunOnceAsync"/> is satisfied explicitly by delegating to the
/// public, richer-typed <see cref="RunOnceAsync"/> below (which returns the dispatched commands for
/// callers/tests that want them); the worker discards the result and only needs the tick to run.
/// </para>
/// </remarks>
public sealed class LifecycleSchedulePass(
    IEnumerable<ILifecycleCommandRule> rules,
    ILifecycleDispatchLedger dispatchLedger,
    ILifecycleCommandSink sink,
    ILogger<LifecycleSchedulePass>? logger = null) : ISchedulePass
{
    private readonly IReadOnlyList<ILifecycleCommandRule> _rules =
        (rules ?? throw new ArgumentNullException(nameof(rules))).ToList();

    private readonly ILifecycleDispatchLedger _dispatchLedger =
        dispatchLedger ?? throw new ArgumentNullException(nameof(dispatchLedger));

    private readonly ILifecycleCommandSink _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    /// <summary>
    /// Run ONE driver pass as-of <paramref name="asOf"/>: ask every registered family rule which lifecycle
    /// commands are due, and for each occurrence this pass WINS the ledger claim on, POST it through the
    /// sink, record the dispatch, and return it. Running it again over the same world returns an empty
    /// list — the dispatch ledger absorbs the re-ticks, and (durably) the restarts and the competing
    /// replicas. A sink failure propagates (the worker treats it as backpressure), releasing the failed
    /// occurrence's un-recorded claim for the next pass to retry.
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
                // Claim BEFORE touching the engine (ADR-PC-038 §Decision 2): a due occurrence keeps
                // surfacing on the forward calendar until the engine event that satisfies it lands, so a
                // null claim is the expected idempotent case — already dispatched (this replica, another
                // replica, or before a restart) or claimed by a peer mid-POST — never a second firing.
                await using var claim = await _dispatchLedger.TryClaimAsync(decision, ct);
                if (claim is null)
                {
                    continue;
                }

                // The dispatch id IS the engine Idempotency-Key — the SAME canonical, server-derived,
                // number-pinned value, derived the way the engine derives it (LCD-1, ADR-PC-036
                // §Decision 1+3), and the ledger row's claim key (ADR-PC-038 §Decision 1).
                var commandId = claim.DispatchId;

                // POST while holding the claim; record only on success. A non-success engine response
                // throws out of the sink and bubbles to the worker as backpressure — the claim disposes
                // UN-recorded, releasing the occurrence so the next pass retries it (the engine's
                // command_dedup makes the re-POST safe — ADR-PC-029 slot 4). The failure is counted
                // (lifecycle_dispatch_failure_total — a sustained rate is the "money-mover cannot reach
                // the engine" page, bd babelstone-1nkm.4) before it propagates.
                try
                {
                    await _sink.DispatchAsync(decision, commandId, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LifecycleDriverMetrics.RecordDispatchFailed(decision.CommandKind);
                    throw;
                }

                // Commit the durable dispatched record in the same stroke that releases the claim
                // (ADR-PC-038 §Decision 3): a crash between the POST's 2xx and this commit leaves the
                // occurrence re-claimable, and the engine dedupes the re-POST — effectively-once.
                await claim.RecordDispatchedAsync(ct);

                // The throughput + lag signals (bd babelstone-1nkm.4), recorded in this impure driver
                // shell (OBS-2) with structural tags only: one dispatch, and how late after its business
                // due date it landed.
                LifecycleDriverMetrics.RecordDispatched(decision.CommandKind, decision.DueAt);

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

        // The tick-liveness heartbeat (bd babelstone-1nkm.4): this pass ran to COMPLETION — every rule
        // evaluated, every claimed occurrence recorded or released. A pass that threw above never reaches
        // this, so the heartbeat goes stale while the worker backs off — exactly the signal the
        // LifecycleDriverTickStale alert reads.
        LifecycleDriverMetrics.RecordPassCompleted();

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
