using Babelstone.EventStore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Babelstone.Engine.Hosting;

/// <summary>One instance registered into a job's frozen universe (the ADR-PC-035 registration input).</summary>
/// <param name="InstanceId">The opaque product-instance (stream) reference — never PII.</param>
/// <param name="ItemParamsJson">Optional per-item params the adapter's event factory consumes; frozen at registration.</param>
/// <param name="PreconditionInputJson">Optional per-item input to the adapter's precondition; frozen at registration.</param>
public sealed record BulkTargetRegistration(
    Guid InstanceId,
    string? ItemParamsJson = null,
    string? PreconditionInputJson = null);

/// <summary>
/// A bulk-operation registration (ADR-PC-035): the job header plus the explicit instance list
/// that becomes the frozen universe. The <see cref="JobId"/> is caller-supplied because it IS the
/// <c>action_id</c> the per-instance command ids derive from (<see cref="BulkOperationCommandId"/>);
/// the operator command surface owns minting it and any register-level idempotency.
/// </summary>
/// <param name="JobId">The job's identity and action id.</param>
/// <param name="OperationKind">The adapter dispatch key (e.g. <c>PackVersionMigrated</c>).</param>
/// <param name="MatchedSetJson">The audit snapshot of what was matched (the <c>matched_count</c>
/// preview's predicate) — opaque JSON, no PII.</param>
/// <param name="RequestedBatchSize">The drainer's bounded claim size (ADR-PC-035 / ADR-PC-009).</param>
/// <param name="Actor">The registering operator — a structural token, never PII.</param>
/// <param name="Targets">The matched instances — the universe frozen at registration.</param>
public sealed record BulkOperationRegistration(
    Guid JobId,
    string OperationKind,
    string MatchedSetJson,
    int RequestedBatchSize,
    string Actor,
    IReadOnlyList<BulkTargetRegistration> Targets);

/// <summary>
/// The generic bulk-operation service (ADR-PC-035): register a frozen universe transactionally,
/// answer progress by query, re-arm failures, cancel. In plain English: this is the
/// operator-facing half of the runner — everything except the draining itself — generic over the
/// operation (it never reads an adapter), so a command/query HTTP surface is a thin mapping onto
/// these calls.
/// </summary>
public sealed class BulkOperationService(IBulkOperationStore store)
{
    /// <summary>
    /// Freeze the universe (ADR-PC-035): the header and one PENDING target per instance land in
    /// one transaction; <c>total_count</c> is the frozen matched count. Once registered the set is
    /// immutable — a straggler instance is a NEW job, never a re-scan.
    /// </summary>
    public async Task RegisterAsync(BulkOperationRegistration registration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrEmpty(registration.OperationKind);
        ArgumentException.ThrowIfNullOrEmpty(registration.Actor);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(registration.RequestedBatchSize, 0);

        var job = new BulkOperationJobRow(
            JobId: registration.JobId,
            OperationKind: registration.OperationKind,
            MatchedSetJson: registration.MatchedSetJson,
            RequestedBatchSize: registration.RequestedBatchSize,
            TotalCount: registration.Targets.Count,
            Actor: registration.Actor,
            Status: "REGISTERED",
            CreatedAt: default); // the DB clock stamps created_at; the row value here is unused

        var targets = registration.Targets
            .Select(target => new BulkOperationTargetRow(
                TargetId: Guid.NewGuid(),
                JobId: registration.JobId,
                InstanceId: target.InstanceId,
                Status: "PENDING",
                ItemParamsJson: target.ItemParamsJson,
                PreconditionInputJson: target.PreconditionInputJson,
                Attempts: 0,
                FailureReason: null,
                CommitSequence: null,
                ClaimedAt: null,
                ProcessedAt: null,
                CreatedAt: default))
            .ToList();

        await store.RegisterAsync(job, targets, ct);
    }

    /// <summary>The <c>{total, applied, skipped, failed, pending}</c> tuple by query (ADR-PC-035).</summary>
    public Task<BulkOperationProgress> GetProgressAsync(Guid jobId, CancellationToken ct = default)
        => store.GetProgressAsync(jobId, ct);

    /// <summary>One job header by query (ADR-PC-035) — status, kind, frozen counts, and the
    /// <c>matched_set</c> audit snapshot (which the command surface's register-level idempotency
    /// check reads its <c>set_digest</c> from); null when no such job is registered.</summary>
    public Task<BulkOperationJobRow?> GetJobAsync(Guid jobId, CancellationToken ct = default)
        => store.ReadJobAsync(jobId, ct);

    /// <summary>Selective retry (ADR-PC-035): re-arm a reopenable job's FAILED subset to PENDING;
    /// the re-run dedupes on the deterministic command id (<see cref="BulkOperationCommandId"/>).</summary>
    public Task<int> RetryFailedAsync(Guid jobId, CancellationToken ct = default)
        => store.RetryFailedAsync(jobId, ct);

    /// <summary>Cancel (ADR-PC-035): stop further claims — enforced by the claim's own DRAINING
    /// requirement, so it bites even mid-run; already-applied items stay applied.</summary>
    public Task<bool> CancelAsync(Guid jobId, CancellationToken ct = default)
        => store.CancelAsync(jobId, ct);
}

/// <summary>
/// The drain half of the runner (ADR-PC-035) — the outbox pattern's second instance: claim a
/// bounded <c>FOR UPDATE SKIP LOCKED</c> batch of PENDING targets, run the per-instance step
/// (optional precondition → adapter event factory → native idempotent append), flip each row's
/// status in the claim transaction, repeat until the job drains, then complete it. In plain
/// English: the worker that walks a frozen to-do list item by item, safely resumable at any point
/// because the table IS the to-do list and every append dedupes on the deterministic command id
/// (<see cref="BulkOperationCommandId"/>).
/// </summary>
/// <remarks>
/// Per-item failure isolation (ADR-PC-035): a throwing precondition/factory/append marks THAT row
/// <c>FAILED</c> (with an operational-tier reason) and the batch continues — one bad item never
/// aborts the job. An append that loses a head race retries bounded times before it counts as a
/// failure. A job whose <c>operation_kind</c> has no registered adapter is flipped <c>FAILED</c>
/// fail-loud (visible by query) rather than silently starving the queue. Restart resumability is
/// the substrate's: claimed-but-uncommitted rows roll back to PENDING, and the deterministic
/// command id turns any re-run of an already-appended step into a no-op returning the original
/// receipt. A cancel starves the claim itself (it requires a DRAINING job), so it stops a job
/// even mid-pass.
/// </remarks>
public sealed class BulkOperationDrainer(
    IBulkOperationStore store,
    BulkInstanceAppender appender,
    IEnumerable<IBulkOperationStrategy> strategies,
    ILogger<BulkOperationDrainer>? logger = null)
{
    private readonly IReadOnlyDictionary<string, IBulkOperationStrategy> _strategies =
        strategies.ToDictionary(strategy => strategy.OperationKind, StringComparer.Ordinal);

    /// <summary>
    /// One full pass: every active job is drained batch-by-batch to exhaustion, then completed.
    /// Returns the number of targets processed (0 = no active work).
    /// </summary>
    public async Task<int> DrainOnceAsync(CancellationToken ct = default)
    {
        var processed = 0;
        foreach (var job in await store.ReadActiveJobsAsync(ct))
        {
            if (!_strategies.TryGetValue(job.OperationKind, out var strategy))
            {
                // Fail-loud, by query (ADR-PC-035): a job nothing can execute must not sit
                // REGISTERED forever looking healthy. Its targets stay untouched for audit.
                logger?.LogError(
                    "Bulk job {JobId} has operation_kind '{OperationKind}' with no registered adapter; marking the job FAILED.",
                    job.JobId, job.OperationKind);
                await store.MarkJobFailedAsync(job.JobId, ct);
                continue;
            }

            if (job.Status == "REGISTERED")
            {
                await store.MarkDrainingAsync(job.JobId, ct);
            }

            while (!ct.IsCancellationRequested)
            {
                var claimed = await store.DrainBatchAsync(
                    job.JobId,
                    job.RequestedBatchSize,
                    target => ProcessTargetAsync(strategy, job, target, ct),
                    ct);
                processed += claimed;
                if (claimed == 0)
                {
                    break; // nothing pending (or a concurrent drainer holds the tail) — move on
                }
            }

            // A cancel raced between batches simply leaves the flip a no-op (TryComplete requires
            // DRAINING); a fully drained job completes here even when some items FAILED — failure
            // is per-item (ADR-PC-035), and the failed subset stays selectively retryable.
            await store.TryCompleteAsync(job.JobId, ct);
        }

        return processed;
    }

    private async Task<BulkTargetOutcome> ProcessTargetAsync(
        IBulkOperationStrategy strategy,
        BulkOperationJobRow job,
        BulkOperationTargetRow target,
        CancellationToken ct)
    {
        try
        {
            if (strategy.EvaluatePrecondition(target) is BulkPreconditionVerdict.Skip skip)
            {
                logger?.LogInformation(
                    "Bulk job {JobId}: instance {InstanceId} skipped by precondition ({Reason}).",
                    job.JobId, target.InstanceId, skip.Reason);
                return BulkTargetOutcome.Skipped();
            }

            var @event = strategy.CreateEvent(target);
            var commandId = BulkOperationCommandId.For(job.JobId, target.InstanceId);

            // A ConcurrencyException is a lost head race (another writer — a lifecycle step, a
            // sibling operation — advanced the stream between the appender's head-read and its
            // append), not a broken item: the appender re-reads the head on every call, so a
            // bounded retry usually lands it. Only exhaustion records FAILED — with the exception
            // name in the reason, so the outcome stays classifiable for selective retry.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var commitSequence = await appender.AppendAsync(
                        target.InstanceId,
                        @event,
                        commandId,
                        actor: job.Actor,
                        // The job's registration instant, not a drain-time clock read: a
                        // retry/restart re-derives the identical envelope valid_time.
                        validTime: job.CreatedAt,
                        ct);
                    return BulkTargetOutcome.Applied(commitSequence);
                }
                catch (ConcurrencyException) when (attempt < MaxHeadRaceAttempts)
                {
                    // retry: the next AppendAsync call re-reads the moved head
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown is not a per-item failure — let the claim transaction roll back
        }
        catch (Exception ex)
        {
            // Per-item isolation (ADR-PC-035): record and continue. The reason stays
            // operational-tier (exception type + truncated message — structural context, never a
            // business amount or PII; ADR-PC-004, mirroring inbox.result_summary).
            return BulkTargetOutcome.Failed(FailureReason(ex));
        }
    }

    // Bounded head-race retries per item: enough to ride out routine concurrent lifecycle writes,
    // small enough that a genuinely contended stream surfaces as FAILED for selective retry.
    private const int MaxHeadRaceAttempts = 3;

    private static string FailureReason(Exception ex)
    {
        var message = $"{ex.GetType().Name}: {ex.Message}";
        return message.Length <= 200 ? message : message[..200];
    }
}

/// <summary>Tuning for the in-process bulk-operation runner loop.</summary>
public sealed record BulkOperationRunnerOptions
{
    /// <summary>How long to wait after a pass that found no work before polling again.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Hosts the <see cref="BulkOperationDrainer"/> as an in-process <see cref="BackgroundService"/> —
/// the same co-hosted poll-loop shape as the outbox relay and the spine projection relay. An idle
/// pass waits the poll interval; a failed pass is backpressure (back off and retry — the
/// work-table is the durable to-do list, so nothing is lost, ADR-PC-035).
/// </summary>
public sealed class BulkOperationRunnerService(
    BulkOperationDrainer drainer,
    BulkOperationRunnerOptions options,
    ILogger<BulkOperationRunnerService>? logger = null) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = options.PollInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await drainer.DrainOnceAsync(stoppingToken);

                backoff = options.PollInterval; // a clean pass resets the backoff
                if (processed == 0)
                {
                    await Task.Delay(options.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // graceful shutdown; claimed-but-uncommitted rows roll back to PENDING
            }
            catch (Exception ex)
            {
                logger?.LogWarning(
                    ex, "Bulk-operation drain pass failed; backing off {Backoff} and retrying (the work-table resumes from PENDING).",
                    backoff);
                await Task.Delay(backoff, stoppingToken);
                backoff = backoff < MaxBackoff ? backoff + backoff : MaxBackoff;
            }
        }
    }
}
