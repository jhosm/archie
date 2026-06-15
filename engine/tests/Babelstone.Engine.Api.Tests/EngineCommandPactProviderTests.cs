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
/// The PROVIDER-verification half of the dispatcher↔engine command contract (ENGINE_COMMAND_PACT,
/// ADR-PC-029 slot 6 / ADR-IC-009). The orchestrator's command dispatcher is the consumer; the engine
/// <c>POST /v1/deposits</c> command surface is the provider. The consumer side
/// (<c>EngineCommandPactConsumerTests</c> in the orchestrator test project) declares the request the
/// dispatcher makes; THIS test replays that same contract shape against the REAL engine via
/// <c>WebApplicationFactory&lt;Program&gt;</c> and asserts the engine HONOURS every clause — so a
/// provider-side break (dropping the mandatory Idempotency-Key, changing the snake_case body or the
/// 201 shape, breaking replay) fails this build.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pact-STYLE CDC, not the PactNet FFI.</b> PactNet carries a native Rust FFI that must
/// download/bundle per-platform on CI; a greenfield Pact broker harness is a larger, CI-fragile
/// change than this lane should land. So the contract is pinned in code — the consumer produces
/// against it, this provider verification replays it against the real engine — without the native
/// dependency. The catalogue row 20 (<c>ENGINE_COMMAND_PACT</c>) therefore stays <c>Planned</c> with
/// a clear note: the formal PactNet harness + broker is the follow-up; the contract is concretely
/// held by the consumer + provider tests in the meantime, never faked Live.
/// </para>
/// <para>
/// The clauses verified here mirror <c>EngineCommandContract</c> on the consumer side: the mandatory
/// <c>Idempotency-Key</c> (a UUID; 400 on absent/malformed, ADR-PC-029 slot 1), the snake_case
/// request body the engine's SnakeCaseLower options accept, the 201 + <c>ConstituteDepositResponse</c>
/// carrying <c>commit_sequence</c> (the ADR-IC-005 §P3 read-your-writes token), and replay (the same
/// Idempotency-Key returns the same 201 with no second append, ADR-PC-029 slot 4).
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(EngineApiHostCollection.Name)]
public sealed class EngineCommandPactProviderTests : IAsyncLifetime
{
    /// <summary>The catalogue Test ID this provider verification realises (ADR-PC-029 slot 6). Pinned
    /// to the SAME token the consumer side uses so the two halves are one contract; lets the
    /// spec-coverage checker resolve the Test ID under the CODE_DIRS once row 20 is flipped.</summary>
    public const string TestId = "ENGINE_COMMAND_PACT";

    private static readonly JsonSerializerOptions SnakeCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await new MigrationRunner(_pg.GetConnectionString()).ApplyAsync();
        await new Babelstone.Families.TermDeposit.Application.Migrations.MigrationRunner(
            _pg.GetConnectionString()).ApplyAsync();

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

    /// <summary>
    /// ENGINE_COMMAND_PACT — the provider honours the full contract: the dispatcher's request shape
    /// (POST /v1/deposits, snake_case body, Idempotency-Key = a UUID) yields a 201 +
    /// ConstituteDepositResponse with commit_sequence, and a replay of the same key returns the SAME
    /// 201 (no second append). This is the exact interaction the consumer declares.
    /// </summary>
    [Fact]
    public async Task ENGINE_COMMAND_PACT_provider_honours_the_dispatcher_request_and_replay()
    {
        // The dispatcher's command id is the saga_outbox row's message_id (a UUID) — the
        // Idempotency-Key the engine dedups on.
        var idempotencyKey = Guid.NewGuid().ToString();
        var body = ContractRequestBody();

        // The provider's first response to the contract request: 201 + ConstituteDepositResponse.
        var first = await PostConstituteAsync(body, idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstDoc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());

        // The 201 body carries the contract's pinned snake_case fields: deposit_id, status,
        // commit_sequence (the ADR-IC-005 §P3 read-your-writes token).
        Assert.True(firstDoc.RootElement.TryGetProperty("deposit_id", out var depositIdEl));
        Assert.True(firstDoc.RootElement.TryGetProperty("commit_sequence", out var commitSeqEl));
        Assert.True(firstDoc.RootElement.TryGetProperty("status", out _));
        Assert.True(Guid.TryParse(depositIdEl.GetString(), out var depositId));
        var commitSequence = commitSeqEl.GetInt64();

        // Replay (ADR-PC-029 slot 4): the SAME Idempotency-Key returns the SAME 201 — same deposit_id,
        // same commit_sequence — with no second constitution.
        var replay = await PostConstituteAsync(body, idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var replayDoc = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(depositId, Guid.Parse(replayDoc.RootElement.GetProperty("deposit_id").GetString()!));
        Assert.Equal(commitSequence, replayDoc.RootElement.GetProperty("commit_sequence").GetInt64());
    }

    /// <summary>
    /// ENGINE_COMMAND_PACT — the provider enforces the contract's mandatory Idempotency-Key
    /// (ADR-PC-029 slot 1): an absent or malformed (non-UUID) key is a 400, never a silent
    /// non-idempotent append. This is the clause that makes the dispatcher's at-least-once retry safe.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uuid")]
    public async Task ENGINE_COMMAND_PACT_provider_rejects_an_absent_or_malformed_idempotency_key(string? key)
    {
        var response = await PostConstituteAsync(ContractRequestBody(), key);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// ENGINE_COMMAND_PACT — the deposit_id = process_id correlation pin (bd babelstone-t7o3.11 / 3k10).
    /// The dispatcher POSTs <c>deposit_id = process_id</c>; the engine MUST honour that supplied id AS the
    /// stream/aggregate id, so the resulting <c>DepositConstituted</c> the outbox relay publishes carries
    /// <c>ce_subject = aggregate_id = process_id</c> and the orchestrator's consume loop correlates the
    /// engine's REAL integration fact (on the <c>term_deposit</c> family topic) back to THIS saga by
    /// identity. The engine resolves the RATE in-transaction (ADR-PC-008 §S2) from the structural body
    /// alone — no TAN is supplied. This test pins the load-bearing clause: the 201's <c>deposit_id</c>
    /// equals the supplied <c>process_id</c> (NOT a server-minted GUID).
    /// </summary>
    [Fact]
    public async Task ENGINE_COMMAND_PACT_provider_honours_the_supplied_deposit_id_as_the_stream_id()
    {
        // The saga's process_id, sent as the engine's deposit_id (= the stream/aggregate id).
        var processId = Guid.NewGuid();
        var body = ContractRequestBody() with { DepositId = processId };
        var idempotencyKey = Guid.NewGuid().ToString();

        var response = await PostConstituteAsync(body, idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var depositId = Guid.Parse(doc.RootElement.GetProperty("deposit_id").GetString()!);

        // The engine used the SUPPLIED id as the stream id — so ce_subject = process_id on the relayed
        // DepositConstituted, which is what closes the engine→saga correlation.
        Assert.Equal(processId, depositId);

        // Read-your-writes against the SAME id confirms the stream was opened under it (the engine folds
        // the deposit at that stream id and serves it back), not under a server-minted id.
        var commitSequence = doc.RootElement.GetProperty("commit_sequence").GetInt64();
        var read = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/v1/deposits/{processId}")
        {
            Headers = { { "If-Min-Sequence", commitSequence.ToString() } },
        });
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var readDoc = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal(processId, Guid.Parse(readDoc.RootElement.GetProperty("deposit_id").GetString()!));
    }

    // ---- The contract request the dispatcher declares ------------------------------------------

    /// <summary>The MINIMAL constitute request the dispatcher's ActivateDeposit now translates to (Fork B
    /// rework, bd t7o3.11 / 3k10 / c8d8): the snake_case body carries only product_id + principal_cents +
    /// funding_account (+ deposit_id, set per test). NO term_days / start_date / interest_variant /
    /// auto_renewal_policy / role — the engine RESOLVES those from its deployed product-config store at
    /// constitution, IN-TRANSACTION with the rate-sheet resolve (ADR-PC-008 §S2 / ADR-PC-009). The
    /// provider verification replays this exact minimal shape; a field-shape break here is a contract
    /// break. The engine still resolves the TAN in-transaction (no rate is ever sent).</summary>
    private static ConstituteDepositRequest ContractRequestBody() => new(
        PrincipalCents: 1_000_000,
        ProductId: "dpz_pt_12m_juros_venc",
        FundingAccount: "PT50-DDA-001");

    private async Task<HttpResponseMessage> PostConstituteAsync(ConstituteDepositRequest body, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, EngineCommandContractRoute)
        {
            Content = JsonContent.Create(body, options: SnakeCase),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return await _client.SendAsync(request);
    }

    // The Pact-pinned engine command route (ADR-PC-029 slot 1) — the same literal the consumer side's
    // EngineCommandContract.ConstituteRoute pins.
    private const string EngineCommandContractRoute = "/v1/deposits";

    private static RateSheetBody FlatPriced(string productId, string role, int tanBasisPoints) => new()
    {
        Products = new Dictionary<string, Dictionary<string, RoleRates>>
        {
            [productId] = new()
            {
                [role] = new RoleRates { Bands = [new RateBand(0L, null, tanBasisPoints)] },
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
