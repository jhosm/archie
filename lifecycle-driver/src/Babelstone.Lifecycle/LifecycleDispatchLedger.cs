using Babelstone.Engine.Hosting;

namespace Babelstone.Lifecycle;

/// <summary>
/// The driver's "already fired this occurrence" memory (ADR-PC-036 §Decision 2) — what makes a re-tick of an
/// already-dispatched lifecycle command a no-op. In plain terms: the forward calendar keeps surfacing a due
/// occurrence on every poll until the engine event that satisfies it lands, so without this ledger the driver
/// would re-POST the same maturity/installment every hour. The ledger records the canonical, SERVER-DERIVED,
/// number-pinned dispatch id of each occurrence it has successfully POSTed, so a later tick that re-derives the
/// same id skips it. The engine's own <c>command_dedup</c> (ADR-PC-029 slot 4) is the AUTHORITATIVE
/// idempotency backstop — this ledger is the cheap front-line that keeps the driver from hammering the engine
/// with a re-POST every tick.
/// </summary>
/// <remarks>
/// <para>
/// <b>The dispatch id IS the engine idempotency key.</b> Both are <see cref="LifecycleCommandKey"/>.Derive over
/// <c>(instance_id, command_kind, stable_occurrence_key)</c> — referenced from the engine hosting seam, derived
/// the SAME way the engine derives it (LCD-1, ADR-PC-036 §Decision 1+3). So the value this ledger dedupes on is
/// identical to the <c>Idempotency-Key</c> the sink presents and to the key the engine derives server-side —
/// the driver, a manual operator and the MCP agent all converge on ONE id per occurrence. The id is
/// NUMBER-PINNED: the recurring occurrence key is the stable installment NUMBER, never the due-date, so a
/// re-dated or backfilled retry of occurrence N re-derives the SAME id and dedupes to one firing
/// (<c>LIFECYCLE_COMMAND_NUMBER_PINNED_IDEMPOTENT</c>).
/// </para>
/// <para>
/// <b>Two-phase (check, then record-after-success), deliberately.</b> The POST is a FALLIBLE side effect that
/// must precede the commit: the pass checks <see cref="HasDispatched"/> first (skip a re-tick before touching
/// the engine), POSTs, and only on success calls <see cref="RecordDispatched"/>. A POST failure leaves the
/// occurrence UN-recorded, so the next pass retries it — and the engine's <c>command_dedup</c> makes that
/// re-POST safe. (This is why the ledger owns its own membership set rather than reusing the generic
/// single-method <c>Babelstone.Cadence.IDedupeLedger</c>, whose atomic test-and-set <c>TryReserveAsync</c> would
/// reserve-before-POST and so silently strand an occurrence on a transient failure. The driver reuses
/// Babelstone.Cadence for the heart of the machinery — the clock-owning worker and the per-tick pass — and the
/// shared LifecycleCommandKey for the id; the fallible-sink ordering is the one place a bespoke ledger earns
/// its keep.) Thread-safe: a single set behind a lock — a single worker runs one pass at a time, but the lock
/// keeps a future concurrent or durable ledger honest. In-memory for v1; a durable, crash-surviving ledger is a
/// later operating concern (the host owns single-firing/leader-election + a durable ledger as it hardens —
/// ADR-PC-036 §Consequences), and on a restart the empty ledger simply re-derives every still-due occurrence
/// from the calendar, the engine deduping any already-applied one.
/// </para>
/// </remarks>
public sealed class LifecycleDispatchLedger
{
    private readonly HashSet<Guid> _dispatched = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// The canonical, server-derived, number-pinned dispatch id for one due occurrence — the engine
    /// <c>Idempotency-Key</c> the sink presents AND this ledger's dedupe key, the SAME value, via
    /// <see cref="LifecycleCommandKey"/>.Derive over <c>(instance_id, command_kind, stable_occurrence_key)</c>
    /// (ADR-PC-036 §Decision 1+3, LCD-1). Pure: the same decision identity always yields the same id.
    /// </summary>
    public static Guid DispatchId(LifecycleCommandDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return LifecycleCommandKey.Derive(decision.InstanceId, decision.CommandKind, decision.OccurrenceKey);
    }

    /// <summary>
    /// True when this occurrence's dispatch id has already been recorded dispatched — the re-tick a later pass
    /// must skip BEFORE POSTing. A non-mutating check (the record happens only after a successful POST, via
    /// <see cref="RecordDispatched"/>), so a not-yet-recorded occurrence is never falsely suppressed.
    /// </summary>
    public bool HasDispatched(LifecycleCommandDecision decision)
    {
        var id = DispatchId(decision);
        lock (_gate)
        {
            return _dispatched.Contains(id);
        }
    }

    /// <summary>
    /// Record this occurrence's dispatch id as fired — called by the pass ONLY after the sink's POST succeeds,
    /// so a subsequent tick's <see cref="HasDispatched"/> returns the occurrence as already-dispatched and
    /// skips it. Idempotent: recording an id already present is a harmless no-op.
    /// </summary>
    public void RecordDispatched(LifecycleCommandDecision decision)
    {
        var id = DispatchId(decision);
        lock (_gate)
        {
            _dispatched.Add(id);
        }
    }
}
