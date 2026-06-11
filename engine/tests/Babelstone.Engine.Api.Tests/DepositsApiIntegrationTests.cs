using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Babelstone.Engine.Api;
using Babelstone.EventStore.Migrations;
using Babelstone.RateSheets;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Engine.Api.Tests;

/// <summary>
/// End-to-end constitute→read→mature over the real Babelstone.Engine.Api host + real PostgreSQL
/// (ADR-PC-021 §D5 boundary): the HTTP surface the Python MCP server (E.5) drives. Tagged
/// Integration — runs in the Testcontainers lane (E.6), not the default unit lane.
///
/// This is the acceptance test that exercises ZERO_ENGINE_DIFF_PER_VARIANT (01 §3): a whole
/// term-deposit variant runs constitute→mature end-to-end through the generic engine host with
/// zero <c>/engine</c> diff — the family + pack carry the variant, not the kernel. E.6 asserts
/// the events + projection legs here; the published-messages leg is the E.4 Redpanda round-trip
/// (<c>OutboxToRedpandaIntegrationTests</c>) the same Integration lane now runs.
/// </summary>
[Trait("Category", "Integration")]
[Collection(EngineApiHostCollection.Name)]
public sealed class DepositsApiIntegrationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions SnakeCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await new MigrationRunner(_pg.GetConnectionString()).ApplyAsync();

        // Deploy the rate sheet the constitute flow resolves (300 bps for dpz_pt_12m_juros_venc/standard).
        var rateSheets = new PostgresRateSheetStore(_pg.GetConnectionString());
        await rateSheets.InsertAsync(new RateSheet(
            RateSheetVersionId: "pt-deposits-2026.1",
            ProductFamily: "term_deposit",
            PackVersion: "pt.2026.1",
            EffectiveFrom: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Body: FlatPriced("dpz_pt_12m_juros_venc", "standard", 300),
            ApprovedBy: "alm@bank.pt",
            ApprovalRef: "RC-2026-001",
            PublishedBy: "deploy@bank.pt"));

        Environment.SetEnvironmentVariable("ConnectionStrings__Engine", _pg.GetConnectionString());
        Environment.SetEnvironmentVariable("Engine__PacksDir", PacksDir());
        // The host fails fast without an explicit deployment.environment (BabelstoneResource), so the
        // test declares one — WebApplicationFactory sets the host env via config, not this OS var.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Engine", null);
        Environment.SetEnvironmentVariable("Engine__PacksDir", null);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", null);
        _client.Dispose();
        await _factory.DisposeAsync();
        await _pg.DisposeAsync();
    }

    [Fact]
    public async Task Constitute_read_then_mature_drives_the_full_lifecycle_to_the_canonical_position()
    {
        // Constitute.
        var constituteResponse = await _client.PostAsJsonAsync("/v1/deposits", new ConstituteDepositRequest(
            PrincipalCents: 1_000_000,
            ProductId: "dpz_pt_12m_juros_venc",
            Role: "standard",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            FundingAccount: "PT50-DDA-001"), SnakeCase);

        Assert.Equal(HttpStatusCode.Created, constituteResponse.StatusCode);
        var constituted = await constituteResponse.Content.ReadFromJsonAsync<ConstituteDepositResponse>(SnakeCase);
        Assert.NotNull(constituted);
        Assert.Equal("ACTIVE", constituted.Status);
        var depositId = constituted.DepositId;

        // Read the deposit_position resource — Active, stamped with the resolved TAN + sheet.
        var active = await _client.GetFromJsonAsync<DepositResponse>($"/v1/deposits/{depositId}", SnakeCase);
        Assert.NotNull(active);
        Assert.Equal(1_000_000, active.PrincipalCents);
        Assert.Equal(300, active.TanBasisPoints);
        Assert.Equal("pt-deposits-2026.1", active.RateSheetVersionId);
        Assert.Equal("Active", active.Lifecycle);

        // Mature → the canonical AT_MATURITY numbers.
        var maturityResponse = await _client.PostAsJsonAsync(
            $"/v1/deposits/{depositId}/maturity", new MatureDepositRequest(), SnakeCase);
        Assert.Equal(HttpStatusCode.OK, maturityResponse.StatusCode);
        var matured = await maturityResponse.Content.ReadFromJsonAsync<DepositResponse>(SnakeCase);
        Assert.NotNull(matured);
        Assert.Equal(30_417, matured.AccruedGrossInterestCents);
        Assert.Equal(8_517, matured.WithholdingToDateCents);
        Assert.Equal(21_900, matured.NetInterestCents);
        Assert.Equal(1_021_900, matured.TotalPayoutCents);
        Assert.Equal("Matured", matured.Lifecycle);

        // Assert the durable event log directly, not just the folded projection: the
        // constitute→mature flow appended exactly the four canonical term-deposit events,
        // in order, each paired with an outbox row (ES_ATOMIC_APPEND_OUTBOX) — the "events"
        // leg of E.6's events+projection+published-messages triad, driven through MCP's HTTP
        // surface rather than the runtime directly.
        Assert.Equal(
            ["term_deposit.DepositConstituted", "term_deposit.InterestAccrued",
             "term_deposit.WithholdingApplied", "term_deposit.DepositMatured"],
            await EventTypesAsync(depositId));
        Assert.Equal(4, await CountAsync("events", "stream_id", depositId));
        Assert.Equal(4, await CountAsync("outbox", "aggregate_id", depositId));
    }

    [Fact]
    public async Task Reading_an_unknown_deposit_is_404()
    {
        var response = await _client.GetAsync($"/v1/deposits/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_read_model_materialises_and_the_canonical_GET_and_maturities_scan_serve_it()
    {
        // Constitute, then assert the D.4 CQRS read model (ADR-IC-005) materialises asynchronously (the
        // projection relay drains it) and the I.2 query surface serves it: the maturities range scan AND
        // the ONE canonical point lookup GET /v1/deposits/{id} — there is NO /read-model sibling.
        var maturityDate = new DateOnly(2027, 1, 15);
        var constituteResponse = await _client.PostAsJsonAsync("/v1/deposits", new ConstituteDepositRequest(
            PrincipalCents: 1_000_000,
            ProductId: "dpz_pt_12m_juros_venc",
            Role: "standard",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            FundingAccount: "PT50-DDA-001"), SnakeCase);
        Assert.Equal(HttpStatusCode.Created, constituteResponse.StatusCode);
        var depositId = (await constituteResponse.Content.ReadFromJsonAsync<ConstituteDepositResponse>(SnakeCase))!.DepositId;

        // The maturities scan is served ONLY from the read model (a cross-stream range scan the live
        // fold cannot answer), so the deposit appearing here proves the projector drained the
        // constitution event into read_model.deposits. Poll until it materialises (async, v1 default).
        var fromReadModel = await EventuallyAsync(async () =>
        {
            var maturities = await _client.GetFromJsonAsync<DepositMaturitiesResponse>(
                "/v1/deposits/maturities?from=2027-01-01&to=2027-02-01", SnakeCase);
            return maturities?.Deposits.FirstOrDefault(d => d.DepositId == depositId);
        });

        Assert.NotNull(fromReadModel);
        Assert.Equal("engine", fromReadModel.Sor);                 // ADR-PC-018 §6.2 routing truth
        Assert.Equal(1_000_000, fromReadModel.PrincipalCents);
        Assert.Equal(300, fromReadModel.TanBasisPoints);
        // BOTH product keys under their honest names: the resolved rate-sheet version (price/version
        // key) AND the catalogue product_code that was POSTed, carried end-to-end (POST → decider →
        // fold → read model → HTTP) — bd babelstone-v794.
        Assert.Equal("pt-deposits-2026.1", fromReadModel.RateSheetVersionId);
        Assert.Equal("dpz_pt_12m_juros_venc", fromReadModel.ProductCode);
        Assert.Equal(maturityDate, fromReadModel.MaturityDate);
        Assert.Equal("Active", fromReadModel.Lifecycle);
        Assert.Equal(0, fromReadModel.LastSequence);               // the constitution event's sequence
        // The enriched position (D.4 single-resource): a just-constituted AT_MATURITY deposit has
        // accrued nothing and paid no coupons, so the live financial facts are all zero.
        Assert.Equal(0, fromReadModel.AccruedGrossInterestCents);
        Assert.Equal(0, fromReadModel.WithholdingToDateCents);
        Assert.Equal(0, fromReadModel.NetInterestCents);
        Assert.Equal(0, fromReadModel.CouponsPaid);

        // The ONE canonical point lookup serves the SAME row (no token, projector caught up): identical
        // shape and values, served from the read model — storage never appears in the URL.
        var point = await _client.GetFromJsonAsync<DepositResponse>($"/v1/deposits/{depositId}", SnakeCase);
        Assert.NotNull(point);
        Assert.Equal(depositId, point.DepositId);
        Assert.Equal("dpz_pt_12m_juros_venc", point.ProductCode);
        Assert.Equal("Active", point.Lifecycle);

        // A window that excludes the maturity date returns no rows.
        var empty = await _client.GetFromJsonAsync<DepositMaturitiesResponse>(
            "/v1/deposits/maturities?from=2030-01-01&to=2030-02-01", SnakeCase);
        Assert.DoesNotContain(empty!.Deposits, d => d.DepositId == depositId);
    }

    [Fact]
    public async Task Reading_with_a_commit_token_gives_read_your_writes_before_the_projector_catches_up()
    {
        // The READ_YOUR_WRITES_FOLD_ON_TOKEN fitness function (ADR-PC-027 slot 2/3): constitute and
        // immediately read with If-Min-Sequence = the returned commit_sequence, WITHOUT waiting for the
        // async projector (Option 3). The read model has almost certainly not drained yet, so the
        // canonical GET folds the stream — the authoritative read-your-writes fallback — and returns the
        // just-written deposit: one URL, correct read, no /read-model round-trip and no flaky wait.
        // (Once the projector catches up, the same URL serves the fast read-model row.)
        var constituteResponse = await _client.PostAsJsonAsync("/v1/deposits", new ConstituteDepositRequest(
            PrincipalCents: 1_000_000,
            ProductId: "dpz_pt_12m_juros_venc",
            Role: "standard",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            FundingAccount: "PT50-DDA-001"), SnakeCase);
        Assert.Equal(HttpStatusCode.Created, constituteResponse.StatusCode);
        var constituted = (await constituteResponse.Content.ReadFromJsonAsync<ConstituteDepositResponse>(SnakeCase))!;

        var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/deposits/{constituted.DepositId}");
        request.Headers.TryAddWithoutValidation(
            "If-Min-Sequence", constituted.CommitSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var deposit = await response.Content.ReadFromJsonAsync<DepositResponse>(SnakeCase);
        Assert.NotNull(deposit);
        Assert.Equal(constituted.DepositId, deposit.DepositId);
        Assert.Equal("Active", deposit.Lifecycle);
        Assert.Equal(1_000_000, deposit.PrincipalCents);
        // The answer reflects the caller's own write: last_sequence is at least the commit token.
        Assert.True(deposit.LastSequence >= constituted.CommitSequence);
    }

    private static async Task<T?> EventuallyAsync<T>(Func<Task<T?>> probe) where T : class
    {
        // The async projection relay polls every ~1s; give it generous headroom under a loaded CI box.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var result = await probe();
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(250);
        }

        return null;
    }

    [Fact]
    public async Task Constituting_an_unpriced_product_is_a_422_domain_rejection()
    {
        // The deployed sheet prices only dpz_pt_12m_juros_venc; an unpriced product is a domain
        // rejection (DomainRejectedException) -> 422, never a silent default rate and never a 500.
        var response = await _client.PostAsJsonAsync("/v1/deposits", new ConstituteDepositRequest(
            PrincipalCents: 1_000_000,
            ProductId: "unpriced_product",
            Role: "standard",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            FundingAccount: "PT50-DDA-001"), SnakeCase);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task The_async_command_surface_returns_202_then_the_stream_reports_succeeded_and_the_deposit_reads_back()
    {
        // I.1 (bd babelstone-pxj9): POST the constitution command to the ASYNC surface. It does NOT
        // block on the append — it returns 202 Accepted with a process_id and an SSE stream_url
        // (ADR-IC-006 §Context / Document 05 §Step-0), having kicked the dispatch off on a background
        // task through the SAME engine command path the synchronous POST uses.
        var accepted = await _client.PostAsJsonAsync("/v1/deposits/commands", new ConstituteDepositRequest(
            PrincipalCents: 1_000_000,
            ProductId: "dpz_pt_12m_juros_venc",
            Role: "standard",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            FundingAccount: "PT50-DDA-001"), SnakeCase);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var command = await accepted.Content.ReadFromJsonAsync<CommandAcceptedResponse>(SnakeCase);
        Assert.NotNull(command);
        Assert.Equal("PROCESSING", command.Status);
        Assert.NotEqual(Guid.Empty, command.ProcessId);
        Assert.NotEqual(Guid.Empty, command.DepositId);
        Assert.Equal($"/v1/processes/{command.ProcessId}/stream", command.StreamUrl);

        // Subscribe to the SSE stream and read until it reports a terminal state. The stream replays
        // the current snapshot then streams updates, closing on the terminal one — so this returns the
        // SUCCEEDED snapshot carrying the deposit id + its commit_sequence (the read-your-writes token).
        var terminal = await ReadProcessStreamToTerminalAsync(command.StreamUrl);
        Assert.Equal(ProcessStatus.Succeeded, terminal.Status);
        Assert.Equal(command.DepositId, terminal.AggregateId);
        Assert.NotNull(terminal.CommitSequence);

        // The async dispatch went through the real kernel path: the deposit reads back from the engine,
        // and threading the streamed commit_sequence as If-Min-Sequence gives read-your-writes.
        var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/deposits/{command.DepositId}");
        request.Headers.TryAddWithoutValidation(
            "If-Min-Sequence", terminal.CommitSequence!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var read = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var deposit = await read.Content.ReadFromJsonAsync<DepositResponse>(SnakeCase);
        Assert.NotNull(deposit);
        Assert.Equal(command.DepositId, deposit.DepositId);
        Assert.Equal("Active", deposit.Lifecycle);
        Assert.Equal(1_000_000, deposit.PrincipalCents);
    }

    [Fact]
    public async Task An_async_command_that_a_domain_precondition_rejects_reports_rejected_on_the_stream()
    {
        // The async analogue of the synchronous 422: an unpriced product is a domain rejection. The
        // 202 is still returned (the command was accepted for dispatch), but the dispatch fails the
        // domain precondition, so the stream reaches a terminal REJECTED with the reason — never a
        // phantom SUCCEEDED and never an unobservable swallowed fault.
        var accepted = await _client.PostAsJsonAsync("/v1/deposits/commands", new ConstituteDepositRequest(
            PrincipalCents: 1_000_000,
            ProductId: "unpriced_product",
            Role: "standard",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            FundingAccount: "PT50-DDA-001"), SnakeCase);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var command = (await accepted.Content.ReadFromJsonAsync<CommandAcceptedResponse>(SnakeCase))!;

        var terminal = await ReadProcessStreamToTerminalAsync(command.StreamUrl);
        Assert.Equal(ProcessStatus.Rejected, terminal.Status);
        Assert.NotNull(terminal.Detail);
        Assert.Null(terminal.CommitSequence);
    }

    [Fact]
    public async Task Streaming_an_unknown_process_is_404()
    {
        var response = await _client.GetAsync($"/v1/processes/{Guid.NewGuid()}/stream");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Subscribe to a process SSE stream and read its events until a terminal snapshot arrives,
    /// returning that snapshot. Parses the minimal SSE framing the host emits (one <c>data:</c> JSON
    /// line per event); a generous deadline covers the background dispatch on a loaded CI box.
    /// </summary>
    private async Task<ProcessSnapshot> ReadProcessStreamToTerminalAsync(string streamUrl)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await _client.GetAsync(
            streamUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();
        Assert.StartsWith("text/event-stream", response.Content.Headers.ContentType!.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(cts.Token)) is not null)
        {
            const string dataPrefix = "data:";
            if (!line.StartsWith(dataPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var json = line[dataPrefix.Length..].Trim();
            var snapshot = JsonSerializer.Deserialize<ProcessSnapshot>(json, SnakeCase)!;
            if (snapshot.Status is not ProcessStatus.Processing)
            {
                return snapshot;
            }
        }

        throw new InvalidOperationException("process stream closed without a terminal snapshot");
    }

    private static RateSheetBody FlatPriced(string productId, string role, int tanBasisPoints) => new()
    {
        Products = new Dictionary<string, Dictionary<string, RoleRates>>
        {
            [productId] = new()
            {
                [role] = new RoleRates
                {
                    Bands = [new RateBand(0L, null, tanBasisPoints)],
                },
            },
        },
    };

    private async Task<List<string>> EventTypesAsync(Guid streamId)
    {
        await using var connection = new NpgsqlConnection(_pg.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT event_type FROM events WHERE stream_id = @id ORDER BY sequence_number", connection);
        command.Parameters.AddWithValue("id", streamId);
        var types = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            types.Add(reader.GetString(0));
        }

        return types;
    }

    private async Task<long> CountAsync(string table, string idColumn, Guid id)
    {
        await using var connection = new NpgsqlConnection(_pg.GetConnectionString());
        await connection.OpenAsync();
        // table/idColumn are test-local literals (never request input) — no injection surface.
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE {idColumn} = @id", connection);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static string PacksDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "packs", "pt.2026.1", "pack.yaml")))
        {
            dir = dir.Parent;
        }

        return dir is not null
            ? Path.Combine(dir.FullName, "packs")
            : throw new InvalidOperationException($"repo packs/ not found from {AppContext.BaseDirectory}");
    }
}
