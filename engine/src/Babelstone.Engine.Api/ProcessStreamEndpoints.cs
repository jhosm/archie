namespace Babelstone.Engine.Api;

/// <summary>
/// The process-stream surface behind the asynchronous command contract (I.1, bd babelstone-pxj9):
/// the SSE endpoint a client subscribes to after a command POST returns <c>202 Accepted</c> with a
/// <c>stream_url</c>. Per <see href="../../../../docs/product-management/integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md">ADR-IC-006</see>
/// §Context and Document 05 §Step-0, the URL is <c>/v1/processes/{process_id}/stream</c> and the
/// response is a long-running <c>text/event-stream</c> that stays open until the process reaches a
/// terminal state.
/// </summary>
/// <remarks>
/// <para>
/// This is FAMILY-AGNOSTIC host infrastructure: it streams an opaque <see cref="ProcessSnapshot"/>,
/// so a second family's async commands reuse the same endpoint unchanged (the family side only
/// supplies the dispatch closure on its command route). It is the engine host's own async-command
/// progress feed — NOT the cross-context constitution saga's SSE (Epic H, <c>orchestrator/</c>); the
/// gateway (Epic J / ADR-IC-006 §P4) owns buffering passthrough and the per-client authorization the
/// note in Document 05 §Step-0 requires. This auth-deferred dev host (ADR-PC-021 §D5 revision) does
/// not treat the process_id as a secret.
/// </para>
/// <para>
/// The handler stays in the impure host shell; no clock/I/O/randomness reaches a pure decider/fold.
/// Each event's <c>data:</c> is the snapshot serialized with the host's snake_case JSON options.
/// </para>
/// </remarks>
public static class ProcessStreamEndpoints
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/v1/processes/{processId:guid}/stream", StreamAsync);

    private static IResult StreamAsync(Guid processId, ProcessRegistry processes, CancellationToken ct)
    {
        // An unknown process id is a 404 — distinguishable up front from a known process that is still
        // PROCESSING, so a client that mistypes the id gets a clean error rather than an empty stream.
        if (processes.Snapshot(processId) is null)
        {
            return Results.NotFound();
        }

        // A long-running text/event-stream (ADR-IC-006 §P4): the framework keeps the connection open,
        // writing each snapshot as `event: <status> \n data: <json>` and flushing per item, until the
        // registry's subscription completes on the terminal snapshot. The SseItem<T> overload (no
        // eventType arg) serializes ONLY each item's value into `data:` and reads the per-item
        // EventType into `event:` — so each event names its lifecycle state and a client can switch on
        // it without parsing the payload.
        return TypedResults.ServerSentEvents(Events(processes, processId, ct));
    }

    private static async IAsyncEnumerable<System.Net.ServerSentEvents.SseItem<ProcessSnapshot>> Events(
        ProcessRegistry processes,
        Guid processId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var snapshot in processes.SubscribeAsync(processId, ct).ConfigureAwait(false))
        {
            // The SSE `event:` field names the lifecycle state (processing / succeeded / rejected /
            // failed) so a client can switch on it without parsing the JSON `data:` payload.
            yield return new System.Net.ServerSentEvents.SseItem<ProcessSnapshot>(
                snapshot, eventType: snapshot.Status.ToString().ToLowerInvariant());
        }
    }
}
