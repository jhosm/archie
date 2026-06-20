using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Babelstone.Orchestrator.Edge;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Families.TermDeposit.Orchestration;
using Babelstone.Orchestrator.Saga;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
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

        // The owning client is the gateway-attested caller (X-Client-Id), NOT a body field.
        using var response = await PostConstituteAsync(client, attestedClientId: OwningClient);

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
        Assert.Equal(ConstitutionProcess.States.ParallelValidation, saga!.State);
        Assert.Equal(OwningClient, saga.OwningClientId);

        // The two parallel validation commands were produced to saga_outbox — NOT to the bus.
        Assert.Equal(
            new[] { ConstitutionProcess.ReserveAccountBalance, ConstitutionProcess.ValidateProductLimits },
            await OutboxCommandsAsync(saga.ProcessId));
    }

    [Fact]
    public async Task Post_constitute_without_the_gateway_attested_client_id_is_403()
    {
        // ADR-IC-006 §P4 / §P5: the owning client is the gateway-attested caller (X-Client-Id),
        // never a client-supplied body field. A request that did NOT come through Kong (no attested
        // header) must not start a saga owned by an arbitrary/unattributable client_id — otherwise a
        // caller could mint a saga under any owner and the SSE read's ownership check (bound to this
        // same header) would be meaningless.
        await using var edge = NewEdge();
        using var client = edge.Client();

        using var response = await PostConstituteAsync(client, attestedClientId: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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

    // --- GET /status — the agent-channel poll-once sibling of the stream (Document 11 Pattern 2; vjoi) ---

    [Fact]
    public async Task Status_for_a_started_process_is_processing_and_not_terminal()
    {
        await using var edge = NewEdge();
        using var client = edge.Client();

        var processId = await StartProcessAsync(client);

        using var response = await GetStatusAsync(client, processId, attestedClientId: OwningClient);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(processId, body.GetProperty("process_id").GetString());
        // The verbatim family state AND the coarse agent status — the edge returns both (ADR-IC-018 §D3).
        Assert.Equal(ConstitutionProcess.States.ParallelValidation, body.GetProperty("state").GetString());
        Assert.Equal(AgentStatus.Processing, body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("terminal").GetBoolean());
        // No PII crosses the snapshot — only structural state (ADR-PC-004 §P2).
        Assert.DoesNotContain(OwningClient, body.GetRawText());
    }

    [Fact]
    public async Task Status_reflects_a_terminal_state_as_failed_and_terminal()
    {
        await using var edge = NewEdge();
        using var client = edge.Client();

        var processId = await StartProcessAsync(client);
        var saga = (await LoadByPublicIdAsync(processId))!;

        // Drive the saga to a terminal failure out of band (mirrors the consume loop's advance), then poll.
        await DriveToTerminalAsync(saga.ProcessId);

        using var response = await GetStatusAsync(client, processId, attestedClientId: OwningClient);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ConstitutionProcess.States.DepositConstitutionFailed, body.GetProperty("state").GetString());
        Assert.Equal(AgentStatus.Failed, body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("terminal").GetBoolean());
    }

    [Fact]
    public async Task Status_for_an_unknown_process_id_is_404()
    {
        await using var edge = NewEdge();
        using var client = edge.Client();

        using var response = await GetStatusAsync(client, "PROC-2026-NOPE", attestedClientId: OwningClient);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Status_rejects_a_requester_whose_client_id_is_not_the_owner()
    {
        // Same per-process ownership as the stream: process_id is not a capability token (ADR-IC-006 §P4).
        await using var edge = NewEdge();
        using var client = edge.Client();

        var processId = await StartProcessAsync(client);

        using var response = await GetStatusAsync(client, processId, attestedClientId: "CLI-2026-999999");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- helpers -----------------------------------------------------------------------

    private static Task<HttpResponseMessage> GetStatusAsync(
        HttpClient client, string processId, string? attestedClientId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/processes/{processId}/status");
        if (attestedClientId is not null)
        {
            request.Headers.Add(EdgeAuth.ClientIdHeader, attestedClientId);
        }

        return client.SendAsync(request);
    }

    private EdgeHost NewEdge() => new(fixture.ConnectionString);

    private static async Task<string> StartProcessAsync(HttpClient client)
    {
        using var response = await PostConstituteAsync(client, attestedClientId: OwningClient);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("process_id").GetString()!;
    }

    // POST the constitution request, attaching the gateway-attested caller as the X-Client-Id header
    // (what Kong propagates, ADR-IC-006 §P5) when supplied. The owning client is taken from that
    // header, NOT the body — so the body carries only the deposit references.
    private static Task<HttpResponseMessage> PostConstituteAsync(HttpClient client, string? attestedClientId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/deposits/constitute")
        {
            Content = JsonContent.Create(new
            {
                product_code = "TD-TRAD-12M",
                amount = 1_000_000,
                source_account_ref = "acct-ref-001",
                interest_account_ref = "acct-ref-001",
            }),
        };
        if (attestedClientId is not null)
        {
            request.Headers.Add(EdgeAuth.ClientIdHeader, attestedClientId);
        }
        return client.SendAsync(request);
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
            connection, tx, processId, saga!.Version, ConstitutionProcess.States.DepositConstitutionFailed));
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

            // Compose the term-deposit saga module exactly as the host does (ADR-IC-018 §P4): register the
            // module's family-owned services (business-ref store + outbox sink) and the machine/bridge/router
            // it contributes, plus the saga_type → machine registry the SSE read resolves terminality through.
            // EdgeServices then wires the edge starter + SSE reader over those.
            var context = new SagaModuleContext(connectionString, "http://engine", "http://settlement");
            var module = new TermDepositSagaModule(context);
            module.ConfigureServices(builder.Services, context);
            // The saga_outbox store's write side (ADR-IC-018 §D2) — registered as the host does, so the
            // constitution typed sink can compose it (the row write moved off the family sink to the substrate).
            builder.Services.AddSingleton<SagaOutboxWriter>();
            builder.Services.AddSingleton(module.StateMachine);
            builder.Services.AddSingleton(module.ResultEventBridge);
            builder.Services.AddSingleton(module.CommandRouter);
            builder.Services.AddSingleton<IReadOnlyDictionary<string, ISagaStateMachine>>(sp =>
                sp.GetServices<ISagaStateMachine>().ToDictionary(m => m.SagaType, StringComparer.Ordinal));
            // The saga_type → agent-status-map registry the process-status endpoint resolves (bd
            // babelstone-vjoi) — module.ConfigureServices above registered the ISagaAgentStatusMap, exactly
            // as the production host does.
            builder.Services.AddSingleton<IReadOnlyDictionary<string, ISagaAgentStatusMap>>(sp =>
                sp.GetServices<ISagaAgentStatusMap>().ToDictionary(m => m.SagaType, StringComparer.Ordinal));

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
