using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Babelstone.EventStore.Migrations;
using Babelstone.Packs;
using Babelstone.RateSheets;
using Babelstone.RateSheets.Api;
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

namespace Babelstone.RateSheets.Tests;

/// <summary>
/// End-to-end <c>POST /v1/rate-sheets</c> over the real host + a real PostgreSQL
/// (ADR-PC-008 §P2): the idempotency state machine (201 / 200 / 409), deploy-time
/// validation (400), and the deploy-actor requirement (401, §P4 + Amendment A3).
/// </summary>
[Trait("Category", "Integration")]
public sealed class RateSheetDeployApiIntegrationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions SnakeCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    // ADR-IC-007 §P4: a deploy log carries only operational-tier structural identifiers — none of
    // these fragments as a structured-state key. Mirrors the span fitness test's PII-key guard.
    private static readonly string[] PiiKeyFragments =
        ["nif", "iban", "account", "name", "email", "client", "phone", "address", "tax_id"];

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine")
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
        await _pg.GatedStartAsync();
        await new MigrationRunner(_pg.GetConnectionString()).ApplyAsync();

        Environment.SetEnvironmentVariable(ConnectionStringEnvVar, _pg.GetConnectionString());
        // The host fails fast without an explicit deployment.environment (BabelstoneResource), so the
        // test declares one — WebApplicationFactory sets the host env via config, not this OS var.
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
    public async Task A_409_conflict_emits_a_structured_log_with_the_deploy_context()
    {
        // Observability (ADR-IC-007 Layer 1): a 409 must leave a server-side record under the stable
        // BabelstoneEvents.RateSheetDeployConflict id, carrying the deploy context (version id, family,
        // effective_from, X-Deploy-Actor) — so the forward-only-immutability breach (§P5) is
        // diagnosable from the logs, not just a bare HTTP 409. Captured with an in-memory ILogger
        // provider, the log analogue of the ActivityListener the span fitness tests use.
        var log = new CapturedLogs();
        var client = WithLogCollector(log);
        var versionId = "logged-conflict";
        var when = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        const string actor = "alice@treasury.internal";

        await Post(client, RateSheetTestData.ValidRequest(versionId: versionId, effectiveFrom: when), actor: actor);

        // Same version id, a changed TAN under a different actor — the §P5 mismatch 409.
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
        var conflict = await Post(
            client, RateSheetTestData.ValidRequest(versionId: versionId, effectiveFrom: when, body: mutated), actor: actor);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var entry = Assert.Single(log.Entries, e => e.EventId == BabelstoneEvents.RateSheetDeployConflict);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(versionId, entry.State["RateSheetVersionId"]);
        Assert.Equal("term_deposit", entry.State["ProductFamily"]);
        Assert.Equal(when, entry.State["EffectiveFrom"]);
        Assert.Equal(actor, entry.State["DeployActor"]);

        // No PII in the structured state — only the operational-tier deploy identifiers (ADR-IC-007 §P4).
        foreach (var key in entry.State.Keys)
        {
            var lowered = key.ToLowerInvariant();
            Assert.DoesNotContain(PiiKeyFragments, fragment => lowered.Contains(fragment));
        }
    }

    [Fact]
    public async Task An_unexpected_store_failure_returns_500_and_emits_a_structured_log()
    {
        // Observability (ADR-IC-007 Layer 1): an unforeseen write fault (here a store InsertAsync
        // that throws) must surface as a 500 AND leave a server-side record under the stable
        // BabelstoneEvents.RateSheetDeployUnexpectedError id, carrying the same deploy context the
        // 409 path records — so the fault is diagnosable from the logs, not just a bare HTTP 500.
        var log = new CapturedLogs();
        var boom = new InvalidOperationException("simulated store fault");
        var client = WithStore(new ThrowingRateSheetStore(insertFault: boom), log);
        var when = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        const string actor = "alice@treasury.internal";

        var response = await Post(
            client, RateSheetTestData.ValidRequest(versionId: "store-boom", effectiveFrom: when), actor: actor);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertUnexpectedErrorLogged(log, versionId: "store-boom", when: when, actor: actor);
    }

    [Fact]
    public async Task A_just_inserted_sheet_that_cannot_be_read_back_returns_500_and_emits_a_structured_log()
    {
        // The read-back invariant (handler line ~140): a row inserted but not re-readable is an
        // invariant violation, not a routine empty result — it must throw, surface as a 500, and be
        // logged under the same stable RateSheetDeployUnexpectedError id with the deploy context.
        var log = new CapturedLogs();
        var client = WithStore(new ThrowingRateSheetStore(insertFault: null), log);
        var when = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        const string actor = "alice@treasury.internal";

        var response = await Post(
            client, RateSheetTestData.ValidRequest(versionId: "no-readback", effectiveFrom: when), actor: actor);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertUnexpectedErrorLogged(log, versionId: "no-readback", when: when, actor: actor);
    }

    // Asserts the single RateSheetDeployUnexpectedError record: Error level, the four deploy-context
    // state keys, and no PII key — the same structured-state contract the 409 test pins for the
    // conflict event (ADR-IC-007 §P4).
    private static void AssertUnexpectedErrorLogged(
        CapturedLogs log, string versionId, DateTimeOffset when, string actor)
    {
        var entry = Assert.Single(log.Entries, e => e.EventId == BabelstoneEvents.RateSheetDeployUnexpectedError);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(versionId, entry.State["RateSheetVersionId"]);
        Assert.Equal("term_deposit", entry.State["ProductFamily"]);
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
        // bd babelstone-z0as: the race-then-409 "effective_from already claimed" branch, made
        // DETERMINISTIC via the in-process store seam (no real concurrent committers needed). The
        // store's pre-insert idempotency probe finds no existing version (TryGetAsync → null), then
        // InsertAsync throws DuplicateRateSheetVersionException — the (product_family, effective_from)
        // unique-key collision a concurrent deploy under a DIFFERENT version id would cause. The
        // handler re-reads by THIS version id, finds null again (the claimant is a different id), and
        // must return 409 with the "effective_from is already claimed" detail, NOT a 200 or 500.
        var log = new CapturedLogs();
        // The store throws the (product_family, effective_from) unique-key collision a concurrent
        // deploy under a DIFFERENT version id would raise; its TryGetAsync always returns null, so
        // the re-read by THIS version id finds no claimant — exactly the raced-null 409 branch.
        var raced = new DuplicateRateSheetVersionException("a-concurrent-version");
        var client = WithStore(new ThrowingRateSheetStore(insertFault: raced), log);
        var when = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        const string actor = "alice@treasury.internal";

        var response = await Post(
            client, RateSheetTestData.ValidRequest(versionId: "raced-claim", effectiveFrom: when), actor: actor);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // The 409 is recorded server-side under the stable conflict id with the "already claimed"
        // detail — diagnosable from the logs, not just a bare HTTP 409 (ADR-IC-007 Layer 1).
        var entry = Assert.Single(log.Entries, e => e.EventId == BabelstoneEvents.RateSheetDeployConflict);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("raced-claim", entry.State["RateSheetVersionId"]);
        Assert.Contains("already claimed", entry.State["Detail"]?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_version_id_sharing_a_family_effective_from_is_409()
    {
        var when = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        var first = await Post(RateSheetTestData.ValidRequest(versionId: "fx-a", effectiveFrom: when));
        // A different version id, same family + effective_from: the rate_sheets_family_effective_uq
        // collision (a NEW version, not an idempotent replay) must surface as 409, not 200/500.
        var second = await Post(RateSheetTestData.ValidRequest(versionId: "fx-b", effectiveFrom: when));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Re_posting_with_sub_microsecond_effective_from_ticks_is_idempotent_200()
    {
        // 1 tick = 100ns — below PostgreSQL's microsecond resolution. The deploy must normalise
        // effective_from so an identical re-POST replays as 200, not a spurious 409.
        var subMicrosecond = RateSheetTestData.DefaultEffectiveFrom.AddTicks(1);
        var request = RateSheetTestData.ValidRequest(versionId: "sub-us", effectiveFrom: subMicrosecond);

        var first = await Post(request);
        var second = await Post(request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
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
    public async Task A_tan_above_the_packs_max_consumer_rate_is_rejected_400()
    {
        // §P2: the bound is the VERIFIED pt.2026.1 pack's max_consumer_rate_bps = 2000 (read off the
        // committed packs/ tree via the default HostPackStore — no live OCI registry needed). A TAN
        // of 2001 bps breaches it, so the deploy is rejected at the boundary, never at constitution.
        var overCeiling = new RateSheetBody
        {
            Products = new()
            {
                ["dpz_pt_12m_juros_venc"] = new()
                {
                    ["standard"] = new RoleRates { Bands = [RateSheetTestData.Band(50_000, null, 2001)] },
                },
            },
        };

        var response = await Post(RateSheetTestData.ValidRequest(versionId: "over-ceiling", body: overCeiling));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_bound_is_keyed_on_pack_version_so_a_tighter_pack_rejects_what_a_looser_one_allows()
    {
        // Two packs differing only in their ceiling: a 1500-bps TAN is within pt.2026.1's [0,2000]
        // but breaches a hypothetical pt.tight's [0,1000]. The same sheet POSTed under each
        // pack_version must deploy under the looser pack and be rejected under the tighter one —
        // proving the bound is resolved from the pack keyed on pack_version, not a host constant.
        var client = WithPackStore(new StubPackStore(new Dictionary<string, int>
        {
            ["pt.2026.1"] = 2000,
            ["pt.tight"] = 1000,
        }));

        var sheet = new RateSheetBody
        {
            Products = new()
            {
                ["dpz_pt_12m_juros_venc"] = new()
                {
                    ["standard"] = new RoleRates { Bands = [RateSheetTestData.Band(50_000, null, 1500)] },
                },
            },
        };

        var loose = await Post(
            client, RateSheetTestData.ValidRequest(versionId: "loose-1500", body: sheet) with { PackVersion = "pt.2026.1" });
        var tight = await Post(
            client, RateSheetTestData.ValidRequest(versionId: "tight-1500", body: sheet) with { PackVersion = "pt.tight" });

        Assert.Equal(HttpStatusCode.Created, loose.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tight.StatusCode);
    }

    [Fact]
    public async Task An_unknown_pack_version_is_a_clean_400_not_a_500()
    {
        // A pack_version the engine never loaded (a stale or typo'd pin) cannot resolve a bound. The
        // deploy must reject it cleanly (400) — a caller error — rather than letting the
        // PackLoadException escape as a 500.
        var response = await Post(
            RateSheetTestData.ValidRequest(versionId: "unknown-pack") with { PackVersion = "pt.9999.1" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_sheet_uncovering_an_active_configs_rate_ref_is_rejected_400()
    {
        // Cross-artefact §2.5: an active config asks for a 'promo' role the worked-example sheet
        // doesn't price, so the deploy is rejected at the boundary (the same 400 validation path
        // as a self-contained shape breach), never surfacing as an unpriceable constitution.
        var withConfig = WithProductConfigs(
        [
            new ActiveProductConfig(
                "dpz_pt_12m_juros_venc",
                [new RateRef("dpz_pt_12m_juros_venc", "promo")]),
        ]);

        var response = await Post(withConfig, RateSheetTestData.ValidRequest(versionId: "uncovered"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_sheet_deploys_201_with_an_empty_product_config_registry()
    {
        // The default IProductConfigSource (EmptyProductConfigSource) reports no active configs, so
        // the cross-artefact checks pass vacuously and the worked-example sheet deploys — the
        // backwards-compatible default that doesn't reject existing deploys (surface §2.5).
        var response = await Post(RateSheetTestData.ValidRequest(versionId: "empty-registry"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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

    private Task<HttpResponseMessage> Post(
        RateSheetDeployRequest request, string? actor = "deploy-bot", string? idempotencyKey = null) =>
        Post(_client, request, actor, idempotencyKey);

    private static async Task<HttpResponseMessage> Post(
        HttpClient client,
        RateSheetDeployRequest request,
        string? actor = "deploy-bot",
        string? idempotencyKey = null)
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

        return await client.SendAsync(message);
    }

    // A client against a host whose IProductConfigSource is swapped for one reporting the given
    // active configs — the seam a registry-backed source (Epic E/F) will replace. The default host
    // wires EmptyProductConfigSource, so the cross-artefact checks are exercised only here.
    private HttpClient WithProductConfigs(IReadOnlyList<ActiveProductConfig> activeConfigs) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IProductConfigSource>();
                services.AddSingleton<IProductConfigSource>(new StubProductConfigSource(activeConfigs));
            }))
        .CreateClient();

    private sealed class StubProductConfigSource(IReadOnlyList<ActiveProductConfig> activeConfigs)
        : IProductConfigSource
    {
        public IReadOnlyList<ActiveProductConfig> Active() => activeConfigs;
    }

    // A client against a host whose IPackStore is swapped for one pre-loaded with the given
    // ceilings — so the §P2 bound under each pack_version is deterministic and exercised without
    // depending on the on-disk packs/ tree. The default host wires the disk-backed HostPackStore.
    private HttpClient WithPackStore(IPackStore packStore) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPackStore>();
                services.AddSingleton(packStore);
            }))
        .CreateClient();

    private sealed class StubPackStore(IReadOnlyDictionary<string, int> ceilings) : IPackStore
    {
        public Task<VerifiedPack> GetAsync(string packVersion, CancellationToken ct = default)
            => throw new NotSupportedException("the stub is pre-loaded; the deploy path only calls Resolve.");

        public VerifiedPack Resolve(string packVersion)
            => ceilings.TryGetValue(packVersion, out var ceiling)
                ? PackTestStubs.WithMaxConsumerRateBps(packVersion, ceiling)
                : throw new PackLoadException(packVersion, null, "not pre-loaded (stub).");
    }

    // A client against a host whose IRateSheetStore is swapped for one that fails the write path,
    // and whose logging fans out to the given collector — so the unexpected-error (500) path and its
    // stable RateSheetDeployUnexpectedError record are exercised without a real DB fault. The default
    // host wires the PostgreSQL-backed store; this swap drives the catch-all in the deploy handler.
    private HttpClient WithStore(IRateSheetStore store, CapturedLogs log) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRateSheetStore>();
                services.AddSingleton(store);
            });
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(log)));
        })
        .CreateClient();

    // A store that drives the deploy handler's unexpected-error branch: InsertAsync either throws the
    // given fault (the dropped-DB / serialization-fault analogue) or — when no fault is given —
    // succeeds while TryGetAsync always returns null, exercising the "inserted but could not be read
    // back" invariant violation (handler line ~140). TryGetAsync returns null so the pre-insert
    // idempotency probe finds no existing sheet and the write path is taken.
    private sealed class ThrowingRateSheetStore(Exception? insertFault) : IRateSheetStore
    {
        public Task InsertAsync(RateSheet sheet, CancellationToken ct = default) =>
            insertFault is not null ? throw insertFault : Task.CompletedTask;

        public Task<RateSheet?> TryGetAsync(string rateSheetVersionId, CancellationToken ct = default) =>
            Task.FromResult<RateSheet?>(null);

        public Task<RateSheetResolution?> ResolveAsync(
            string productFamily, DateTimeOffset asOf, CancellationToken ct = default) =>
            throw new NotSupportedException("the deploy path does not resolve.");
    }

    // A client against a host whose logging fans out to an in-memory collector, so a test can assert
    // a structured log record (EventId + structured state) the deploy handler emits — the log
    // analogue of the ActivitySource ActivityListener the span fitness tests attach.
    private HttpClient WithLogCollector(CapturedLogs log) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(log))))
        .CreateClient();

    // The captured logs and one record's level + event id + flattened structured state (the
    // {Name} placeholders become state keys). Concurrent because the host logs from request threads.
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
                // The message-template arguments arrive as the structured state — capture them as the
                // {Placeholder} -> value map the assertions read (the Detail/{OriginalFormat} entries
                // ride along untouched).
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
