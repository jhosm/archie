using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Saga;
using Babelstone.Telemetry;
using Npgsql;

namespace Babelstone.Orchestrator.Dispatch;

/// <summary>
/// The saga command DISPATCHER's drain half (bd babelstone-t7o3.3, ADR-PC-029). The saga DECIDES
/// commands and writes them to <c>saga_outbox</c> (the H.2 write side); nothing delivered them. This
/// drainer is the missing reader: one <see cref="DrainOnceAsync"/> call SELECTs the PENDING tail in
/// emission order (<c>seq</c>), and for each row translates <c>command_type</c> → an HTTP target (the
/// <see cref="ICommandRouter"/> seam), POSTs the row's logical body with <c>Idempotency-Key =
/// message_id</c> (the deterministic command id the engine dedups on, ADR-PC-029 slot 1/4) and the
/// row's W3C <c>traceparent</c> propagated (H.5 / ADR-IC-007 Layer 1), then applies the slot-5 error
/// model and flips the row.
/// </summary>
/// <remarks>
/// <para>
/// <b>The §5 error model is the whole point of the drain.</b>
/// <list type="bullet">
///   <item><b>2xx</b> (applied, or an idempotent replay) → flip the row to <b>PUBLISHED</b>. At-least-once
///   delivery becomes effectively-once because the engine's <c>command_dedup</c> ledger (keyed on the
///   Idempotency-Key) absorbs a redelivery.</item>
///   <item><b>4xx</b> (the engine REFUSES — illegal lifecycle transition / validation) → a TERMINAL
///   delivery outcome. The row is flipped to <b>FAILED</b> with the engine's status + reason recorded,
///   so the saga's compensation path can react (ADR-PC-029 slot 5). It is NEVER silently dropped and
///   NEVER retried forever.</item>
///   <item><b>5xx / timeout / transport error</b> → TRANSIENT. The row stays <b>PENDING</b> and the loop
///   retries; idempotency makes the retry safe.</item>
///   <item><b>No route</b> for the command type → also a TERMINAL FAILED (an undeliverable command),
///   surfaced rather than left to spin.</item>
/// </list>
/// </para>
/// <para>
/// <b>Claim → call → flip in one transaction.</b> Each row is claimed <c>FOR UPDATE SKIP LOCKED</c>
/// so a second dispatcher instance steps over an in-flight row (no double-claim); the HTTP call runs
/// and the PENDING → PUBLISHED/FAILED flip commits in the SAME transaction. A crash between the
/// engine's 2xx and the commit leaves the row PENDING → the next cycle re-POSTs, and the engine's
/// idempotency replays the original 201 (effectively-once). The durable Redpanda bus is untouched:
/// commands ride HTTP point-to-point (Primitive 1, ADR-PC-029 §3), the bus stays events-only.
/// </para>
/// <para>
/// <b>Determinism / purity (ADR-PC-010 §P5).</b> This is the impure SHELL: it owns the connection,
/// the HTTP client, and the clock-stamped <c>published_at</c>/<c>failed_at</c> columns (DB-clock). The
/// command body it sends is the byte-stable logical payload the sink wrote — no value is minted here.
/// </para>
/// </remarks>
public sealed class SagaCommandDispatchDrainer
{
    // Counters on the SHARED Babelstone meter (ADR-IC-007 Layer 1), tagged by command_type only —
    // operational tier, never PII. With no listener, Add is a near no-op.
    private static readonly Counter<long> Delivered =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            "saga.dispatch.delivered",
            description: "Saga commands delivered to their HTTP target and flipped PUBLISHED on a 2xx.");

    private static readonly Counter<long> Refused =
        BabelstoneTelemetry.Meter.CreateCounter<long>(
            "saga.dispatch.refused",
            description: "Saga commands the target REFUSED (4xx) — flipped FAILED (terminal, surfaced for compensation).");

    private readonly SagaCommandDispatcherOptions _options;
    private readonly ICommandRouter _router;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SagaAdvanceHandler _advanceHandler;

    public SagaCommandDispatchDrainer(
        SagaCommandDispatcherOptions options,
        ICommandRouter router,
        IHttpClientFactory httpClientFactory,
        SagaAdvanceHandler advanceHandler)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _advanceHandler = advanceHandler ?? throw new ArgumentNullException(nameof(advanceHandler));
    }

    /// <summary>
    /// Drain one batch of PENDING rows. Returns the number of rows that reached a TERMINAL state
    /// (PUBLISHED or FAILED) this cycle — a transient 5xx/timeout leaves its row PENDING and is NOT
    /// counted, so the host loop knows there is still backlog/backpressure. Each row is processed in
    /// its own claim→call→flip transaction so a slow HTTP call to one target does not hold a lock over
    /// the rest of the batch.
    /// </summary>
    public async Task<int> DrainOnceAsync(CancellationToken ct = default)
    {
        var pendingIds = await ReadPendingSeqAsync(ct);
        var settled = 0;
        foreach (var seq in pendingIds)
        {
            if (await DispatchOneAsync(seq, ct))
            {
                settled++;
            }
        }

        return settled;
    }

    /// <summary>
    /// Claim the row at <paramref name="seq"/> (FOR UPDATE SKIP LOCKED), deliver it over HTTP, and
    /// flip it in the same transaction. Returns true if the row reached a terminal state (PUBLISHED or
    /// FAILED); false if it stayed PENDING (a transient failure to retry, or the row was claimed by a
    /// concurrent dispatcher and skipped).
    /// </summary>
    private async Task<bool> DispatchOneAsync(long seq, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var row = await ClaimAsync(connection, transaction, seq, ct);
        if (row is null)
        {
            // Another dispatcher claimed it (SKIP LOCKED), or it is no longer PENDING — nothing to do.
            await transaction.RollbackAsync(ct);
            return false;
        }

        // No route for this command type → terminal. Two cases:
        var route = _router.Resolve(row.CommandType);
        if (route is null)
        {
            // [REVIEW-FLAG A] The in-aggregate ValidateProductLimits carve-out (bd babelstone-t7o3.8).
            // It has NO HTTP route (the router returns null), but at v1 it AUTO-PASSES so the
            // parallel-validation join can complete and the happy path reach COMPLETED. Treat it as a
            // SYNTHETIC Applied: flip the row PUBLISHED AND self-advance the saga (LimitsValidated) in
            // the same commit. Every OTHER no-route command STILL becomes terminal FAILED below. The
            // real product-limits verdict (incl. LimitsRejected) is H.2 / babelstone-n55u.
            if (row.CommandType == ConstitutionProcess.ValidateProductLimits)
            {
                try
                {
                    await BridgeResultAsync(connection, transaction, row, CommandDeliveryKind.Applied, ct);
                }
                catch (SagaConcurrencyException)
                {
                    // The in-tx self-advance lost the version race: roll back the WHOLE unit (the HTTP
                    // leg was a no-op here anyway), leave the row PENDING, retry next cycle.
                    await transaction.RollbackAsync(ct);
                    return false;
                }

                await MarkPublishedAsync(connection, transaction, seq, ct);
                await transaction.CommitAsync(ct);
                Delivered.Add(1, CommandTag(row.CommandType));
                return true;
            }

            // A genuinely undeliverable command (no route, not the auto-pass): surface it as FAILED
            // rather than spin or drop. No saga self-advance — the bridge synthesizes nothing for it.
            await MarkFailedAsync(connection, transaction, seq, statusCode: 0,
                reason: $"No HTTP route registered for command_type '{row.CommandType}'.", ct);
            await transaction.CommitAsync(ct);
            Refused.Add(1, CommandTag(row.CommandType));
            return true;
        }

        DeliveryOutcome outcome;
        try
        {
            outcome = await DeliverAsync(route, row, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
                                   && !ct.IsCancellationRequested)
        {
            // A transport error or a request timeout (NOT host shutdown) is TRANSIENT (ADR-PC-029
            // slot 5): roll back, leave the row PENDING, and let the host loop retry. Idempotency on
            // the engine's command_dedup makes the retry safe.
            await transaction.RollbackAsync(ct);
            return false;
        }

        // A terminal outcome (Applied/Refused) — the HTTP call happened ONCE this claim. The
        // command-outcome → result-event bridge (bd babelstone-t7o3.8) self-advances the saga IN-PROCESS
        // on the SAME connection+transaction as the status flip, so both land in one commit. The
        // SagaConcurrencyException path rolls the WHOLE unit back (the HTTP leg already ran, but the
        // engine's idempotency replays it on the next re-POST) — only the in-tx advance + flip retry.
        switch (outcome.Kind)
        {
            case DeliveryKind.Applied:
                try
                {
                    await BridgeResultAsync(connection, transaction, row, CommandDeliveryKind.Applied, ct);
                }
                catch (SagaConcurrencyException)
                {
                    await transaction.RollbackAsync(ct);
                    return false;
                }

                // 2xx (applied or idempotent replay) → PUBLISHED.
                await MarkPublishedAsync(connection, transaction, seq, ct);
                await transaction.CommitAsync(ct);
                Delivered.Add(1, CommandTag(row.CommandType));
                return true;

            case DeliveryKind.Refused:
                try
                {
                    await BridgeResultAsync(connection, transaction, row, CommandDeliveryKind.Refused, ct);
                }
                catch (SagaConcurrencyException)
                {
                    await transaction.RollbackAsync(ct);
                    return false;
                }

                // 4xx → terminal FAILED, status + reason recorded for the compensation path. The bridge
                // self-advanced the saga's failure/compensation branch (e.g. ActivationFailed →
                // ReverseCoreDebit, or PreconditionRefused → DEPOSIT_CONSTITUTION_FAILED) in this SAME
                // commit, so the row-FAILED and the saga advance are atomic.
                await MarkFailedAsync(connection, transaction, seq, outcome.StatusCode, outcome.Reason, ct);
                await transaction.CommitAsync(ct);
                Refused.Add(1, CommandTag(row.CommandType));
                return true;

            case DeliveryKind.Indeterminate:
                // Scenario C (bd babelstone-t7o3.10): the ACL returned an EXPLICIT INDETERMINATE signal on
                // the ConfirmDebit (HTTP 202). The command WAS delivered — the ACL accepted it — so the row
                // is terminal as a DELIVERY (PUBLISHED), not a FAILED refusal; what is unknown is the Core's
                // EXECUTION, which the bridge hands to the saga via CoreDebitIndeterminate so it parks in
                // AWAIT_CORE_CLEARANCE and emits the clearance query (ADR-IC-003 §P5, never a blind retry).
                try
                {
                    await BridgeResultAsync(connection, transaction, row, CommandDeliveryKind.Indeterminate, ct);
                }
                catch (SagaConcurrencyException)
                {
                    await transaction.RollbackAsync(ct);
                    return false;
                }

                await MarkPublishedAsync(connection, transaction, seq, ct);
                await transaction.CommitAsync(ct);
                Delivered.Add(1, CommandTag(row.CommandType));
                return true;

            default:
                // 5xx → transient: roll back (the flip never happened), leave PENDING, retry.
                await transaction.RollbackAsync(ct);
                return false;
        }
    }

    /// <summary>
    /// The command-outcome → result-event bridge (bd babelstone-t7o3.8). Map the terminal delivery
    /// outcome of this command to the result-event type the saga consumes (<see cref="ConstitutionResultEvents"/>),
    /// and if non-null, SELF-ADVANCE the saga in-process on the caller's connection+transaction via the
    /// existing <see cref="SagaAdvanceHandler"/> — nothing rides the durable bus (the v1 Core ACL is a
    /// WireMock shim with no event producer; DEF-1 / babelstone-ub9s replaces it). The synthesized
    /// event's message id is DETERMINISTIC (derived from the command's message id + the result type), so
    /// a re-POST of the same PENDING row re-derives the same id and the inbox dedup absorbs the
    /// re-advance — effectively-once. A non-Advanced outcome (NoTransition/Terminal/Duplicate/UnknownSaga)
    /// is a graceful no-op — the command WAS delivered; the advance just did not move the saga.
    /// <see cref="SagaConcurrencyException"/> propagates to the caller, which rolls back and retries.
    /// </summary>
    private async Task BridgeResultAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, OutboxRow row,
        CommandDeliveryKind kind, CancellationToken ct)
    {
        var resultEventType = ConstitutionResultEvents.ForOutcome(row.CommandType, kind);
        if (resultEventType is null)
        {
            // No result event for this (command, kind): the bridge synthesizes nothing — the status
            // flip still commits (the command WAS delivered).
            return;
        }

        var resultMessageId = SagaSettlementResultEmit.MessageId(row.MessageId, resultEventType);
        var evt = new SagaInboxEvent(
            MessageId: resultMessageId,
            ProcessId: row.ProcessId,
            EventType: resultEventType,
            SourceTopic: SagaSettlementResultEmit.SourceTopic,
            CorrelationId: row.CorrelationId,
            TraceParent: row.TraceParent);

        // Self-advance on the SAME connection+transaction as the status flip. AdvanceAsync may return a
        // non-Advanced outcome (a graceful no-op) or throw SagaConcurrencyException (the caller retries).
        await _advanceHandler.AdvanceAsync(connection, transaction, evt, ct);
    }

    /// <summary>
    /// Build and send the HTTP request for one command row: the row's byte-stable logical body as the
    /// JSON payload, <c>Idempotency-Key = message_id</c> (ADR-PC-029 slot 1), and the row's
    /// <c>traceparent</c> propagated (H.5). Classify the response into the slot-5 error model.
    /// </summary>
    private async Task<DeliveryOutcome> DeliverAsync(CommandRoute route, OutboxRow row, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = _options.RequestTimeout;

        using var request = new HttpRequestMessage(route.Method, CombineUrl(route.BaseUrl, route.Path))
        {
            // The byte-stable logical body the sink persisted — sent verbatim, no value minted here.
            Content = new ByteArrayContent(row.Payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        // The deterministic command id: the engine dedups on this so an at-least-once retry replays
        // the original outcome (ADR-PC-029 slot 1/4). TryAddWithoutValidation — the engine parses it.
        request.Headers.TryAddWithoutValidation("Idempotency-Key", row.MessageId.ToString());

        // Propagate the saga's outbound W3C trace context so the engine threads its spans under this
        // saga's trace (H.5 / ADR-IC-007 Layer 1). NULL when no tracer was listening at advance time.
        if (!string.IsNullOrEmpty(row.TraceParent))
        {
            request.Headers.TryAddWithoutValidation("traceparent", row.TraceParent);
        }

        using var response = await client.SendAsync(request, ct);
        var status = (int)response.StatusCode;

        // Scenario C (bd babelstone-t7o3.10): an EXPLICIT INDETERMINATE settlement signal on the
        // irreversible ConfirmDebit. The chosen wire signal is HTTP 202 Accepted — the ACL accepted the
        // debit but cannot yet confirm whether the Core executed it (the network dropped after the debit
        // was sent). 202 is a 2xx, so it would otherwise be classified Applied below; we intercept it
        // FIRST, and ONLY for ConfirmDebit, so it is never confused with a real 2xx-success on any other
        // leg, nor with a 4xx Refused or a 5xx/timeout Transient. The dispatcher flips the row to a
        // terminal status and the bridge self-advances the saga with CoreDebitIndeterminate, parking it in
        // AWAIT_CORE_CLEARANCE (ADR-IC-003 §P5). A ConfirmDebit *timeout* is NOT this — it stays Transient
        // (the catch block leaves the row PENDING for an idempotent retry); INDETERMINATE is an explicit
        // ACL signal, not the absence of a response.
        if (response.StatusCode == HttpStatusCode.Accepted
            && row.CommandType == ConstitutionProcess.ConfirmDebit)
        {
            return DeliveryOutcome.IndeterminateOutcome;
        }

        if (response.IsSuccessStatusCode)
        {
            return DeliveryOutcome.AppliedOutcome;
        }

        // 4xx → terminal refusal (the engine refuses: illegal transition / validation). 5xx →
        // transient. A 3xx (unexpected on a command surface) is treated as transient-non-terminal too,
        // so the loop retries rather than mis-flipping the row FAILED on an unhandled redirect.
        if (response.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError)
        {
            var reason = await ReadReasonAsync(response, ct);
            return DeliveryOutcome.RefusedOutcome(status, reason);
        }

        return DeliveryOutcome.TransientOutcome;
    }

    /// <summary>Capture a bounded, structural reason from the refusal body (a ProblemDetails title /
    /// short message) for the audit trail and the compensation decision — never the request body, and
    /// truncated so a large/hostile body cannot bloat the column. No PII is expected on a refusal
    /// reason (a transition/validation label), and the bound is a defence-in-depth backstop.</summary>
    private static async Task<string> ReadReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return body.Length <= 1024 ? body : body[..1024];
        }
        catch
        {
            return $"HTTP {(int)response.StatusCode}";
        }
    }

    // ---- SQL --------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<long>> ReadPendingSeqAsync(CancellationToken ct)
    {
        // The PENDING tail in EMISSION order (seq, monotone — independent of clock granularity). The
        // partial index saga_outbox_pending_idx keeps this bounded to the unpublished tail. We read
        // the candidate seqs without locking, then each DispatchOne claims its row FOR UPDATE SKIP
        // LOCKED in its own transaction — so a slow HTTP call to one target does not hold a lock over
        // the whole batch, and a concurrent dispatcher claims a disjoint set.
        const string sql = """
            SELECT seq
            FROM saga_outbox
            WHERE status = 'PENDING'
            ORDER BY seq
            LIMIT @batch_size;
            """;

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("batch_size", _options.BatchSize);

        var seqs = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            seqs.Add(reader.GetInt64(0));
        }

        return seqs;
    }

    private static async Task<OutboxRow?> ClaimAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long seq, CancellationToken ct)
    {
        // Re-read the row under FOR UPDATE SKIP LOCKED and re-check status = 'PENDING': a concurrent
        // dispatcher that already claimed it (the lock) or already flipped it (the status) is skipped,
        // so the same command is never delivered twice by two instances. process_id + correlation_id are
        // read so the command-outcome → result-event bridge (bd babelstone-t7o3.8) can correlate the
        // delivery outcome back to the saga and self-advance it on this transaction.
        const string sql = """
            SELECT message_id, command_type, payload, traceparent, process_id, correlation_id
            FROM saga_outbox
            WHERE seq = @seq AND status = 'PENDING'
            FOR UPDATE SKIP LOCKED;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("seq", seq);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new OutboxRow(
            MessageId: reader.GetGuid(0),
            CommandType: reader.GetString(1),
            Payload: reader.GetFieldValue<byte[]>(2),
            TraceParent: reader.IsDBNull(3) ? null : reader.GetString(3),
            ProcessId: reader.GetGuid(4),
            CorrelationId: reader.IsDBNull(5) ? null : reader.GetGuid(5));
    }

    private static async Task MarkPublishedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long seq, CancellationToken ct)
    {
        const string sql = """
            UPDATE saga_outbox
            SET status = 'PUBLISHED', published_at = clock_timestamp()
            WHERE seq = @seq;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("seq", seq);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task MarkFailedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long seq,
        int statusCode, string reason, CancellationToken ct)
    {
        const string sql = """
            UPDATE saga_outbox
            SET status = 'FAILED', failed_at = clock_timestamp(),
                failure_status_code = @code, failure_reason = @reason
            WHERE seq = @seq;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("seq", seq);
        command.Parameters.AddWithValue("code", statusCode);
        command.Parameters.AddWithValue("reason", reason);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string CombineUrl(string baseUrl, string path) =>
        $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

    private static KeyValuePair<string, object?> CommandTag(string commandType)
        => new("command_type", commandType);

    private sealed record OutboxRow(
        Guid MessageId, string CommandType, byte[] Payload, string? TraceParent,
        Guid ProcessId, Guid? CorrelationId);

    private enum DeliveryKind { Applied, Refused, Transient, Indeterminate }

    private sealed record DeliveryOutcome(DeliveryKind Kind, int StatusCode, string Reason)
    {
        public static readonly DeliveryOutcome AppliedOutcome = new(DeliveryKind.Applied, 0, string.Empty);
        public static readonly DeliveryOutcome TransientOutcome = new(DeliveryKind.Transient, 0, string.Empty);
        // Scenario C (bd babelstone-t7o3.10): the explicit ACL INDETERMINATE signal (HTTP 202 on a
        // ConfirmDebit). Terminal-as-delivered; the bridge maps it to CoreDebitIndeterminate.
        public static readonly DeliveryOutcome IndeterminateOutcome = new(DeliveryKind.Indeterminate, 0, string.Empty);
        public static DeliveryOutcome RefusedOutcome(int statusCode, string reason) =>
            new(DeliveryKind.Refused, statusCode, reason);
    }
}
