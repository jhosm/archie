using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Babelstone.Orchestrator.Edge;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Outbox;
using Babelstone.Orchestrator.Saga;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Npgsql;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// The I.1 edge-over-saga front door (ADR-IC-006 §P4 / Document 05 §Step 0), driven against a
/// real PostgreSQL. The intended model: a client hits the EDGE (here, the orchestrator's own
/// HTTP surface — the application behind the Kong boundary, ADR-IC-006), which STARTS the
/// <see cref="ConstitutionProcess"/> saga and returns 202 + a <c>process_id</c> + a
/// <c>stream_url</c>; the SSE stream follows the saga to a terminal state.
/// </summary>
/// <remarks>
/// <para>
/// The 202 means the SAGA started — NOT a direct engine append (PR #149's rejected anti-pattern).
/// The edge creates the durable <c>ConstitutionProcess</c> STARTED row and drives the first
/// transition (STARTED + ConstitutionRequested → PARALLEL_VALIDATION) IN-PROCESS within one
/// transaction, emitting the two parallel validation commands to <c>saga_outbox</c>. It puts
/// NOTHING on the durable bus (the bus stays events-only); the existing consume loop (#167) then
/// advances the saga on the validation result events.
/// </para>
/// <para>
/// Per-process authz (ADR-IC-006 §P4 / Document 05 §Step 0 "SSE endpoint authorization note"):
/// the <c>process_id</c> in the URL is NOT a capability token. The SSE endpoint independently
/// validates that the requester's <c>client_id</c> matches the process's OWNING client — a guessed
/// or stolen <c>process_id</c> must not yield another client's saga updates.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(nameof(OrchestratorPostgresCollection))]
public sealed class EdgeProcessApiIntegrationTests(OrchestratorPostgresFixture fixture)
{
    private const string OwningClient = "CLI-2026-007842";

    [Fact]
    public async Task Post_constitute_returns_202_with_a_process_id_and_starts_the_saga()
    {
        await using var edge = NewEdge();
        using var client = edge.Client();

        var response = await client.PostAsJsonAsync(
            "/api/v1/deposits/constitute",
            new
            {
                client_id = OwningClient,
                product_code = "TD-TRAD-12M",
                amount = 1_000_000,
                source_account_ref = "acct-ref-001",
                interest_account_ref = "acct-ref-001",
            });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var processId = body.GetProperty("process_id").GetString()!;
        var depositId = body.GetProperty("deposit_id").GetString()!;
        var streamUrl = body.GetProperty("stream_url").GetString()!;

        // The public process id is a PROC-… reference (Document 05), and the stream_url points at it.
        Assert.StartsWith("PROC-", processId);
        Assert.StartsWith("DEP-", depositId);
        Assert.Equal($"/api/v1/processes/{processId}/stream", streamUrl);

        // The durable ConstitutionProcess saga exists, started and driven into PARALLEL_VALIDATION
        // (the start event drove the first transition), with the owning client persisted.
        var saga = await LoadByPublicIdAsync(processId);
        Assert.NotNull(saga);
        Assert.Equal(SagaState.ParallelValidation, saga!.State);
        Assert.Equal(OwningClient, saga.OwningClientId);

        // The two parallel validation commands were produced to saga_outbox — NOT to the bus.
        Assert.Equal(
            new[] { ConstitutionProcess.ReserveAccountBalance, ConstitutionProcess.ValidateProductLimits },
            await OutboxCommandsAsync(saga.ProcessId));
    }

    [Fact]
    public async Task Sse_stream_emits_the_saga_state_and_terminates_on_a_terminal_state()
    {
        await using var edge = NewEdge();
        using var client = edge.Client();

        var processId = await StartProcessAsync(client);
        var saga = (await LoadByPublicIdAsync(processId))!;

        // Open the SSE stream as the owning client (Kong validated the token; the app enforces
        // ownership via the propagated client_id, ADR-IC-006 §P4).
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/processes/{processId}/stream");
        request.Headers.Add(EdgeAuth.ClientIdHeader, OwningClient);
        using var stream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Equal("text/event-stream", stream.Content.Headers.ContentType!.MediaType);

        // Drive the saga to a terminal state out of band (a precondition refusal → terminal
        // DEPOSIT_CONSTITUTION_FAILED with no reversal), simulating the consume loop's advance.
        await DriveToTerminalAsync(saga.ProcessId);

        // The stream emits the saga's structural state progression and then closes on the terminal
        // state. Read it to completion (the endpoint ends the response at a terminal state).
        var text = await ReadStreamToEndAsync(stream, TimeSpan.FromSeconds(30));

        Assert.Contains("PARALLEL_VALIDATION", text);
        Assert.Contains("DEPOSIT_CONSTITUTION_FAILED", text);
        // No PII ever crosses the stream — only structural saga state (ADR-PC-004 §P2).
        Assert.DoesNotContain(OwningClient, text);
    }

    [Fact]
    public async Task Sse_stream_rejects_a_requester_whose_client_id_is_not_the_owner()
    {
        // ADR-IC-006 §P4 / Document 05 §Step 0: a client that guesses or obtains another client's
        // process_id must NOT receive their saga updates — process_id is not a capability token.
        await using var edge = NewEdge();
        using var client = edge.Client();

        var processId = await StartProcessAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/processes/{processId}/stream");
        request.Headers.Add(EdgeAuth.ClientIdHeader, "CLI-2026-999999"); // a DIFFERENT client
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Sse_stream_for_an_unknown_process_id_is_404()
    {
        await using var edge = NewEdge();
        using var client = edge.Client();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/processes/PROC-2026-NOPE/stream");
        request.Headers.Add(EdgeAuth.ClientIdHeader, OwningClient);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- helpers -----------------------------------------------------------------------

    private EdgeHost NewEdge() => new(fixture.ConnectionString);

    private static async Task<string> StartProcessAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/deposits/constitute",
            new
            {
                client_id = OwningClient,
                product_code = "TD-TRAD-12M",
                amount = 1_000_000,
                source_account_ref = "acct-ref-001",
                interest_account_ref = "acct-ref-001",
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("process_id").GetString()!;
    }

    private async Task DriveToTerminalAsync(Guid processId)
    {
        // Mirror the consume loop's advance: PARALLEL_VALIDATION + PreconditionRefused →
        // DEPOSIT_CONSTITUTION_FAILED (a terminal state, no reversal). Use the real state store.
        var stateStore = new SagaStateStore();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var saga = await stateStore.LoadAsync(connection, tx, processId);
        Assert.NotNull(saga);
        Assert.True(await stateStore.TryAdvanceAsync(
            connection, tx, processId, saga!.Version, SagaState.DepositConstitutionFailed));
        await tx.CommitAsync();
    }

    private static async Task<string> ReadStreamToEndAsync(HttpResponseMessage response, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await using var body = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(body);
        var sb = new System.Text.StringBuilder();
        var buffer = new char[256];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer, cts.Token)) > 0)
            {
                sb.Append(buffer, 0, read);
                if (sb.ToString().Contains("DEPOSIT_CONSTITUTION_FAILED"))
                {
                    break; // the terminal frame arrived; the endpoint will close after it
                }
            }
        }
        catch (OperationCanceledException)
        {
            // fall through — assertions on what we accumulated
        }

        return sb.ToString();
    }

    private async Task<SagaInstance?> LoadByPublicIdAsync(string publicProcessId)
    {
        var stateStore = new SagaStateStore();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var saga = await stateStore.LoadByPublicIdAsync(connection, tx, publicProcessId);
        await tx.RollbackAsync();
        return saga;
    }

    private async Task<string[]> OutboxCommandsAsync(Guid processId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT command_type FROM saga_outbox WHERE process_id = @p ORDER BY seq;", connection);
        command.Parameters.AddWithValue("p", processId);

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return [.. rows];
    }

    /// <summary>
    /// A self-contained Kestrel host running ONLY the edge HTTP surface (the process API
    /// endpoints) over the test PG container — no Kafka, no consume loop — so the test exercises
    /// the 202/saga-start + the SSE wiring in isolation. The production <c>Program.cs</c> composes
    /// these SAME endpoints alongside the hosted consume loop + dispatcher.
    /// </summary>
    private sealed class EdgeHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _baseAddress;

        public EdgeHost(string connectionString)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            EdgeServices.Register(builder.Services, connectionString);
            _app = builder.Build();
            ProcessApiEndpoints.Map(_app);
            _app.StartAsync().GetAwaiter().GetResult();
            _baseAddress = _app.Urls.First();
        }

        public HttpClient Client() => new() { BaseAddress = new Uri(_baseAddress) };

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
