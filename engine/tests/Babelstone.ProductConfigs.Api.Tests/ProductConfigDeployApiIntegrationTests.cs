using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Babelstone.EventStore.Migrations;
using Babelstone.RateSheets;
using Babelstone.Telemetry;
using Babelstone.TestFixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.ProductConfigs.Api.Tests;

/// <summary>
/// End-to-end <c>POST /v1/product-configs</c> over the real host + a real PostgreSQL (ADR-PC-009 §A2,
/// ADR-PC-008): the idempotency state machine (201 / 200 / 409), the envelope guard (400), and the
/// deploy-actor requirement (401). Mirrors <c>RateSheetDeployApiIntegrationTests</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProductConfigDeployApiIntegrationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions SnakeCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    // ADR-IC-007 §P4: a deploy log carries only operational-tier structural identifiers — none of
    // these fragments as a structured-state key.
    private static readonly string[] PiiKeyFragments =
        ["nif", "iban", "account", "name", "email", "client", "phone", "address", "tax_id"];

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine")
        .Build();

    // ConnectionStrings__ProductConfigs -> ConnectionStrings:ProductConfigs, read by the default
    // environment-variables provider at WebApplication.CreateBuilder time (before Build()).
    private const string ConnectionStringEnvVar = "ConnectionStrings__ProductConfigs";

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _pg.GatedStartAsync();
        await new MigrationRunner(_pg.GetConnectionString()).ApplyAsync();

        Environment.SetEnvironmentVariable(ConnectionStringEnvVar, _pg.GetConnectionString());
        // The host fails fast without an explicit deployment.environment (BabelstoneResource).
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(ConnectionStringEnvVar, null);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", null);
        _client.Dispose();
        await _factory.DisposeAsync();
        await _pg.DisposeAsync();
    }

    [Fact]
    public async Task Deploying_a_new_config_returns_201_with_the_stored_resource()
    {
        var response = await Post(ProductConfigTestData.ValidRequest());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(
            "/v1/product-configs/dpz_pt_12m_juros_venc@2026.1", response.Headers.Location?.ToString());

        var stored = await response.Content.ReadFromJsonAsync<ProductConfigDeployResponse>(SnakeCase);
        Assert.NotNull(stored);
        Assert.Equal("dpz_pt_12m_juros_venc@2026.1", stored.ProductConfigVersionId);
        Assert.Equal("deploy-bot", stored.PublishedBy);
        Assert.NotNull(stored.PublishedAt);
        // The registry minted the content hash server-side — a self-describing sha256:<hex> (bd fk7m.9 bridge).
        Assert.StartsWith("sha256:", stored.ContentHash);
    }

    [Fact]
    public async Task Re_posting_an_identical_config_is_idempotent_200()
    {
        var request = ProductConfigTestData.ValidRequest(versionId: "idem");

        var first = await Post(request);
        var second = await Post(request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task Re_posting_a_different_body_under_the_same_version_id_is_409()
    {
        var versionId = "conflict";
        await Post(ProductConfigTestData.ValidRequest(versionId: versionId));

        // Same version id, a changed term_days — a forward-only immutability breach (ADR-PC-008 §P5).
        var mutated = ProductConfigTestData.ValidBody();
        mutated["term_days"] = 730;

        var conflict = await Post(ProductConfigTestData.ValidRequest(versionId: versionId, body: mutated));

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task A_409_conflict_emits_a_structured_log_with_the_deploy_context()
    {
        var log = new CapturedLogs();
        var client = WithLogCollector(log);
        var versionId = "logged-conflict";
        var when = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        const string actor = "alice@product.internal";

        await Post(client, ProductConfigTestData.ValidRequest(versionId: versionId, effectiveFrom: when), actor: actor);

        var mutated = ProductConfigTestData.ValidBody();
        mutated["term_days"] = 730;
        var conflict = await Post(
            client, ProductConfigTestData.ValidRequest(versionId: versionId, effectiveFrom: when, body: mutated), actor: actor);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var entry = Assert.Single(log.Entries, e => e.EventId == BabelstoneEvents.ProductConfigDeployConflict);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(versionId, entry.State["ProductConfigVersionId"]);
        Assert.Equal("dpz_pt_12m_juros_venc", entry.State["ProductId"]);
        Assert.Equal(when, entry.State["EffectiveFrom"]);
        Assert.Equal(actor, entry.State["DeployActor"]);

        foreach (var key in entry.State.Keys)
        {
            var lowered = key.ToLowerInvariant();
            Assert.DoesNotContain(PiiKeyFragments, fragment => lowered.Contains(fragment));
        }
    }

    [Fact]
    public async Task An_unexpected_store_failure_returns_500_and_emits_a_structured_log()
    {
        var log = new CapturedLogs();
        var boom = new InvalidOperationException("simulated store fault");
        var client = WithStore(new ThrowingProductConfigVersionStore(insertFault: boom), log);
        var when = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        const string actor = "alice@product.internal";

        var response = await Post(
            client, ProductConfigTestData.ValidRequest(versionId: "store-boom", effectiveFrom: when), actor: actor);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertUnexpectedErrorLogged(log, versionId: "store-boom", when: when, actor: actor);
    }

    [Fact]
    public async Task A_just_inserted_config_that_cannot_be_read_back_returns_500_and_emits_a_structured_log()
    {
        var log = new CapturedLogs();
        var client = WithStore(new ThrowingProductConfigVersionStore(insertFault: null), log);
        var when = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        const string actor = "alice@product.internal";

        var response = await Post(
            client, ProductConfigTestData.ValidRequest(versionId: "no-readback", effectiveFrom: when), actor: actor);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertUnexpectedErrorLogged(log, versionId: "no-readback", when: when, actor: actor);
    }

    private static void AssertUnexpectedErrorLogged(
        CapturedLogs log, string versionId, DateTimeOffset when, string actor)
    {
        var entry = Assert.Single(log.Entries, e => e.EventId == BabelstoneEvents.ProductConfigDeployUnexpectedError);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(versionId, entry.State["ProductConfigVersionId"]);
        Assert.Equal("dpz_pt_12m_juros_venc", entry.State["ProductId"]);
        Assert.Equal(when, entry.State["EffectiveFrom"]);
        Assert.Equal(actor, entry.State["DeployActor"]);

        foreach (var key in entry.State.Keys)
        {
            var lowered = key.ToLowerInvariant();
            Assert.DoesNotContain(PiiKeyFragments, fragment => lowered.Contains(fragment));
        }
    }

    [Fact]
    public async Task A_raced_insert_that_claims_the_effective_from_under_another_version_is_409()
    {
        var log = new CapturedLogs();
        var raced = new DuplicateProductConfigVersionException("a-concurrent-version");
        var client = WithStore(new ThrowingProductConfigVersionStore(insertFault: raced), log);
        var when = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        const string actor = "alice@product.internal";

        var response = await Post(
            client, ProductConfigTestData.ValidRequest(versionId: "raced-claim", effectiveFrom: when), actor: actor);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var entry = Assert.Single(log.Entries, e => e.EventId == BabelstoneEvents.ProductConfigDeployConflict);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("raced-claim", entry.State["ProductConfigVersionId"]);
        Assert.Contains("already claimed", entry.State["Detail"]?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_version_id_sharing_a_product_effective_from_is_409()
    {
        var when = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        var first = await Post(ProductConfigTestData.ValidRequest(versionId: "fx-a", effectiveFrom: when));
        // A different version id, same product + effective_from: the
        // product_config_versions_product_effective_uq collision must surface as 409, not 200/500.
        var second = await Post(ProductConfigTestData.ValidRequest(versionId: "fx-b", effectiveFrom: when));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Re_posting_with_sub_microsecond_effective_from_ticks_is_idempotent_200()
    {
        var subMicrosecond = ProductConfigTestData.DefaultEffectiveFrom.AddTicks(1);
        var request = ProductConfigTestData.ValidRequest(versionId: "sub-us", effectiveFrom: subMicrosecond);

        var first = await Post(request);
        var second = await Post(request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task An_empty_body_is_rejected_400_before_storage()
    {
        var response = await Post(ProductConfigTestData.ValidRequest(versionId: "empty-body", body: new JsonObject()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_missing_deploy_actor_is_rejected_401()
    {
        var response = await Post(ProductConfigTestData.ValidRequest(versionId: "no-actor"), actor: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_mismatched_idempotency_key_is_rejected_400()
    {
        var response = await Post(
            ProductConfigTestData.ValidRequest(versionId: "keyed"), idempotencyKey: "not-the-version-id");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private Task<HttpResponseMessage> Post(
        ProductConfigDeployRequest request, string? actor = "deploy-bot", string? idempotencyKey = null) =>
        Post(_client, request, actor, idempotencyKey);

    private static async Task<HttpResponseMessage> Post(
        HttpClient client,
        ProductConfigDeployRequest request,
        string? actor = "deploy-bot",
        string? idempotencyKey = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/product-configs")
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

        return await client.SendAsync(message);
    }

    // A client against a host whose IProductConfigVersionStore is swapped for one that fails the write
    // path, and whose logging fans out to the given collector — so the unexpected-error (500) and the
    // raced-null (409) paths are exercised without a real DB fault.
    private HttpClient WithStore(IProductConfigVersionStore store, CapturedLogs log) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IProductConfigVersionStore>();
                services.AddSingleton(store);
            });
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(log)));
        })
        .CreateClient();

    // Either throws the given fault on InsertAsync, or — when no fault is given — succeeds while
    // TryGetAsync always returns null, exercising the "inserted but could not be read back" invariant.
    private sealed class ThrowingProductConfigVersionStore(Exception? insertFault) : IProductConfigVersionStore
    {
        public Task InsertAsync(ProductConfigVersion version, CancellationToken ct = default) =>
            insertFault is not null ? throw insertFault : Task.CompletedTask;

        public Task<ProductConfigVersion?> TryGetAsync(string productConfigVersionId, CancellationToken ct = default) =>
            Task.FromResult<ProductConfigVersion?>(null);

        public Task<ProductConfigVersionResolution?> ResolveAsync(
            string productId, DateTimeOffset asOf, CancellationToken ct = default) =>
            throw new NotSupportedException("the deploy path does not resolve.");
    }

    private HttpClient WithLogCollector(CapturedLogs log) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(log))))
        .CreateClient();

    private sealed class CapturedLogs
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
    }

    private sealed record LogEntry(
        LogLevel Level, EventId EventId, IReadOnlyDictionary<string, object?> State);

    private sealed class CapturingLoggerProvider(CapturedLogs log) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(log);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturedLogs log) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
                var flattened = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var kvp in values)
                {
                    flattened[kvp.Key] = kvp.Value;
                }

                log.Entries.Enqueue(new LogEntry(logLevel, eventId, flattened));
            }
        }
    }
}
