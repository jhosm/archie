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
        var active = await _client.GetFromJsonAsync<DepositPositionResponse>($"/v1/deposits/{depositId}", SnakeCase);
        Assert.NotNull(active);
        Assert.Equal(1_000_000, active.PrincipalCents);
        Assert.Equal(300, active.TanBasisPoints);
        Assert.Equal("pt-deposits-2026.1", active.RateSheetVersionId);
        Assert.Equal("Active", active.Lifecycle);

        // Mature → the canonical AT_MATURITY numbers.
        var maturityResponse = await _client.PostAsJsonAsync(
            $"/v1/deposits/{depositId}/maturity", new MatureDepositRequest(), SnakeCase);
        Assert.Equal(HttpStatusCode.OK, maturityResponse.StatusCode);
        var matured = await maturityResponse.Content.ReadFromJsonAsync<DepositPositionResponse>(SnakeCase);
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
    public async Task Read_model_materialises_and_serves_the_CQRS_query_surface()
    {
        // Constitute a deposit, then assert the D.4 CQRS read model (ADR-IC-005) materialises it
        // asynchronously (the projection relay drains it) and the I.2 query endpoints serve it: the
        // point lookup by id and the maturity-date range scan.
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

        // The read model is async (v1 default), so poll the point-lookup endpoint until the relay
        // drains the constitution event into read_model.deposits (or time out).
        var readModel = await EventuallyAsync(async () =>
        {
            var response = await _client.GetAsync($"/v1/deposits/{depositId}/read-model");
            return response.StatusCode == HttpStatusCode.OK
                ? await response.Content.ReadFromJsonAsync<DepositReadModelResponse>(SnakeCase)
                : null;
        });

        Assert.NotNull(readModel);
        Assert.Equal(depositId, readModel.DepositId);
        Assert.Equal("engine", readModel.Sor);                 // ADR-PC-018 §6.2 routing truth
        Assert.Equal(1_000_000, readModel.PrincipalCents);
        Assert.Equal(300, readModel.TanBasisPoints);
        // The product key is the resolved rate-sheet version, NOT the catalogue product code that
        // was POSTed ("dpz_pt_12m_juros_venc") — the read surface carries no catalogue product_id
        // (bd babelstone-yfr2). Pin the meaning of the product key on the wire response.
        Assert.Equal("pt-deposits-2026.1", readModel.RateSheetVersionId);
        Assert.Equal(maturityDate, readModel.MaturityDate);
        Assert.Equal("Active", readModel.Lifecycle);
        Assert.Equal(0, readModel.LastSequence);               // the constitution event's sequence

        // The maturity range scan returns the deposit when its maturity falls in the window.
        var maturities = await _client.GetFromJsonAsync<DepositMaturitiesResponse>(
            "/v1/deposits/maturities?from=2027-01-01&to=2027-02-01", SnakeCase);
        Assert.NotNull(maturities);
        Assert.Contains(maturities.Deposits, d => d.DepositId == depositId);

        // A window that excludes the maturity date returns no rows.
        var empty = await _client.GetFromJsonAsync<DepositMaturitiesResponse>(
            "/v1/deposits/maturities?from=2030-01-01&to=2030-02-01", SnakeCase);
        Assert.DoesNotContain(empty!.Deposits, d => d.DepositId == depositId);
    }

    [Fact]
    public async Task Reading_an_unknown_read_model_is_404()
    {
        var response = await _client.GetAsync($"/v1/deposits/{Guid.NewGuid()}/read-model");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
