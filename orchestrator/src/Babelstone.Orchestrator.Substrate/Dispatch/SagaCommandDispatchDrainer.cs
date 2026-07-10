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
///   NEVER retried forever. EXCEPTION — a saga's bridge MAY mark a specific 4xx as RETRIABLE via
///   <see cref="IResultEventBridge.IsRetriableStayPending"/> (reading the ProblemDetails error code): the
///   settlement saga reads a <c>422 SCA_REQUIRED</c> on a cash confirm as retriable, so the row STAYS
///   PENDING for a re-drive on a fresh SCA proof rather than flipping FAILED (ADR-PC-043).</item>
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
    private readonly IReadOnlyDictionary<string, IResultEventBridge> _bridges;

    /// <summary>
    /// Drain saga_outbox for N saga types (bd babelstone-mtto PR1 — the multi-saga substrate). Routing
    /// and the command-outcome → result-event bridge are now keyed by the owning saga's
    /// <c>saga_type</c> (read off the row's <c>saga_state</c> join): <paramref name="router"/> is the
    /// <see cref="CompositeCommandRouter"/> and <paramref name="bridges"/> are the per-saga-type result
    /// bridges. A duplicate <see cref="IResultEventBridge.SagaType"/> is a wiring error and throws.
    /// </summary>
    public SagaCommandDispatchDrainer(
        SagaCommandDispatcherOptions options,
        ICommandRouter router,
        IHttpClientFactory httpClientFactory,
        SagaAdvanceHandler advanceHandler,
        IEnumerable<IResultEventBridge> bridges)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _advanceHandler = advanceHandler ?? throw new ArgumentNullException(nameof(advanceHandler));

        ArgumentNullException.ThrowIfNull(bridges);
        var bridgeMap = new Dictionary<string, IResultEventBridge>(StringComparer.Ordinal);
        foreach (var bridge in bridges)
        {
            if (!bridgeMap.TryAdd(bridge.SagaType, bridge))
            {
                throw new InvalidOperationException(
                    $"Duplicate IResultEventBridge for saga_type '{bridge.SagaType}': the saga-type → " +
                    "bridge registry must be a function (bd babelstone-mtto PR1).");
            }
        }

        _bridges = bridgeMap;
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

        // No route for this command type → terminal. Two cases. Route by the owning saga's saga_type
        // (bd babelstone-mtto PR1) so a second saga's commands resolve through its OWN sub-router.
        var route = _router.Resolve(row.CommandType, row.SagaType);
        if (route is null)
        {
            // [REVIEW-FLAG A] The no-route AUTO-PASS carve-out (bd babelstone-t7o3.8). A no-route command
            // is normally terminal FAILED, but a saga's bridge MAY mark a specific no-route command as a
            // synthetic Applied AUTO-PASS (ADR-IC-018 §P6): flip the row PUBLISHED AND self-advance the
            // saga in the same commit. The constitution saga uses this for its in-aggregate
            // ValidateProductLimits (it has no HTTP route but at v1 auto-passes to LimitsValidated so the
            // parallel-validation join completes and the happy path reaches COMPLETED). The substrate
            // names no family: it asks the routed bridge IsNoRouteAutoPass; every OTHER no-route command
            // STILL becomes terminal FAILED. The real product-limits verdict is H.2 / babelstone-n55u.
            if (_bridges.TryGetValue(row.SagaType, out var noRouteBridge)
                && noRouteBridge.IsNoRouteAutoPass(row.CommandType))
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
                // AWAIT_CORE_CLEARANCE and emits the clearance query (ADR-IC-003 §P4, never a blind retry).
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
    /// outcome of this command to the result-event type the saga consumes (the routed <see cref="IResultEventBridge"/>),
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
        // Resolve the result-event bridge for THIS saga's type (bd babelstone-mtto PR1 — the multi-saga
        // substrate). A saga type with no registered bridge synthesizes nothing — the status flip still
        // commits (the command WAS delivered; the saga simply has no in-process result-event mapping).
        if (!_bridges.TryGetValue(row.SagaType, out var bridge))
        {
            return;
        }

        var resultEventType = bridge.ForOutcome(row.CommandType, kind);
        if (resultEventType is null)
        {
            // No result event for this (command, kind): the bridge synthesizes nothing — the status
            // flip still commits (the command WAS delivered).
            return;
        }

        var resultMessageId = SagaSettlementResultEmit.MessageId(row.MessageId, resultEventType);

        // PROPAGATE the attested step-up-SCA claims forward (bd babelstone-t7o3.19; ADR-PC-032 §A7/§A8). A
        // multi-step settlement leg emits its IRREVERSIBLE cash command (ConfirmDebit / ConfirmCredit) on a
        // LATER advance, driven by THIS synthesized result event (BalanceReserved → ConfirmDebit) — which
        // carries no CloudEvents headers of its own. Re-emitting the delivering command's row SCA on the
        // result event's extension headers lets the next advance re-thread the SAME attestation onto the cash
        // leg's outbox row, so the freshness gate the RECEIVER enforces sees the original proof and re-checks
        // it against SCA_MAX_AGE at the next dispatch instant (never inherited-and-forgotten). The substrate
        // only carries the claims (attest, don't deny — ADR-IC-006 §P2 / ADR-IC-018 §D2). Null SCA ⇒ no
        // headers, so a non-money-mover result event is unchanged. Operational, never PII (ADR-PC-004 §P2).
        var scaHeaders = BuildScaHeaders(row.ScaAcr, row.ScaAuthTime);

        var evt = new SagaInboxEvent(
            MessageId: resultMessageId,
            ProcessId: row.ProcessId,
            EventType: resultEventType,
            SourceTopic: SagaSettlementResultEmit.SourceTopic,
            CorrelationId: row.CorrelationId,
            TraceParent: row.TraceParent,
            ExtensionHeaders: scaHeaders);

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

        using var request = new HttpRequestMessage(route.Method, CombineUrl(route.BaseUrl, route.Path, row.ProcessId))
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

        // Thread the SAME gateway-attested step-up-SCA claims the engine-DIRECT money-movers enforce
        // (bd babelstone-ls44; ADR-IC-010 §P8 A10, ADR-IC-006 §P2 A2) through the saga lane to the
        // SAME engine gate. When a money-mover (maturity / interest) rode the saga carrying fresh SCA,
        // the advance pinned the attested acr/auth_time on the row; we re-emit them here as the
        // X-SCA-Acr / X-SCA-Auth-Time headers the engine's ScaPrecondition gate reads — exactly as Kong
        // attests them on the engine-direct path (the same set_header overwrite-from-the-token pattern,
        // §P3). When NULL (the common case — every non-money-mover command, or a money-mover with no
        // fresh SCA) we send neither header, so the engine gate sees an absent proof and 422s the
        // money-mover BEFORE any side effect — fail-closed, matching the engine-direct behaviour. The
        // engine remains the single freshness authority: it re-checks auth_time against SCA_MAX_AGE at
        // THIS dispatch instant, so a claim that has gone stale (a delayed drain, a crash-recovery
        // re-dispatch) is 422'd at the engine, never settled stale by this row. The header names match
        // the engine's ScaPrecondition (AcrHeader/AuthTimeHeader); the orchestrator stays
        // extraction-ready (ADR-PC-019 §P2) so it cannot reference that engine-side constant directly.
        if (!string.IsNullOrEmpty(row.ScaAcr))
        {
            request.Headers.TryAddWithoutValidation(ScaAcrHeader, row.ScaAcr);
        }

        if (row.ScaAuthTime is { } authTime)
        {
            request.Headers.TryAddWithoutValidation(
                ScaAuthTimeHeader, authTime.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        using var response = await client.SendAsync(request, ct);
        var status = (int)response.StatusCode;

        _bridges.TryGetValue(row.SagaType, out var bridge);

        // Saga-specific status REINTERPRETATION (ADR-IC-018 §P6). A saga's bridge MAY read a particular
        // status on a particular command as a non-default terminal kind — the substrate names no family,
        // it asks the routed bridge. The constitution saga uses this for Scenario C
        // (bd babelstone-t7o3.10): an HTTP 202 Accepted on the irreversible ConfirmDebit is an EXPLICIT
        // INDETERMINATE settlement signal (the ACL accepted the debit but cannot yet confirm whether the
        // Core executed it — the network dropped after the debit was sent). 202 is a 2xx, so it would
        // otherwise be classified Applied below; the bridge intercepts it so the dispatcher flips the row
        // terminal and self-advances with CoreDebitIndeterminate, parking the saga in AWAIT_CORE_CLEARANCE
        // (ADR-IC-003 §P4). A ConfirmDebit *timeout* is NOT this — it stays Transient (the catch block
        // leaves the row PENDING for an idempotent retry); INDETERMINATE is an explicit signal, not the
        // absence of a response. A saga whose bridge returns null here falls through to the default below.
        if (bridge is not null
            && bridge.ClassifyResponse(row.CommandType, status) is { } reinterpreted)
        {
            return reinterpreted switch
            {
                CommandDeliveryKind.Indeterminate => DeliveryOutcome.IndeterminateOutcome,
                CommandDeliveryKind.Applied => DeliveryOutcome.AppliedOutcome,
                CommandDeliveryKind.Refused =>
                    DeliveryOutcome.RefusedOutcome(status, await ReadReasonAsync(response, ct)),
                _ => DeliveryOutcome.TransientOutcome,
            };
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

            // Saga-specific RETRIABLE-4xx carve-out (ADR-PC-043). The bridge MAY read a 4xx body's
            // ProblemDetails error code as a NON-terminal outcome — the leg must stay PENDING and be
            // re-driven rather than flip FAILED. The settlement saga uses this for a 422 SCA_REQUIRED on a
            // cash confirm: the money never moved, so a fresh SCA proof re-drives the SAME leg under the
            // SAME process_id, never dropping the payout (FAILED) and never a fresh occurrence (a double
            // move). Every other 4xx (a genuine decline) still becomes the terminal Refused below. The
            // reason body carries the code, so no extra read is needed.
            if (bridge is not null && bridge.IsRetriableStayPending(row.CommandType, status, reason))
            {
                return DeliveryOutcome.TransientOutcome;
            }

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
        // The PER-AGGREGATE FIFO drain (bd babelstone-t7o3.7, ADR-PC-029 slot 3). We read at most ONE
        // candidate per process_id — the EARLIEST still-PENDING seq for each saga instance (DISTINCT ON
        // (process_id) … ORDER BY process_id, seq), served index-only by saga_outbox_pending_fifo_idx
        // (migration 0007). Delivering only per-process heads means a later seq for an aggregate is never
        // attempted before its earlier seq settles: if the head returns a transient 5xx and stays PENDING,
        // it is STILL the head next cycle, so the same aggregate's next command waits behind it — FIFO per
        // aggregate. DIFFERENT aggregates' heads are independent candidates, so they dispatch in parallel
        // (and a per-process advisory lock in ClaimAsync serialises two pods on the SAME aggregate).
        //
        // The outer ORDER BY seq + LIMIT keeps the cross-aggregate batch bounded and fair (the globally
        // oldest heads first). We read the candidate seqs without locking; each DispatchOne then claims its
        // row FOR UPDATE SKIP LOCKED under that advisory lock in its own transaction — so a slow HTTP call
        // to one target does not hold a lock over the whole batch.
        const string sql = """
            SELECT seq FROM (
                SELECT DISTINCT ON (process_id) seq
                FROM saga_outbox
                WHERE status = 'PENDING'
                ORDER BY process_id, seq
            ) heads
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
        //
        // PER-AGGREGATE FIFO GUARD (bd babelstone-t7o3.7, ADR-PC-029 slot 3). Before the row lock, take a
        // TRANSACTION-scoped advisory lock keyed on the saga instance: a single 64-bit key
        // hashtextextended(process_id::text, FifoLockSalt). Same process_id → same key → the lock
        // SERIALISES two dispatchers (or two in-flight claims) on the SAME aggregate; the loser's try
        // returns false, the row is filtered out (claim skipped, retried next cycle once the holder
        // commits and releases). DIFFERENT process_ids hash to different keys → no contention → they
        // dispatch in PARALLEL. The lock is xact-scoped, so it auto-releases on this claim's commit OR
        // rollback — a transient 5xx that rolls back frees the aggregate immediately for its retry, and a
        // crash mid-claim releases it when the backend connection drops. The single-arg bigint form
        // carries the FULL 64-bit hash (no int4 truncation/overflow), and FifoLockSalt namespaces the key
        // space (as hashtextextended's seed) so a saga-FIFO lock cannot collide with another component's
        // advisory locks on the same cluster. pg_try (not pg_advisory_xact_lock) NEVER blocks the drain
        // thread — a contended aggregate is simply skipped this cycle, never a held connection waiting.
        //
        // The JOIN to saga_state reads the owning saga's saga_type (bd babelstone-mtto PR1 — the
        // multi-saga substrate) so routing and the result bridge can pick the right per-saga-type
        // sub-router/bridge. It is a read-only PK-join (saga_state's PK is process_id), O(1) per row,
        // and needs NO migration — the saga_type column has existed since the original schema. FOR
        // UPDATE OF o locks ONLY the outbox row, never the saga_state row (which the advance handler
        // locks FOR UPDATE on its own).
        const string sql = """
            SELECT o.message_id, o.command_type, o.payload, o.traceparent, o.process_id, o.correlation_id,
                   s.saga_type, o.sca_acr, o.sca_auth_time
            FROM saga_outbox o
            JOIN saga_state s ON s.process_id = o.process_id
            WHERE o.seq = @seq AND o.status = 'PENDING'
              AND pg_try_advisory_xact_lock(hashtextextended(o.process_id::text, @fifo_salt))
            FOR UPDATE OF o SKIP LOCKED;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("seq", seq);
        command.Parameters.AddWithValue("fifo_salt", FifoLockSalt);
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
            CorrelationId: reader.IsDBNull(5) ? null : reader.GetGuid(5),
            SagaType: reader.GetString(6),
            ScaAcr: reader.IsDBNull(7) ? null : reader.GetString(7),
            ScaAuthTime: reader.IsDBNull(8) ? null : reader.GetInt64(8));
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

    /// <summary>
    /// Combine the route base URL + path into the absolute target, substituting any
    /// <c>{process_id}</c> token in the path with the outbox row's <c>process_id</c> (bd babelstone-mtto
    /// PR2). This is the family-agnostic URL-templating seam: a command whose engine endpoint carries the
    /// id in the PATH — e.g. the renewal saga's <c>POST /v1/deposits/{process_id}/constitute-renewal</c>,
    /// where the closing deposit id IS the saga's process_id — declares the token in its
    /// <see cref="CommandRoute.Path"/>, and the dispatcher (which already holds the row's process_id)
    /// fills it. A path with no token is unchanged, so every existing body-based route (e.g.
    /// ActivateDeposit → <c>/v1/deposits</c>) is untouched. The substrate names no family — it knows only
    /// the row's process_id and the single generic token. The process id is a structural reference, not
    /// PII (ADR-PC-004 §P2).
    /// </summary>
    private static string CombineUrl(string baseUrl, string path, Guid processId)
    {
        var resolvedPath = path.Replace(ProcessIdToken, processId.ToString(), StringComparison.Ordinal);
        return $"{baseUrl.TrimEnd('/')}/{resolvedPath.TrimStart('/')}";
    }

    /// <summary>The single generic path-template token the dispatcher substitutes (bd babelstone-mtto
    /// PR2): the saga's process_id. A family route that needs the id in the path declares this literal in
    /// its <see cref="CommandRoute.Path"/>; the substrate fills it from the outbox row.</summary>
    private const string ProcessIdToken = "{process_id}";

    /// <summary>
    /// Build the extension-attribute map that PROPAGATES the attested SCA claims onto a synthesized result
    /// event (bd babelstone-t7o3.19), so a later same-saga advance re-threads them onto the irreversible cash
    /// leg's outbox row. Keys are the ce_-stripped, lowercased projection the consume loop produces and the
    /// advance handler reads (<c>scaacr</c> / <c>scaauthtime</c>); null when no SCA was attested (the result
    /// event then carries no SCA headers — a non-money-mover leg is unchanged). Operational, never PII.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? BuildScaHeaders(string? scaAcr, long? scaAuthTime)
    {
        if (string.IsNullOrEmpty(scaAcr) && scaAuthTime is null)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(scaAcr))
        {
            headers[ScaAcrHeaderKey] = scaAcr;
        }

        if (scaAuthTime is { } authTime)
        {
            headers[ScaAuthTimeHeaderKey] =
                authTime.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return headers;
    }

    /// <summary>The ce_-stripped, lowercased extension-attribute key carrying the attested OIDC <c>acr</c> on a
    /// propagated result event (mirrors <c>SagaAdvanceHandler.ScaAcrHeaderKey</c>).</summary>
    private const string ScaAcrHeaderKey = "scaacr";

    /// <summary>The ce_-stripped, lowercased extension-attribute key carrying the attested OIDC <c>auth_time</c>
    /// (Unix seconds) on a propagated result event (mirrors <c>SagaAdvanceHandler.ScaAuthTimeHeaderKey</c>).</summary>
    private const string ScaAuthTimeHeaderKey = "scaauthtime";

    /// <summary>The gateway-attested SCA-completion class header the dispatcher re-emits for a
    /// money-mover command (bd babelstone-ls44; ADR-IC-010 §P8 A10). MUST match the engine's
    /// <c>ScaPrecondition.AcrHeader</c> — the orchestrator stays extraction-ready (ADR-PC-019 §P2), so it
    /// pins the literal here rather than referencing the engine-side constant; the saga SCA integration
    /// test asserts the header the engine gate reads.</summary>
    private const string ScaAcrHeader = "X-SCA-Acr";

    /// <summary>The gateway-attested SCA freshness header (the OIDC <c>auth_time</c>, Unix seconds) the
    /// dispatcher re-emits for a money-mover command (bd babelstone-ls44). MUST match the engine's
    /// <c>ScaPrecondition.AuthTimeHeader</c>.</summary>
    private const string ScaAuthTimeHeader = "X-SCA-Auth-Time";

    /// <summary>
    /// The namespace SEED of the per-aggregate FIFO advisory lock (bd babelstone-t7o3.7, ADR-PC-029
    /// slot 3). Passed as <c>hashtextextended(process_id::text, FifoLockSalt)</c>, it derives a single
    /// 64-bit advisory-lock key from the saga instance while namespacing the dispatcher's key space — two
    /// components hashing the same text with different seeds land on different keys, so a saga-FIFO lock
    /// cannot collide with another component's advisory locks on the same cluster. The single-arg
    /// <c>pg_try_advisory_xact_lock(bigint)</c> form takes the full 64-bit hash directly (no int4
    /// truncation). An arbitrary but STABLE value — only its reservation for this guard matters.
    /// </summary>
    private const long FifoLockSalt = 0x5A6A_4F58_4649_464FL; // 'ZjOXFIFO' — saga-outbox FIFO guard seed.

    private static KeyValuePair<string, object?> CommandTag(string commandType)
        => new("command_type", commandType);

    private sealed record OutboxRow(
        Guid MessageId, string CommandType, byte[] Payload, string? TraceParent,
        Guid ProcessId, Guid? CorrelationId, string SagaType,
        string? ScaAcr = null, long? ScaAuthTime = null);

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
