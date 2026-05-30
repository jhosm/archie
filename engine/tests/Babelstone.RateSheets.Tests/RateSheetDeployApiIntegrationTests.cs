using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Babelstone.EventStore.Migrations;
using Babelstone.RateSheets;
using Babelstone.RateSheets.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.RateSheets.Tests;

/// <summary>
/// End-to-end <c>POST /v1/rate-sheets</c> over the real host + a real PostgreSQL
/// (ADR-PC-008 §P2): the idempotency state machine (201 / 200 / 409), deploy-time
/// validation (400), and the §P4 deploy-actor requirement (401).
/// </summary>
[Trait("Category", "Integration")]
public sealed class RateSheetDeployApiIntegrationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions SnakeCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder()
        .WithImage("postgres:18-alpine")
        .Build();

    // ConnectionStrings__RateSheets -> ConnectionStrings:RateSheets, read by the default
    // environment-variables provider at WebApplication.CreateBuilder time. The host reads
    // the connection string before Build(), so a build-time ConfigureAppConfiguration hook
    // would be too late; the env var is the source that is present early enough.
    private const string ConnectionStringEnvVar = "ConnectionStrings__RateSheets";

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await new MigrationRunner(_pg.GetConnectionString()).ApplyAsync();

        Environment.SetEnvironmentVariable(ConnectionStringEnvVar, _pg.GetConnectionString());
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(ConnectionStringEnvVar, null);
        _client.Dispose();
        await _factory.DisposeAsync();
        await _pg.DisposeAsync();
    }

    [Fact]
    public async Task Deploying_a_new_sheet_returns_201_with_the_stored_resource()
    {
        var response = await Post(RateSheetTestData.ValidRequest());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/v1/rate-sheets/pt-deposits-2026.1", response.Headers.Location?.ToString());

        var stored = await response.Content.ReadFromJsonAsync<RateSheetResponse>(SnakeCase);
        Assert.NotNull(stored);
        Assert.Equal("pt-deposits-2026.1", stored.RateSheetVersionId);
        Assert.Equal("deploy-bot", stored.PublishedBy);
        Assert.NotNull(stored.PublishedAt);
    }

    [Fact]
    public async Task Re_posting_an_identical_sheet_is_idempotent_200()
    {
        var request = RateSheetTestData.ValidRequest(versionId: "idem");

        var first = await Post(request);
        var second = await Post(request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task Re_posting_a_different_body_under_the_same_version_id_is_409()
    {
        var versionId = "conflict";
        await Post(RateSheetTestData.ValidRequest(versionId: versionId));

        // Same version id, a changed TAN — a forward-only immutability breach (§P5).
        var mutated = new RateSheetBody
        {
            Products = new()
            {
                ["dpz_pt_12m_juros_venc"] = new()
                {
                    ["standard"] = new RoleRates { Bands = [RateSheetTestData.Band(50_000, null, 999)] },
                },
            },
        };

        var conflict = await Post(RateSheetTestData.ValidRequest(versionId: versionId, body: mutated));

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task An_invalid_sheet_is_rejected_400_before_storage()
    {
        // A gap between bands — rejected at deploy, never at constitution.
        var gapped = new RateSheetBody
        {
            Products = new()
            {
                ["dpz_pt_12m_juros_venc"] = new()
                {
                    ["standard"] = new RoleRates
                    {
                        Bands =
                        [
                            RateSheetTestData.Band(50_000, 5_000_000, 300),
                            RateSheetTestData.Band(6_000_000, null, 350),
                        ],
                    },
                },
            },
        };

        var response = await Post(RateSheetTestData.ValidRequest(versionId: "bad", body: gapped));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_missing_deploy_actor_is_rejected_401()
    {
        var response = await Post(RateSheetTestData.ValidRequest(versionId: "no-actor"), actor: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_mismatched_idempotency_key_is_rejected_400()
    {
        var response = await Post(
            RateSheetTestData.ValidRequest(versionId: "keyed"), idempotencyKey: "not-the-version-id");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<HttpResponseMessage> Post(
        RateSheetDeployRequest request, string? actor = "deploy-bot", string? idempotencyKey = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/rate-sheets")
        {
            Content = JsonContent.Create(request, options: SnakeCase),
        };
        if (actor is not null)
        {
            message.Headers.Add("X-Deploy-Actor", actor);
        }

        if (idempotencyKey is not null)
        {
            message.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await _client.SendAsync(message);
    }
}
