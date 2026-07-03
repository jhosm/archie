using Npgsql;

namespace Babelstone.EventStore;

/// <summary>
/// One registered bulk-operation job — the frozen-universe header of ADR-PC-035 §P1, flattened to
/// family-agnostic PRIMITIVES (the storage boundary names no engine domain type, the same split as
/// <see cref="MovementLedgerEntry"/> / <see cref="AccountHoldRow"/>). The <see cref="JobId"/> IS
/// the <c>action_id</c>: the per-instance command id derives deterministically from
/// <c>(job_id, instance_id)</c> (§P3), so a retried/restarted step never double-appends.
/// </summary>
/// <param name="JobId">The job's identity and the action id (migration 0018).</param>
/// <param name="OperationKind">The adapter key (e.g. <c>PackVersionMigrated</c>) — an open set, never a family name.</param>
/// <param name="MatchedSetJson">The frozen matched-set predicate/snapshot, opaque JSON — the audit record of what this plan targeted.</param>
/// <param name="RequestedBatchSize">The drainer's bounded claim size (§P2 — the re-homed PR #324 cap).</param>
/// <param name="TotalCount">The size of the frozen universe, set at registration (the matched_count).</param>
/// <param name="Actor">The registering operator — a structural actor token, never PII.</param>
/// <param name="Status"><c>REGISTERED → DRAINING → COMPLETED | FAILED | CANCELLED</c>.</param>
/// <param name="CreatedAt">When the job was registered (DB clock).</param>
/// <param name="StartedAt">When draining began; null until the first claim.</param>
/// <param name="CompletedAt">When a terminal status was reached; null until then.</param>
public sealed record BulkOperationJobRow(
    Guid JobId,
    string OperationKind,
    string MatchedSetJson,
    int RequestedBatchSize,
    long TotalCount,
    string Actor,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null);

/// <summary>
/// One instance in a job's frozen universe (ADR-PC-035 §P2/§P4/§P5) — the per-item work-queue row
/// of migration 0018. <see cref="ItemParamsJson"/> / <see cref="PreconditionInputJson"/> are opaque
/// JSON the operation's adapter reads; the spine never parses them.
/// </summary>
/// <param name="TargetId">The row's stable identity.</param>
/// <param name="JobId">The owning frozen set.</param>
/// <param name="InstanceId">The opaque product-instance (stream) reference — never PII.</param>
/// <param name="Status"><c>PENDING → APPLIED | SKIPPED | FAILED</c>.</param>
/// <param name="ItemParamsJson">Optional per-item params for the adapter's event factory; frozen at registration.</param>
/// <param name="PreconditionInputJson">Optional per-item input to the adapter's precondition; frozen at registration.</param>
/// <param name="Attempts">How many times the drainer has claimed this row (a stuck-row signal).</param>
/// <param name="FailureReason">Set on FAILED — operational-tier only, never PII (ADR-PC-004).</param>
/// <param name="CommitSequence">Set on APPLIED — the appended event's per-stream head (the receipt).</param>
/// <param name="ClaimedAt">When a drainer last claimed the row; observability only.</param>
/// <param name="ProcessedAt">When the terminal outcome was recorded.</param>
/// <param name="CreatedAt">When the row was frozen into the universe.</param>
public sealed record BulkOperationTargetRow(
    Guid TargetId,
    Guid JobId,
    Guid InstanceId,
    string Status,
    string? ItemParamsJson,
    string? PreconditionInputJson,
    int Attempts,
    string? FailureReason,
    long? CommitSequence,
    DateTimeOffset? ClaimedAt,
    DateTimeOffset? ProcessedAt,
    DateTimeOffset CreatedAt);

/// <summary>
/// The terminal outcome of one per-instance step (ADR-PC-035 §P5), recorded by the drainer inside
/// the claim transaction: <c>APPLIED</c> (event appended, with the commit-sequence receipt),
/// <c>SKIPPED</c> (the precondition declined), or <c>FAILED</c> (an error — recorded with an
/// operational-tier reason and left for selective retry).
/// </summary>
public sealed record BulkTargetOutcome(string Status, long? CommitSequence = null, string? FailureReason = null)
{
    public static BulkTargetOutcome Applied(long commitSequence) => new("APPLIED", commitSequence);

    public static BulkTargetOutcome Skipped() => new("SKIPPED");

    public static BulkTargetOutcome Failed(string reason) => new("FAILED", FailureReason: reason);
}

/// <summary>The by-query progress tuple of ADR-PC-035 §P5/§P6: counts over the frozen set.</summary>
public sealed record BulkOperationProgress(
    long Total, long Applied, long Skipped, long Failed, long Pending);

/// <summary>
/// The generic, family-agnostic storage boundary for the bulk-operations work-tables
/// (ADR-PC-035, migration 0018). Registration freezes the universe transactionally (§P1); the
/// drainer claims bounded <c>FOR UPDATE SKIP LOCKED</c> batches and flips per-item status inside
/// one transaction (§P2/§P5); progress, selective retry, and cancel are queries/flips over the
/// same tables (§P5/§P6). Family-agnostic by construction — opaque ids and JSON only.
/// </summary>
public interface IBulkOperationStore
{
    /// <summary>
    /// Register a job: the header and its one-row-per-instance frozen target set land in ONE
    /// transaction (§P1) — together or not at all. The set is immutable from here on.
    /// </summary>
    Task RegisterAsync(
        BulkOperationJobRow job, IReadOnlyList<BulkOperationTargetRow> targets, CancellationToken ct = default);

    /// <summary>The jobs with outstanding work (<c>REGISTERED</c> or <c>DRAINING</c>), oldest first.</summary>
    Task<IReadOnlyList<BulkOperationJobRow>> ReadActiveJobsAsync(CancellationToken ct = default);

    /// <summary>Flip a <c>REGISTERED</c> job to <c>DRAINING</c>, stamping <c>started_at</c>; a no-op if already past it.</summary>
    Task MarkDrainingAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Claim one bounded batch of the job's <c>PENDING</c> targets with
    /// <c>FOR UPDATE SKIP LOCKED</c>, run <paramref name="perItem"/> for each, and record every
    /// outcome — all inside ONE transaction (§P2/§P5). The per-instance append inside
    /// <paramref name="perItem"/> commits on its OWN connection (the engine's native path), so a
    /// crash mid-batch rolls the claim back to <c>PENDING</c> while the §P3 deterministic command
    /// id makes the re-claimed step a no-op append. Returns the number of rows claimed
    /// (0 = nothing pending or every pending row was locked by a concurrent drainer).
    /// </summary>
    Task<int> DrainBatchAsync(
        Guid jobId,
        int batchSize,
        Func<BulkOperationTargetRow, Task<BulkTargetOutcome>> perItem,
        CancellationToken ct = default);

    /// <summary>
    /// Flip a <c>DRAINING</c> job with no remaining <c>PENDING</c> targets to <c>COMPLETED</c>
    /// (stamping <c>completed_at</c>). Returns whether the flip happened. A job whose only
    /// non-applied items are <c>FAILED</c>/<c>SKIPPED</c> still completes — failures are isolated
    /// per item (§P5) and stay selectively retryable.
    /// </summary>
    Task<bool> TryCompleteAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Flip a job to <c>FAILED</c> (e.g. no adapter for its operation kind) — terminal, audited by query.</summary>
    Task MarkJobFailedAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>The <c>{total, applied, skipped, failed, pending}</c> progress tuple by query (§P5/§P6).</summary>
    Task<BulkOperationProgress> GetProgressAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Selective retry (§P5): re-arm the job's <c>FAILED</c> targets back to <c>PENDING</c> and,
    /// when any were re-armed, return the job itself to <c>DRAINING</c> so the runner resumes it.
    /// The re-run is no-op-safe: a partially-applied-then-failed item re-runs under the SAME
    /// deterministic command id, so it dedupes rather than double-applies (§P3). Returns the
    /// number of re-armed targets.
    /// </summary>
    Task<int> RetryFailedAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Cancel (§P5): flip a non-terminal job to <c>CANCELLED</c> so the drainer claims no further
    /// <c>PENDING</c> rows. Already-applied items stay applied — the frozen-set audit answer stays
    /// decidable. Returns whether the flip happened.
    /// </summary>
    Task<bool> CancelAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>One target row (for tests/audit reads); null when absent.</summary>
    Task<BulkOperationTargetRow?> ReadTargetAsync(Guid jobId, Guid instanceId, CancellationToken ct = default);
}

/// <summary>
/// PostgreSQL-backed <see cref="IBulkOperationStore"/>. Hand-rolled, Npgsql-only, all
/// <c>bulk_operation_*</c> SQL private to this type — the storage-boundary discipline of
/// <see cref="PostgresMovementLedgerStore"/> applied to the work-tables (migration 0018). The
/// claim uses exactly the 0018-documented shape: a partial-index scan over the <c>PENDING</c>
/// tail, <c>ORDER BY created_at, target_id</c> for stable FIFO, <c>FOR UPDATE SKIP LOCKED</c> so
/// concurrent drainers never contend on the same rows.
/// </summary>
public sealed class PostgresBulkOperationStore(string connectionString) : IBulkOperationStore
{
    public async Task RegisterAsync(
        BulkOperationJobRow job, IReadOnlyList<BulkOperationTargetRow> targets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(targets);

        const string jobSql = """
            INSERT INTO bulk_operation_jobs
                (job_id, operation_kind, matched_set, requested_batch_size, total_count, actor, status)
            VALUES
                (@job_id, @operation_kind, @matched_set::jsonb, @requested_batch_size, @total_count, @actor, 'REGISTERED');
            """;
        const string targetSql = """
            INSERT INTO bulk_operation_targets
                (target_id, job_id, instance_id, status, item_params, precondition_input)
            VALUES
                (@target_id, @job_id, @instance_id, 'PENDING', @item_params::jsonb, @precondition_input::jsonb);
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        // ONE transaction (§P1): the header and its frozen set land together or not at all — a
        // crash mid-registration leaves no headerless targets and no targetless plan.
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var command = new NpgsqlCommand(jobSql, connection, transaction))
        {
            command.Parameters.AddWithValue("job_id", job.JobId);
            command.Parameters.AddWithValue("operation_kind", job.OperationKind);
            command.Parameters.AddWithValue("matched_set", job.MatchedSetJson);
            command.Parameters.AddWithValue("requested_batch_size", job.RequestedBatchSize);
            command.Parameters.AddWithValue("total_count", job.TotalCount);
            command.Parameters.AddWithValue("actor", job.Actor);
            await command.ExecuteNonQueryAsync(ct);
        }

        foreach (var target in targets)
        {
            await using var command = new NpgsqlCommand(targetSql, connection, transaction);
            command.Parameters.AddWithValue("target_id", target.TargetId);
            command.Parameters.AddWithValue("job_id", target.JobId);
            command.Parameters.AddWithValue("instance_id", target.InstanceId);
            command.Parameters.AddWithValue("item_params", (object?)target.ItemParamsJson ?? DBNull.Value);
            command.Parameters.AddWithValue("precondition_input", (object?)target.PreconditionInputJson ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<BulkOperationJobRow>> ReadActiveJobsAsync(CancellationToken ct = default)
    {
        // The 0018 partial active_idx backs this: terminal jobs never enter the scan.
        const string sql = """
            SELECT job_id, operation_kind, matched_set::text, requested_batch_size, total_count, actor,
                   status, created_at, started_at, completed_at
            FROM bulk_operation_jobs
            WHERE status IN ('REGISTERED', 'DRAINING')
            ORDER BY created_at, job_id;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);

        var jobs = new List<BulkOperationJobRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            jobs.Add(new BulkOperationJobRow(
                JobId: reader.GetGuid(0),
                OperationKind: reader.GetString(1),
                MatchedSetJson: reader.GetString(2),
                RequestedBatchSize: reader.GetInt32(3),
                TotalCount: reader.GetInt64(4),
                Actor: reader.GetString(5),
                Status: reader.GetString(6),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(7),
                StartedAt: reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                CompletedAt: reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9)));
        }

        return jobs;
    }

    public async Task MarkDrainingAsync(Guid jobId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE bulk_operation_jobs
            SET status = 'DRAINING', started_at = COALESCE(started_at, clock_timestamp())
            WHERE job_id = @job_id AND status = 'REGISTERED';
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("job_id", jobId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> DrainBatchAsync(
        Guid jobId,
        int batchSize,
        Func<BulkOperationTargetRow, Task<BulkTargetOutcome>> perItem,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(perItem);

        // The 0018-documented claim, verbatim: PENDING tail only (the partial claim index),
        // stable FIFO order, SKIP LOCKED so concurrent drainers claim disjoint rows.
        const string claimSql = """
            SELECT target_id, job_id, instance_id, status, item_params::text, precondition_input::text,
                   attempts, failure_reason, commit_sequence, claimed_at, processed_at, created_at
            FROM bulk_operation_targets
            WHERE job_id = @job_id AND status = 'PENDING'
            ORDER BY created_at, target_id
            LIMIT @batch_size
            FOR UPDATE SKIP LOCKED;
            """;
        const string flipSql = """
            UPDATE bulk_operation_targets
            SET status = @status,
                attempts = attempts + 1,
                failure_reason = @failure_reason,
                commit_sequence = @commit_sequence,
                claimed_at = clock_timestamp(),
                processed_at = clock_timestamp()
            WHERE target_id = @target_id;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        // Claim + status flips in ONE transaction (§P2/§P5): a crash before commit rolls every
        // claimed row back to PENDING (resumability); the per-instance append inside perItem is on
        // its own connection and stays safe under the re-claim via the §P3 command id.
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var claimed = new List<BulkOperationTargetRow>();
        await using (var command = new NpgsqlCommand(claimSql, connection, transaction))
        {
            command.Parameters.AddWithValue("job_id", jobId);
            command.Parameters.AddWithValue("batch_size", batchSize);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                claimed.Add(ReadTarget(reader));
            }
        }

        foreach (var target in claimed)
        {
            // Per-item failure isolation (§P5): perItem NEVER throws for a domain/append failure —
            // it returns FAILED — so one bad item flips to FAILED and the batch continues.
            var outcome = await perItem(target);

            await using var command = new NpgsqlCommand(flipSql, connection, transaction);
            command.Parameters.AddWithValue("target_id", target.TargetId);
            command.Parameters.AddWithValue("status", outcome.Status);
            command.Parameters.AddWithValue("failure_reason", (object?)outcome.FailureReason ?? DBNull.Value);
            command.Parameters.AddWithValue("commit_sequence", (object?)outcome.CommitSequence ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return claimed.Count;
    }

    public async Task<bool> TryCompleteAsync(Guid jobId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE bulk_operation_jobs
            SET status = 'COMPLETED', completed_at = clock_timestamp()
            WHERE job_id = @job_id
              AND status = 'DRAINING'
              AND NOT EXISTS (
                  SELECT 1 FROM bulk_operation_targets
                  WHERE job_id = @job_id AND status = 'PENDING');
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("job_id", jobId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task MarkJobFailedAsync(Guid jobId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE bulk_operation_jobs
            SET status = 'FAILED', completed_at = clock_timestamp()
            WHERE job_id = @job_id AND status IN ('REGISTERED', 'DRAINING');
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("job_id", jobId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<BulkOperationProgress> GetProgressAsync(Guid jobId, CancellationToken ct = default)
    {
        // Counts by query over the frozen set (§P5/§P6, the job_status_idx). total comes from the
        // header (the frozen matched_count), the breakdown from the targets — so a job with zero
        // targets still answers.
        const string sql = """
            SELECT j.total_count,
                   COUNT(t.target_id) FILTER (WHERE t.status = 'APPLIED'),
                   COUNT(t.target_id) FILTER (WHERE t.status = 'SKIPPED'),
                   COUNT(t.target_id) FILTER (WHERE t.status = 'FAILED'),
                   COUNT(t.target_id) FILTER (WHERE t.status = 'PENDING')
            FROM bulk_operation_jobs j
            LEFT JOIN bulk_operation_targets t ON t.job_id = j.job_id
            WHERE j.job_id = @job_id
            GROUP BY j.total_count;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("job_id", jobId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException($"No bulk-operation job '{jobId}' is registered.");
        }

        return new BulkOperationProgress(
            Total: reader.GetInt64(0),
            Applied: reader.GetInt64(1),
            Skipped: reader.GetInt64(2),
            Failed: reader.GetInt64(3),
            Pending: reader.GetInt64(4));
    }

    public async Task<int> RetryFailedAsync(Guid jobId, CancellationToken ct = default)
    {
        const string retrySql = """
            UPDATE bulk_operation_targets
            SET status = 'PENDING', failure_reason = NULL, processed_at = NULL
            WHERE job_id = @job_id AND status = 'FAILED';
            """;
        const string reopenSql = """
            UPDATE bulk_operation_jobs
            SET status = 'DRAINING', completed_at = NULL
            WHERE job_id = @job_id AND status IN ('COMPLETED', 'FAILED');
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        // Re-arm + reopen in one transaction so the runner never sees re-armed PENDING rows on a
        // job it considers terminal.
        await using var transaction = await connection.BeginTransactionAsync(ct);

        int reArmed;
        await using (var command = new NpgsqlCommand(retrySql, connection, transaction))
        {
            command.Parameters.AddWithValue("job_id", jobId);
            reArmed = await command.ExecuteNonQueryAsync(ct);
        }

        if (reArmed > 0)
        {
            await using var command = new NpgsqlCommand(reopenSql, connection, transaction);
            command.Parameters.AddWithValue("job_id", jobId);
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return reArmed;
    }

    public async Task<bool> CancelAsync(Guid jobId, CancellationToken ct = default)
    {
        // PENDING rows are left as-is: the drainer only claims for REGISTERED/DRAINING jobs, and
        // the untouched rows keep the "what did this plan touch?" answer decidable (§P5).
        const string sql = """
            UPDATE bulk_operation_jobs
            SET status = 'CANCELLED', completed_at = clock_timestamp()
            WHERE job_id = @job_id AND status IN ('REGISTERED', 'DRAINING');
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("job_id", jobId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<BulkOperationTargetRow?> ReadTargetAsync(
        Guid jobId, Guid instanceId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT target_id, job_id, instance_id, status, item_params::text, precondition_input::text,
                   attempts, failure_reason, commit_sequence, claimed_at, processed_at, created_at
            FROM bulk_operation_targets
            WHERE job_id = @job_id AND instance_id = @instance_id;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("job_id", jobId);
        command.Parameters.AddWithValue("instance_id", instanceId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTarget(reader) : null;
    }

    private static BulkOperationTargetRow ReadTarget(NpgsqlDataReader reader) => new(
        TargetId: reader.GetGuid(0),
        JobId: reader.GetGuid(1),
        InstanceId: reader.GetGuid(2),
        Status: reader.GetString(3),
        ItemParamsJson: reader.IsDBNull(4) ? null : reader.GetString(4),
        PreconditionInputJson: reader.IsDBNull(5) ? null : reader.GetString(5),
        Attempts: reader.GetInt32(6),
        FailureReason: reader.IsDBNull(7) ? null : reader.GetString(7),
        CommitSequence: reader.IsDBNull(8) ? null : reader.GetInt64(8),
        ClaimedAt: reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
        ProcessedAt: reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
        CreatedAt: reader.GetFieldValue<DateTimeOffset>(11));
}
