using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Babelstone.Engine.Api;
using Babelstone.EventStore.Migrations;
using Babelstone.RateSheets;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Engine.Api.Tests;

/// <summary>
/// End-to-end constitute→read→mature over the real Babelstone.Engine.Api host + real PostgreSQL
/// (ADR-PC-021 §D5 boundary): the HTTP surface the Python MCP server (E.5) drives. Tagged
/// Integration — runs in the Testcontainers lane (E.6), not the default unit lane.
/// </summary>
[Trait("Category", "Integration")]
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
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Engine", null);
        Environment.SetEnvironmentVariable("Engine__PacksDir", null);
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
    }

    [Fact]
    public async Task Reading_an_unknown_deposit_is_404()
    {
        var response = await _client.GetAsync($"/v1/deposits/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
                    Bands = [new RateBand { PrincipalCents = [0L, null], TanBasisPoints = tanBasisPoints }],
                },
            },
        },
    };

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
