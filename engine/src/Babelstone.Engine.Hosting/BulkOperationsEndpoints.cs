using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Babelstone.EventStore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Babelstone.Engine.Hosting;

// The operator bulk-operations HTTP contract (ADR-PC-035, bd babelstone-qpiw.4). snake_case on the
// wire (the host's JSON options). No PII (ADR-PC-004): a job id, an operation kind, an operator
// actor reference, structural instance ids, and opaque per-item JSON params only. Family-agnostic
// by construction — the contract names no family; the runner dispatches on operation_kind to the
// registered IBulkOperationStrategy adapters (ADR-PC-021 / ADR-PC-035).

/// <summary>
/// One instance in the register request's explicit target set, with optional frozen per-item
/// JSON for the operation's adapter (ADR-PC-035): <see cref="ItemParams"/> feeds the event
/// factory, <see cref="PreconditionInput"/> the precondition. Opaque to the spine — never parsed
/// here, only frozen verbatim into the work-table row.
/// </summary>
/// <param name="InstanceId">The opaque product-instance (stream) reference — never PII.</param>
/// <param name="ItemParams">Optional per-item params for the adapter's event factory; frozen at registration.</param>
/// <param name="PreconditionInput">Optional per-item input to the adapter's precondition; frozen at registration.</param>
public sealed record BulkTargetRequest(
    Guid InstanceId,
    JsonElement? ItemParams = null,
    JsonElement? PreconditionInput = null);

/// <summary>
/// An operator bulk-operation registration (ADR-PC-035): freeze an EXPLICIT instance set into one
/// audited job the background runner drains. The target set is named ONE of two ways (exactly one
/// — they are mutually exclusive): a bare <see cref="InstanceIds"/> list (operations with no
/// per-item params), or a rich <see cref="Targets"/> list carrying frozen per-item JSON (e.g. a
/// per-item <c>held_amount_cents</c>). There is no predicate arm here — resolving a predicate to
/// ids is the caller's step; the registration input is always the concrete frozen set
/// (ADR-PC-035: one job owns ONE decidable set).
/// </summary>
/// <param name="JobId">The operator-minted job id — the job's identity, the <c>action_id</c> the
/// deterministic per-instance command ids derive from (<see cref="BulkOperationCommandId"/>), AND
/// the register-level idempotency key: re-POSTing the same <c>job_id</c> with the same frozen set
/// (same <c>set_digest</c>) is a benign replay; with a DIFFERENT set it is a 409 conflict.</param>
/// <param name="OperationKind">The adapter dispatch key (e.g. <c>PackVersionMigrated</c>, <c>FundsHeld</c>).</param>
/// <param name="Actor">The registering operator — a structural actor token, never PII (ADR-PC-004).</param>
/// <param name="RequestedBatchSize">The drainer's bounded claim size (ADR-PC-035 / ADR-PC-009 §A3); defaults when omitted.</param>
/// <param name="InstanceIds">The explicit frozen id set (bare arm). Mutually exclusive with <see cref="Targets"/>.</param>
/// <param name="Targets">The explicit frozen target set with per-item JSON (rich arm). Mutually exclusive with <see cref="InstanceIds"/>.</param>
/// <param name="SetDigest">Optional integrity check: the caller's digest over the frozen id set
/// (<see cref="BulkOperationSetDigest"/>). When supplied it MUST match the server-computed digest,
/// so a payload that mutated between the operator's preview and the register fails loud.</param>
/// <param name="Preview">When true, returns counts + digest + a small sample WITHOUT registering anything.</param>
public sealed record BulkOperationRequest(
    Guid JobId,
    string OperationKind,
    string Actor,
    int? RequestedBatchSize = null,
    IReadOnlyList<Guid>? InstanceIds = null,
    IReadOnlyList<BulkTargetRequest>? Targets = null,
    string? SetDigest = null,
    bool Preview = false);

/// <summary>
/// The register/preview outcome. Deliberately NEVER echoes the full id set — a low-millions
/// registration must not turn the response into the 500k-id payload the runner exists to avoid
/// (ADR-PC-035; the PR #324 lesson): the caller gets counts, the <see cref="SetDigest"/> (the
/// verifiable fingerprint of exactly what was frozen), and a small sample.
/// </summary>
/// <param name="JobId">Echoes the job id (the audit handle).</param>
/// <param name="Registered">True iff a job exists after this call (false only for a preview).</param>
/// <param name="AlreadyRegistered">True when this was an idempotent replay of an existing registration.</param>
/// <param name="Status">The job's status (<c>REGISTERED</c> on first registration; the live status on a replay); null on a preview.</param>
/// <param name="TotalCount">The size of the frozen universe (the <c>matched_count</c>).</param>
/// <param name="SetDigest">The server-computed digest over the frozen id set (<see cref="BulkOperationSetDigest"/>).</param>
/// <param name="SampleInstanceIds">A small sample of the frozen set (first few, request order) — never the full set.</param>
public sealed record BulkOperationRegisterResponse(
    Guid JobId,
    bool Registered,
    bool AlreadyRegistered,
    string? Status,
    long TotalCount,
    string SetDigest,
    IReadOnlyList<Guid> SampleInstanceIds);

/// <summary>The live job view: status + the <c>{total, applied, skipped, failed, pending}</c>
/// progress tuple by query over the frozen set (ADR-PC-035).</summary>
/// <param name="JobId">The job's identity.</param>
/// <param name="OperationKind">The adapter dispatch key the job registered under.</param>
/// <param name="Status"><c>REGISTERED → DRAINING → COMPLETED | FAILED | CANCELLED</c>.</param>
/// <param name="SetDigest">The digest frozen at registration (from the audit snapshot), when present.</param>
/// <param name="Total">The frozen universe size.</param>
/// <param name="Applied">Targets whose event appended.</param>
/// <param name="Skipped">Targets the precondition declined.</param>
/// <param name="Failed">Targets that errored — selectively retryable.</param>
/// <param name="Pending">Targets not yet processed.</param>
public sealed record BulkOperationStatusResponse(
    Guid JobId,
    string OperationKind,
    string Status,
    string? SetDigest,
    long Total,
    long Applied,
    long Skipped,
    long Failed,
    long Pending);

/// <summary>The selective-retry outcome (ADR-PC-035): how many FAILED targets were re-armed to PENDING.</summary>
public sealed record BulkOperationRetryResponse(Guid JobId, int RetriedCount);

/// <summary>The cancel outcome (ADR-PC-035): whether the job flipped to CANCELLED.</summary>
public sealed record BulkOperationCancelResponse(Guid JobId, bool Cancelled);

/// <summary>
/// THE register-level integrity token, stated once (bd babelstone-qpiw.4): a deterministic
/// SHA-256 digest over a frozen instance-id set. In plain English: instead of echoing half a
/// million ids back and forth to prove "we are talking about the same set", the operator and the
/// server each fingerprint the set and compare fingerprints. Canonical form: each id in lowercase
/// hyphenated (<c>D</c>) format, ORDINAL-SORTED (so the digest is order-insensitive — the set, not
/// the list, is what is frozen), joined by <c>\n</c>, UTF-8, SHA-256, lowercase hex, prefixed
/// <c>sha256:</c>. Pure — no clock, no randomness — pinned by <c>BulkOperationSetDigestTests</c>.
/// The digest is stored inside the job's <c>matched_set</c> audit snapshot (no schema change to
/// the immutable migration 0018), which is also what the idempotent-replay check compares against.
/// </summary>
public static class BulkOperationSetDigest
{
    public static string Compute(IEnumerable<Guid> instanceIds)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);

        var canonical = string.Join(
            '\n',
            instanceIds.Select(id => id.ToString("D")).OrderBy(id => id, StringComparer.Ordinal));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"sha256:{Convert.ToHexStringLower(hash)}";
    }
}

/// <summary>
/// The resolved plan for a bulk-operation register request: either a validation ERROR (an HTTP
/// status + message) or a PROCEED carrying the normalized frozen target list, the computed set
/// digest, and the effective batch size. A pure value (no clock, no I/O), so
/// <see cref="BulkOperationsEndpoints.Plan"/> is unit-testable without the HTTP stack or a
/// database (the <see cref="PackMigrationPlan"/> shape).
/// </summary>
internal sealed record BulkOperationRegisterPlan(
    int? ErrorStatus,
    string? ErrorMessage,
    IReadOnlyList<BulkTargetRegistration>? Targets = null,
    string? SetDigest = null,
    int BatchSize = 0)
{
    public bool Ok => ErrorStatus is null;

    public static BulkOperationRegisterPlan Error(int status, string message) => new(status, message);
}

/// <summary>
/// The operator bulk-operations command/query surface (ADR-PC-035, bd babelstone-qpiw.4):
/// <c>POST /v1/bulk-operations</c> (register a frozen set / preview it),
/// <c>GET /v1/bulk-operations/{id}</c> (live progress + status),
/// <c>POST /v1/bulk-operations/{id}/retry-failed</c> (re-arm only the FAILED subset), and
/// <c>POST /v1/bulk-operations/{id}/cancel</c> (stop further claims). In plain English: this is
/// how an operator hands the engine a huge, explicit to-do list once, then watches it drain,
/// retries just the failures, or calls the whole plan off — thin mappings onto the existing
/// <see cref="BulkOperationService"/>; the actual work happens in the background runner.
/// </summary>
/// <remarks>
/// Family-agnostic (it lives in the hosting spine, ADR-PC-021): the contract names no family; the
/// drainer dispatches on <c>operation_kind</c> to the registered <see cref="IBulkOperationStrategy"/>
/// adapters. This surface owns MINTING/CHECKING register-level idempotency (the
/// <see cref="BulkOperationRegistration"/> contract — the runner leaves it to the command
/// surface): the operator supplies the
/// <c>job_id</c>; a duplicate register with the SAME frozen set (equal <see cref="BulkOperationSetDigest"/>)
/// replays benignly, a duplicate with a DIFFERENT set is a 409 — never a silent second plan and
/// never a silent mutation of a frozen one. Cancel is the ADR-PC-035 status flip (the claim's own
/// DRAINING requirement enforces it); there is deliberately NO catalogued cancellation event —
/// bulk milestones are STORE-ONLY facts (ADR-IC-017).
/// </remarks>
public static class BulkOperationsEndpoints
{
    /// <summary>Sample size echoed on register/preview — enough for an operator sanity check,
    /// never the full set (the PR #324 payload lesson).</summary>
    internal const int SampleSize = 5;

    /// <summary>The default drainer claim size when the request omits <c>requested_batch_size</c>
    /// (the re-homed PR #324 cap — ADR-PC-009 §A3: a batching detail, not a population ceiling).</summary>
    internal const int DefaultBatchSize = 500;

    /// <summary>
    /// Map the four routes ONCE at host level (family-agnostic), beside
    /// <see cref="PackMigrationsEndpoints.Map"/> in <c>Program.cs</c>.
    /// </summary>
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/bulk-operations", RegisterAsync);
        app.MapGet("/v1/bulk-operations/{jobId:guid}", GetStatusAsync);
        app.MapPost("/v1/bulk-operations/{jobId:guid}/retry-failed", RetryFailedAsync);
        app.MapPost("/v1/bulk-operations/{jobId:guid}/cancel", CancelAsync);
    }

    /// <summary>
    /// Validate the request and NORMALIZE it into the frozen registration input — a pure decision
    /// (no clock, no I/O), split out so the validation rules are unit-testable with no HTTP stack
    /// or database (the <see cref="PackMigrationsEndpoints.Plan"/> discipline).
    /// </summary>
    internal static BulkOperationRegisterPlan Plan(BulkOperationRequest request)
    {
        // Malformed intent — fail loud with 400 rather than freeze nothing or the wrong plan.
        if (request.JobId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.OperationKind)
            || string.IsNullOrWhiteSpace(request.Actor))
        {
            return BulkOperationRegisterPlan.Error(
                StatusCodes.Status400BadRequest,
                "job_id, operation_kind and actor are required.");
        }

        if (request.RequestedBatchSize is <= 0)
        {
            return BulkOperationRegisterPlan.Error(
                StatusCodes.Status400BadRequest, "requested_batch_size must be a positive integer.");
        }

        // Exactly one target arm (bare ids XOR rich targets). Both or neither is unprocessable.
        var hasIds = request.InstanceIds is { Count: > 0 };
        var hasTargets = request.Targets is { Count: > 0 };
        if (hasIds == hasTargets)
        {
            return BulkOperationRegisterPlan.Error(
                StatusCodes.Status422UnprocessableEntity,
                "provide exactly one of instance_ids (non-empty) or targets (non-empty).");
        }

        var targets = hasIds
            ? request.InstanceIds!.Select(id => new BulkTargetRegistration(id)).ToList()
            : request.Targets!
                .Select(target => new BulkTargetRegistration(
                    target.InstanceId,
                    ItemParamsJson: target.ItemParams?.GetRawText(),
                    PreconditionInputJson: target.PreconditionInput?.GetRawText()))
                .ToList();

        if (targets.Any(target => target.InstanceId == Guid.Empty))
        {
            return BulkOperationRegisterPlan.Error(
                StatusCodes.Status422UnprocessableEntity, "every target instance_id must be a non-empty uuid.");
        }

        // A duplicate id would violate the frozen set's (job_id, instance_id) uniqueness at the
        // store (migration 0018) — reject it HERE as the operator's input error, not a 500.
        if (targets.Select(target => target.InstanceId).Distinct().Count() != targets.Count)
        {
            return BulkOperationRegisterPlan.Error(
                StatusCodes.Status422UnprocessableEntity,
                "the frozen target set contains duplicate instance_ids — a set freezes each instance once.");
        }

        // The integrity check: when the caller carries its own digest (e.g. minted at preview
        // time), the payload must still BE that set — a drifted payload fails loud, never
        // freezing a set the operator did not confirm.
        var digest = BulkOperationSetDigest.Compute(targets.Select(target => target.InstanceId));
        if (request.SetDigest is not null
            && !string.Equals(request.SetDigest, digest, StringComparison.Ordinal))
        {
            return BulkOperationRegisterPlan.Error(
                StatusCodes.Status422UnprocessableEntity,
                $"set_digest mismatch: the payload's frozen id set computes '{digest}', not the supplied "
                + "digest — the target set changed since it was fingerprinted.");
        }

        return new BulkOperationRegisterPlan(
            null, null,
            Targets: targets,
            SetDigest: digest,
            BatchSize: request.RequestedBatchSize ?? DefaultBatchSize);
    }

    /// <summary>The job's <c>matched_set</c> audit snapshot (migration 0018): what this plan
    /// targeted, WITHOUT the full id echo — kind, digest, count, and a small sample. This is also
    /// where the register-level idempotency token lives (no schema change; the JSONB column
    /// already exists for exactly this audit role).</summary>
    internal static string BuildMatchedSetJson(string setDigest, long totalCount, IReadOnlyList<Guid> sample)
        => JsonSerializer.Serialize(
            new MatchedSetSnapshot("explicit_set", setDigest, totalCount, sample), SnakeCaseJson);

    /// <summary>Read the <c>set_digest</c> back out of a job's <c>matched_set</c> snapshot; null
    /// when the snapshot predates the digest convention (e.g. a job registered straight through
    /// <see cref="BulkOperationService"/> in tests).</summary>
    internal static string? ReadSetDigest(string matchedSetJson)
    {
        try
        {
            using var document = JsonDocument.Parse(matchedSetJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("set_digest", out var digest)
                && digest.ValueKind == JsonValueKind.String
                    ? digest.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null; // an opaque/foreign snapshot simply carries no digest to compare
        }
    }

    private static async Task<IResult> RegisterAsync(
        BulkOperationRequest request,
        BulkOperationService service,
        CancellationToken ct)
    {
        var plan = Plan(request);
        if (!plan.Ok)
        {
            return Results.Problem(plan.ErrorMessage, statusCode: plan.ErrorStatus);
        }

        var targets = plan.Targets!;
        var sample = targets.Take(SampleSize).Select(target => target.InstanceId).ToList();

        // Preview (the ADR-PC-035 matched_count confirmation step): counts + the digest the
        // operator can pin on the follow-up register — no side effect, no id echo.
        if (request.Preview)
        {
            return Results.Ok(new BulkOperationRegisterResponse(
                request.JobId,
                Registered: false,
                AlreadyRegistered: false,
                Status: null,
                TotalCount: targets.Count,
                SetDigest: plan.SetDigest!,
                SampleInstanceIds: sample));
        }

        // Register-level idempotency (this surface's contract, per the runner header): the same
        // job_id with the same frozen set replays benignly; with a different set it conflicts.
        var existing = await service.GetJobAsync(request.JobId, ct);
        if (existing is not null)
        {
            return ReplayOrConflict(existing, plan.SetDigest!, sample);
        }

        try
        {
            await service.RegisterAsync(
                new BulkOperationRegistration(
                    JobId: request.JobId,
                    OperationKind: request.OperationKind,
                    MatchedSetJson: BuildMatchedSetJson(plan.SetDigest!, targets.Count, sample),
                    RequestedBatchSize: plan.BatchSize,
                    Actor: request.Actor,
                    Targets: targets),
                ct);
        }
        catch (Exception)
        {
            // Two concurrent registers of the same job_id race past the read above; the store's
            // primary key makes exactly one win. Re-read: if the job exists now, resolve the loser
            // as replay-or-conflict; anything else is a genuine fault and rethrows.
            var raced = await service.GetJobAsync(request.JobId, ct);
            if (raced is null)
            {
                throw;
            }

            return ReplayOrConflict(raced, plan.SetDigest!, sample);
        }

        return Results.Ok(new BulkOperationRegisterResponse(
            request.JobId,
            Registered: true,
            AlreadyRegistered: false,
            Status: "REGISTERED",
            TotalCount: targets.Count,
            SetDigest: plan.SetDigest!,
            SampleInstanceIds: sample));
    }

    private static IResult ReplayOrConflict(
        BulkOperationJobRow existing, string requestDigest, IReadOnlyList<Guid> sample)
    {
        var existingDigest = ReadSetDigest(existing.MatchedSetJson);
        if (string.Equals(existingDigest, requestDigest, StringComparison.Ordinal))
        {
            // The benign replay: same job, same frozen set — return the live truth, register nothing.
            return Results.Ok(new BulkOperationRegisterResponse(
                existing.JobId,
                Registered: true,
                AlreadyRegistered: true,
                Status: existing.Status,
                TotalCount: existing.TotalCount,
                SetDigest: requestDigest,
                SampleInstanceIds: sample));
        }

        // Same job_id, different frozen set: a registered universe is IMMUTABLE (ADR-PC-035 — a
        // straggler is a NEW job, never a re-scan), so this is a conflict, never a silent merge.
        return Results.Problem(
            $"job '{existing.JobId}' is already registered over a different frozen set "
            + $"(registered digest '{existingDigest ?? "<none>"}', request digest '{requestDigest}') — "
            + "a frozen universe is immutable; register a NEW job_id for a different set.",
            statusCode: StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> GetStatusAsync(
        Guid jobId, BulkOperationService service, CancellationToken ct)
    {
        var job = await service.GetJobAsync(jobId, ct);
        if (job is null)
        {
            return Results.Problem($"no bulk-operation job '{jobId}' is registered.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var progress = await service.GetProgressAsync(jobId, ct);
        return Results.Ok(new BulkOperationStatusResponse(
            job.JobId,
            job.OperationKind,
            job.Status,
            ReadSetDigest(job.MatchedSetJson),
            Total: progress.Total,
            Applied: progress.Applied,
            Skipped: progress.Skipped,
            Failed: progress.Failed,
            Pending: progress.Pending));
    }

    private static async Task<IResult> RetryFailedAsync(
        Guid jobId, BulkOperationService service, CancellationToken ct)
    {
        var job = await service.GetJobAsync(jobId, ct);
        if (job is null)
        {
            return Results.Problem($"no bulk-operation job '{jobId}' is registered.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Re-drives ONLY the FAILED subset (ADR-PC-035): the store re-arms FAILED→PENDING under a
        // reopenable job; a CANCELLED plan re-arms nothing (count 0) — the plan stays cancelled.
        var retried = await service.RetryFailedAsync(jobId, ct);
        return Results.Ok(new BulkOperationRetryResponse(jobId, retried));
    }

    private static async Task<IResult> CancelAsync(
        Guid jobId, BulkOperationService service, CancellationToken ct)
    {
        var job = await service.GetJobAsync(jobId, ct);
        if (job is null)
        {
            return Results.Problem($"no bulk-operation job '{jobId}' is registered.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // A status flip, deliberately NOT an event (ADR-IC-017 store-only posture — there is no
        // catalogued BulkOperationCancelled): the claim's own DRAINING requirement makes the flip
        // bite even mid-run; already-applied items stay applied.
        var cancelled = await service.CancelAsync(jobId, ct);
        if (!cancelled)
        {
            return Results.Problem(
                $"job '{jobId}' is '{job.Status}' — only a REGISTERED or DRAINING job can be cancelled.",
                statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Ok(new BulkOperationCancelResponse(jobId, Cancelled: true));
    }

    // The wire casing for the matched_set snapshot — the same snake_case the HTTP surface speaks.
    private static readonly JsonSerializerOptions SnakeCaseJson =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record MatchedSetSnapshot(
        string Kind, string SetDigest, long TotalCount, IReadOnlyList<Guid> SampleInstanceIds);
}
