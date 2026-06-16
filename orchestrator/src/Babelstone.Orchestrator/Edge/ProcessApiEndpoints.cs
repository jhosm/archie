using System.Text;
using Babelstone.Orchestrator.Saga;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Babelstone.Orchestrator.Edge;

/// <summary>
/// The I.1 edge-over-saga front door's HTTP routes (ADR-IC-006 §P4 / Document 05 §Step 0), mapped
/// onto the orchestrator's own Kestrel surface. The orchestrator is the application BEHIND the Kong
/// gateway (Boundary 1, ADR-IC-006 §P5): Kong validates the token + SCA claim and proxies these
/// routes; the application starts the saga and enforces per-process ownership.
/// </summary>
/// <remarks>
/// <para>
/// This is the IMPURE HTTP SHELL (ADR-PC-010 §P5): it owns the request/response, the connection, and
/// the SSE stream lifecycle. The pure saga state machine is untouched — the edge START drives it
/// through <see cref="EdgeSagaStarter"/>, and the SSE READ only observes the state the consume loop
/// (#167) mutates.
/// </para>
/// <para>
/// <b>Extraction-ready (ADR-PC-019 §P2).</b> These routes add ASP.NET Core / Kestrel (a framework),
/// NOT an engine-kernel <c>ProjectReference</c> — the orchestrator subtree stays shedable.
/// </para>
/// </remarks>
public static class ProcessApiEndpoints
{
    /// <summary>The constitution-request route (Document 05 §Step 0).</summary>
    public const string ConstituteRoute = "/api/v1/deposits/constitute";

    /// <summary>The SSE stream route — keyed on the public <c>PROC-…</c> reference (Document 05).</summary>
    public const string StreamRoute = "/api/v1/processes/{processId}/stream";

    /// <summary>Map the edge routes onto <paramref name="endpoints"/>.</summary>
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(ConstituteRoute, ConstituteAsync);
        endpoints.MapGet(StreamRoute, StreamAsync);
    }

    // POST /api/v1/deposits/constitute — STARTS the saga, returns 202 + process_id + stream_url.
    private static async Task<IResult> ConstituteAsync(
        ConstituteRequest? request,
        HttpContext context,
        EdgeSagaStarter starter,
        EdgeOptions options,
        CancellationToken ct)
    {
        // Light edge validation (Document 05 §Step 0 step 5): a structurally malformed request is
        // rejected here — the application never starts a saga for it.
        if (request is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "A constitution request body is required.");
        }

        // AUTHZ (ADR-IC-006 §P4 / Document 05 §Step 0): the owning client is the GATEWAY-ATTESTED
        // caller — the signed client_id Kong propagates as EdgeAuth.ClientIdHeader — NOT a
        // client-supplied body field. Binding the owner to the body would let any caller start a saga
        // owned by an arbitrary client_id, defeating the SSE read's ownership check (which binds to
        // this same attested header). Only Kong-fronted, mTLS-authenticated traffic reaches the
        // orchestrator (ADR-IC-006 §P5), so an absent header means the request did not come through
        // the gateway — reject it rather than start an unattributable saga.
        var caller = context.Request.Headers[EdgeAuth.ClientIdHeader].ToString();
        if (string.IsNullOrWhiteSpace(caller))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Missing gateway-attested caller identity.");
        }

        // Structural request validation (Document 05 §Step 0 step 5): the saga's business references
        // are pinned from this body, so a request missing them — or carrying a negative amount —
        // cannot start a coherent saga. Reject here; the application never starts a saga for it.
        if (string.IsNullOrWhiteSpace(request.ProductCode)
            || string.IsNullOrWhiteSpace(request.SourceAccountRef)
            || request.Amount < 0)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "A constitution request requires product_code, source_account_ref, and a non-negative amount.");
        }

        // Assemble the PII-free business facts the saga pins (bd babelstone-t7o3.1): the amount +
        // account/product references come from the body; the approval-fork policy inputs are pinned at
        // the edge — the auto-approval threshold from EdgeOptions (the policy in force at admission)
        // and the client standing resolved here. The substrate treats the gateway-attested caller as
        // an EXISTING relationship (the happy auto-approve path); a future change resolves the standing
        // from the client profile behind the engine boundary (still PII-free here — a closed code).
        //
        // No product-family knowledge here (Fork B rework, bd t7o3.11 / 3k10 / c8d8): the edge pins only
        // the product CODE, not its shape. The engine resolves the term / interest variant / renewal
        // policy / coupon cadence / role from the product code at constitution (the maintainer's Q2
        // choice — the engine is the single home of product config, ADR-PC-009). No clock is needed at
        // the edge: the engine, as the constitution authority, derives the start date from its own
        // constituted_at instant (ADR-PC-008 §S2).
        var businessFacts = new EdgeBusinessFacts(
            ProductRef: request.ProductCode,
            AmountMinorUnits: request.Amount,
            SourceAccountRef: request.SourceAccountRef,
            InterestAccountRef: request.InterestAccountRef,
            ClientType: Babelstone.Families.TermDeposit.Orchestration.ClientType.Existing,
            AutoApprovalThresholdMinorUnits: options.AutoApprovalThresholdMinorUnits);

        // STARTS the saga (NOT a direct engine append): creates the ConstitutionProcess STARTED row
        // owned by the attested caller, pins the business references, drives the first transition,
        // emits the parallel commands — all in one transaction, nothing on the bus. The 202 means the
        // SAGA started (Document 05 §Step 0).
        var result = await starter.StartAsync(options.ConnectionString, caller, businessFacts, correlationId: null, ct);

        var stream = StreamUrlFor(result.PublicProcessId);
        return Results.Accepted(
            stream,
            new ConstituteResponse(result.DepositId, result.PublicProcessId, "PROCESSING", stream));
    }

    // GET /api/v1/processes/{processId}/stream — SSE stream of the saga's state to a terminal state.
    private static async Task StreamAsync(
        string processId,
        HttpContext context,
        SagaStateReader reader,
        IReadOnlyDictionary<string, ISagaStateMachine> machines,
        EdgeOptions options,
        CancellationToken ct)
    {
        // (1) Per-process AUTHZ (ADR-IC-006 §P4 / Document 05 §Step 0): the gateway-attested caller
        // client_id must match the process's OWNING client. process_id is NOT a capability token —
        // an unknown reference is 404, a known reference owned by ANOTHER client is 403 (never leak
        // existence vs ownership beyond what the owner already knows).
        var saga = await reader.ResolveAsync(processId, ct);
        if (saga is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // The terminal check is per-MACHINE (ADR-IC-018 §D3 — no central static predicate). Resolve the
        // saga's state machine by its saga_type (fixed for the life of the stream) from the host's
        // machine registry, then ask it whether each polled state is terminal — the SAME answer the
        // advance handler gives, so the SSE read and the advance loop stay consistent (ADR-IC-018 §P6).
        // A saga_type with no registered machine is a fail-closed wiring error.
        if (!machines.TryGetValue(saga.SagaType, out var machine))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        var caller = context.Request.Headers[EdgeAuth.ClientIdHeader].ToString();
        if (string.IsNullOrEmpty(caller) || !string.Equals(caller, saga.OwningClientId, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // (2) SSE-correct response headers (ADR-IC-006 §P4): text/event-stream, no buffering at the
        // app OR the gateway (X-Accel-Buffering: no disables nginx/Kong buffering for THIS stream),
        // and disable the server's response buffering so each frame flushes immediately.
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        // (3) Stream the saga's STRUCTURAL state progression until a terminal state (Completed /
        // Cancelled / CancelledAfterDebit / DepositConstitutionFailed). The substrate polls the
        // saga_state the consume loop (#167) mutates; a notification hook (LISTEN/NOTIFY, ADR-IC-011)
        // is a later refinement. NO PII crosses the stream — only the business state name + version.
        var emittedVersion = -1L;
        while (!ct.IsCancellationRequested)
        {
            var current = await reader.CurrentAsync(saga.ProcessId, ct);
            if (current is null)
            {
                break; // the saga row vanished (should not happen for a started saga) — close.
            }

            var (state, version) = current.Value;
            if (version != emittedVersion)
            {
                await WriteStateEventAsync(context, machine, processId, state, version, ct);
                emittedVersion = version;
            }

            if (machine.IsTerminal(state))
            {
                break; // terminal state emitted — the stream's job is done.
            }

            if (options.EmitKeepAlive)
            {
                // An SSE comment keeps the long-lived connection (and Kong) from treating a long wait
                // — a saga in AWAIT_WORKFLOW_APPROVAL — as dead (ADR-IC-006 §P4).
                await WriteAsync(context, ": keep-alive\n\n", ct);
            }

            try
            {
                await Task.Delay(options.StreamPollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break; // the client disconnected — stop cleanly.
            }
        }
    }

    private static async Task WriteStateEventAsync(
        HttpContext context, ISagaStateMachine machine, string processId, string state, long version, CancellationToken ct)
    {
        // A named SSE event carrying the STRUCTURAL saga state — process reference, business state
        // name, version, and whether it is terminal. JSON-shaped data on the SSE data: line. PII-free.
        // The state IS the wire string (ADR-IC-018 §D3); terminality is the routed machine's answer.
        var terminal = machine.IsTerminal(state) ? "true" : "false";
        var data =
            $"{{\"process_id\":\"{processId}\",\"state\":\"{state}\",\"version\":{version},\"terminal\":{terminal}}}";
        await WriteAsync(context, $"event: state\ndata: {data}\n\n", ct);
    }

    private static async Task WriteAsync(HttpContext context, string frame, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(frame);
        await context.Response.Body.WriteAsync(bytes, ct);
        await context.Response.Body.FlushAsync(ct);
    }

    private static string StreamUrlFor(string publicProcessId) =>
        $"/api/v1/processes/{publicProcessId}/stream";
}

/// <summary>
/// The 202 body the edge returns (Document 05 §Step 0). Every field is a structural reference, no
/// PII (ADR-PC-004 §P2).
/// </summary>
/// <param name="DepositId">The client-facing <c>DEP-…</c> deposit reference.</param>
/// <param name="ProcessId">The client-facing <c>PROC-…</c> process reference.</param>
/// <param name="Status">The synchronous acceptance status ("PROCESSING").</param>
/// <param name="StreamUrl">The SSE endpoint the client subscribes to for saga progress.</param>
public sealed record ConstituteResponse(string DepositId, string ProcessId, string Status, string StreamUrl);
