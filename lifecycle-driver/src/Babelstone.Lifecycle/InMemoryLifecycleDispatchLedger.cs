namespace Babelstone.Lifecycle;

/// <summary>
/// The process-local, in-memory <see cref="ILifecycleDispatchLedger"/> — the claim-semantics TEST DOUBLE of
/// the durable <see cref="PostgresLifecycleDispatchLedger"/> (ADR-PC-038). In plain terms: it remembers
/// which occurrences this ONE process already fired (a set of number-pinned dispatch ids) and hands out
/// per-occurrence claims exactly the way the durable ledger does — already-dispatched or currently-claimed
/// yields <see langword="null"/>, a released un-recorded claim is re-claimable — so the
/// <see cref="LifecycleSchedulePass"/> is testable Docker-free against the REAL claim → POST → record
/// ordering. It is NOT the production ledger: it survives no restart and is shared by no replica (the two
/// defects ADR-PC-038 §Decision 1 fixed with the durable Postgres table); the host wires
/// <see cref="PostgresLifecycleDispatchLedger"/>.
/// </summary>
/// <remarks>
/// <b>Same two-phase discipline as production (ADR-PC-038 §Decision 3).</b> A claim is a lease, never a
/// record: <see cref="TryClaimAsync"/> reserves the id in an in-flight set, the POST runs, and only
/// <see cref="ILifecycleDispatchClaim.RecordDispatchedAsync"/> moves it to the dispatched set — disposing
/// an un-recorded claim just releases the reservation, so a failed POST leaves the occurrence claimable by
/// the next pass and the engine's <c>command_dedup</c> (ADR-PC-029 slot 4) keeps any re-POST
/// effectively-once. Thread-safe: both sets sit behind one lock so two concurrent claimants of the same
/// occurrence resolve to exactly one winner — the in-process mirror of the durable ledger's
/// <c>FOR UPDATE SKIP LOCKED</c> claim (single-firing).
/// </remarks>
public sealed class InMemoryLifecycleDispatchLedger : ILifecycleDispatchLedger
{
    private readonly HashSet<Guid> _dispatched = [];
    private readonly HashSet<Guid> _inFlight = [];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public Task<ILifecycleDispatchClaim?> TryClaimAsync(
        LifecycleCommandDecision decision, CancellationToken ct = default)
    {
        var id = LifecycleDispatchId.Of(decision);
        lock (_gate)
        {
            // Already durably recorded (the re-tick no-op) or leased to a competing claimant mid-POST
            // (skip this tick) — the same two null cases the Postgres claim resolves with its committed
            // status check + SKIP LOCKED.
            if (_dispatched.Contains(id) || !_inFlight.Add(id))
            {
                return Task.FromResult<ILifecycleDispatchClaim?>(null);
            }
        }

        return Task.FromResult<ILifecycleDispatchClaim?>(new Claim(this, id));
    }

    /// <summary>True when this occurrence's dispatch id has been recorded dispatched — a test-assertion
    /// convenience mirroring the durable table's committed <c>DISPATCHED</c> row.</summary>
    public bool HasDispatched(LifecycleCommandDecision decision)
    {
        var id = LifecycleDispatchId.Of(decision);
        lock (_gate)
        {
            return _dispatched.Contains(id);
        }
    }

    private void Commit(Guid id)
    {
        lock (_gate)
        {
            _dispatched.Add(id);
            _inFlight.Remove(id);
        }
    }

    private void Release(Guid id)
    {
        lock (_gate)
        {
            _inFlight.Remove(id);
        }
    }

    /// <summary>One held in-memory claim: record moves the id to the dispatched set (and ends the lease);
    /// disposal without recording releases the lease so the occurrence stays claimable.</summary>
    private sealed class Claim(InMemoryLifecycleDispatchLedger ledger, Guid id) : ILifecycleDispatchClaim
    {
        private bool _recorded;

        public Guid DispatchId => id;

        public Task RecordDispatchedAsync(CancellationToken ct = default)
        {
            ledger.Commit(id);
            _recorded = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_recorded)
            {
                ledger.Release(id);
            }

            return ValueTask.CompletedTask;
        }
    }
}
