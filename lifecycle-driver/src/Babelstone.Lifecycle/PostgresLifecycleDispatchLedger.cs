using Npgsql;

namespace Babelstone.Lifecycle;

/// <summary>
/// The PRODUCTION dispatch ledger (ADR-PC-038 §Decision 1+2): the durable Postgres
/// <c>lifecycle_dispatch_ledger</c> table, whose per-occurrence atomic claim IS the multi-replica
/// single-firing mechanism. In plain terms: every replica ticks and finds the same due occurrences; for
/// each one this ledger first makes sure a claimable <c>PENDING</c> row exists (idempotent insert), then
/// claims that row under <c>FOR UPDATE SKIP LOCKED</c> plus a per-instance transaction advisory lock — so
/// exactly ONE replica wins the occurrence, POSTs it, and commits the <c>DISPATCHED</c> flip (with its
/// <c>dispatched_at</c> audit stamp) as the claim releases. The losers skip it this tick; a restart
/// re-reads the committed row and re-POSTs nothing. No leader is elected — the durable claim is the guard
/// (<c>LIFECYCLE_DRIVER_SINGLE_FIRING</c>, <c>LIFECYCLE_DISPATCH_LEDGER_DURABLE</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The exact competing-consumers precedent, reused (ADR-PC-038 §Decision 2).</b> The claim query is the
/// saga dispatcher's (<c>SagaCommandDispatchDrainer.ClaimAsync</c>, ADR-PC-029 slot 3) and the outbox
/// relay's (<c>OutboxDrainer</c>, ADR-IC-004): re-check the committed status under
/// <c>FOR UPDATE SKIP LOCKED</c> so a concurrent claimant is stepped over (never blocked on), inside a
/// transaction-scoped <c>pg_try_advisory_xact_lock(hashtextextended(instance_id, salt))</c> that
/// serialises two replicas on the SAME instance (per-instance ordering of recurring occurrences) while
/// different instances claim in parallel. The salt namespaces this component's advisory-lock key space so
/// it cannot collide with the saga FIFO guard's or the outbox seed's on a shared cluster (ADR-PC-038
/// §Residual risks).
/// </para>
/// <para>
/// <b>Claim → POST → record, crash-safe (ADR-PC-038 §Decision 3).</b> The claim transaction stays OPEN
/// across the caller's fallible POST (the lease); <see cref="ILifecycleDispatchClaim.RecordDispatchedAsync"/>
/// flips the row <c>DISPATCHED</c> and commits — record and release are one atomic commit. Disposing an
/// un-recorded claim rolls back: the row stays <c>PENDING</c> and re-claimable (the advisory lock is
/// xact-scoped, so a crash releases it when the backend connection drops). The pre-claim <c>PENDING</c>
/// insert is NOT a reserve-before-POST — a <c>PENDING</c> row records only "seen due", which the forward
/// calendar already re-derives every tick, so nothing can strand; the engine's <c>command_dedup</c>
/// (ADR-PC-029 slot 4) remains the correctness floor for any re-POST this ledger fails to suppress.
/// </para>
/// <para>
/// <b>PII-free by construction (ADR-PC-004 §P2).</b> A row carries only structural references: the
/// number-pinned dispatch id, the instance id, the command-kind code, the occurrence number, the due date,
/// and DB-clock timestamps. Never a name, NIF, IBAN, account number, or amount.
/// </para>
/// </remarks>
public sealed class PostgresLifecycleDispatchLedger(string connectionString) : ILifecycleDispatchLedger
{
    /// <summary>
    /// The namespace SEED of the per-instance claim advisory lock (ADR-PC-038 §Decision 2 + §Residual
    /// risks). Passed as <c>hashtextextended(instance_id::text, ClaimLockSalt)</c> it derives a single
    /// 64-bit advisory-lock key from the target instance while namespacing THIS component's key space —
    /// two components hashing the same text with different seeds land on different keys, so a lifecycle
    /// claim lock cannot collide with the saga dispatcher's FIFO guard (<c>FifoLockSalt</c>) or the outbox
    /// relay's per-aggregate lock on the same cluster. An arbitrary but STABLE value — only its
    /// reservation for this guard matters.
    /// </summary>
    public const long ClaimLockSalt = 0x4C49_4645_434C_4D31L; // 'LIFECLM1' — lifecycle dispatch-claim seed.

    private readonly string _connectionString =
        connectionString ?? throw new ArgumentNullException(nameof(connectionString));

    /// <inheritdoc />
    public async Task<ILifecycleDispatchClaim?> TryClaimAsync(
        LifecycleCommandDecision decision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var dispatchId = LifecycleDispatchId.Of(decision);

        var connection = new NpgsqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);

            // Phase 1 (autocommit, idempotent): make sure a claimable PENDING row exists for this
            // occurrence. ON CONFLICT DO NOTHING — a row already there (PENDING from an earlier failed
            // POST, or DISPATCHED) is left untouched. Kept OUTSIDE the claim transaction so a competing
            // replica's insert conflicts against a COMMITTED row (a millisecond uniqueness wait at worst),
            // never blocks for the length of an open claim's POST.
            const string ensureSql = """
                INSERT INTO lifecycle_dispatch_ledger
                    (dispatch_id, instance_id, command_kind, occurrence_key, due_at)
                VALUES (@dispatch_id, @instance_id, @command_kind, @occurrence_key, @due_at)
                ON CONFLICT (dispatch_id) DO NOTHING;
                """;
            await using (var ensure = new NpgsqlCommand(ensureSql, connection))
            {
                ensure.Parameters.AddWithValue("dispatch_id", dispatchId);
                ensure.Parameters.AddWithValue("instance_id", decision.InstanceId);
                ensure.Parameters.AddWithValue("command_kind", decision.CommandKind);
                ensure.Parameters.AddWithValue("occurrence_key", decision.OccurrenceKey);
                ensure.Parameters.AddWithValue("due_at", decision.DueAt);
                await ensure.ExecuteNonQueryAsync(ct);
            }

            // Phase 2: the atomic claim — the single-firing guard (ADR-PC-038 §Decision 2). Re-check the
            // COMMITTED status under FOR UPDATE SKIP LOCKED inside the per-instance advisory lock:
            //   • status = 'DISPATCHED'  → no row returned → null (the durable re-tick/restart no-op);
            //   • row locked by a peer   → SKIP LOCKED steps over it → null (the peer is mid-POST);
            //   • advisory lock held     → pg_try returns false, the row filters out → null (two replicas
            //     on the SAME instance serialise; different instances proceed in parallel).
            // The winner holds the row lock in this OPEN transaction across the caller's POST.
            var transaction = await connection.BeginTransactionAsync(ct);
            const string claimSql = """
                SELECT dispatch_id
                FROM lifecycle_dispatch_ledger
                WHERE dispatch_id = @dispatch_id AND status = 'PENDING'
                  AND pg_try_advisory_xact_lock(hashtextextended(@instance_text, @claim_salt))
                FOR UPDATE SKIP LOCKED;
                """;
            await using (var claim = new NpgsqlCommand(claimSql, connection, transaction))
            {
                claim.Parameters.AddWithValue("dispatch_id", dispatchId);
                claim.Parameters.AddWithValue("instance_text", decision.InstanceId.ToString("D"));
                claim.Parameters.AddWithValue("claim_salt", ClaimLockSalt);
                var won = await claim.ExecuteScalarAsync(ct) is not null;
                if (!won)
                {
                    await transaction.DisposeAsync();
                    await connection.DisposeAsync();
                    return null;
                }
            }

            var held = new PostgresDispatchClaim(connection, transaction, dispatchId);
            connection = null!; // ownership transferred to the claim — the finally must not dispose it.
            return held;
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// One held row claim: the open transaction whose row lock (+ xact advisory lock) IS the lease.
    /// <see cref="RecordDispatchedAsync"/> flips the row <c>DISPATCHED</c> with its DB-clock
    /// <c>dispatched_at</c> audit stamp and COMMITS — record and release in one atomic commit. Disposal
    /// without recording rolls the transaction back (the row stays <c>PENDING</c>, immediately
    /// re-claimable), which is also what a process crash effects when the backend connection drops.
    /// </summary>
    private sealed class PostgresDispatchClaim(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid dispatchId) : ILifecycleDispatchClaim
    {
        private bool _recorded;

        public Guid DispatchId => dispatchId;

        public async Task RecordDispatchedAsync(CancellationToken ct = default)
        {
            // dispatched_at is DB-stamped (clock_timestamp()) — single-clock, like the saga outbox's
            // published_at — so the audit trail cannot be skewed by a replica's wall clock.
            const string recordSql = """
                UPDATE lifecycle_dispatch_ledger
                SET status = 'DISPATCHED', dispatched_at = clock_timestamp()
                WHERE dispatch_id = @dispatch_id;
                """;
            await using (var record = new NpgsqlCommand(recordSql, connection, transaction))
            {
                record.Parameters.AddWithValue("dispatch_id", dispatchId);
                await record.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            _recorded = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_recorded)
            {
                // Un-recorded claim → roll back: the row stays PENDING and the advisory lock releases,
                // so the occurrence is re-claimable by the next pass (ADR-PC-038 §Decision 3).
                await transaction.RollbackAsync();
            }

            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
