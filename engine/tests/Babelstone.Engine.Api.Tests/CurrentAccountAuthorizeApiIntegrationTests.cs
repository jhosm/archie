using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.EventStore.Migrations;
using Babelstone.TestFixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Engine.Api.Tests;

/// <summary>
/// End-to-end synchronous AUTHORIZE over the real Babelstone.Engine.Api host + real PostgreSQL: the
/// proof that the current_account (conta à ordem) family answers a real-time debit authorization in the
/// running engine, idempotently — the AUTHORIZATION_SYNC_IDEMPOTENT (AUTH-1) commitment. The host boots
/// via <see cref="WebApplicationFactory{Program}"/> (the SAME Program
/// the deposit / loan integration tests boot), discovers <c>CurrentAccountHostModule</c> by assembly-scan,
/// composes its <c>AggregateRuntime&lt;AccountPosition&gt;</c> + authorize service, and maps
/// <c>POST /v1/accounts/{id}/authorize</c>.
/// </summary>
/// <remarks>
/// The account is funded by recording a cleared inbound CREDIT directly on the spine movement ledger
/// (<see cref="IMovementLedgerStore"/>) — a rebuildable read model <c>AccountBalanceReader</c> sums
/// (ADR-PC-032) — standing in for a settled inbound posting observed via the capture/settlement feed
/// (the current-account posting-feed producer is dormant in v1, POSTING-1). The authorize itself is
/// exercised over HTTP end-to-end. Tagged Integration (Testcontainers lane) so CI's
/// <c>Category=Integration</c> engine job runs it — the gate that legitimises AUTH-1's Live flip.
/// </remarks>
[Trait("Category", "Integration")]
[Collection(EngineApiHostCollection.Name)]
public sealed class CurrentAccountAuthorizeApiIntegrationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions SnakeCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _pg.GatedStartAsync();
        // Engine event-store schema first (creates the babelstone_engine role the family read models grant
        // on), then the term-deposit family read-model migration the host's term-deposit module also applies
        // at boot — applied up front for a deterministic boot. current_account needs NO family migration:
        // its balances/holds are spine-owned folds over the shared movement_ledger / account_holds tables.
        await new MigrationRunner(_pg.GetConnectionString()).ApplyAsync();
        await new Babelstone.Families.TermDeposit.Application.Migrations.MigrationRunner(
            _pg.GetConnectionString()).ApplyAsync();

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

    [Fact]
    public async Task AUTHORIZATION_SYNC_IDEMPOTENT_a_replayed_authorize_returns_the_original_hold_and_appends_once()
    {
        // AUTH-1 (ADR-PC-034 / ADR-PC-037 §D6). In plain English: authorizing the SAME debit twice — same
        // Idempotency-Key — must earmark the money ONCE. The engine guarantees it by deduping on the command
        // id (ADR-PC-029): the replay returns the original verdict off the single appended event, no second
        // HoldPlaced.
        var accountId = await OpenAccountAsync();
        await SeedClearedCreditAsync(accountId, 100_000);
        var commandId = Guid.NewGuid();

        // Firing #1: authorize a 300.00 debit within the 1000.00 balance. 200 AUTHORIZED, carrying the hold.
        var first = await AuthorizeAsync(accountId, amountCents: 30_000, commandId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AUTHORIZED", firstBody.GetProperty("outcome").GetString());
        var holdId = firstBody.GetProperty("hold_id").GetString();
        // The hold id is deterministic per authorization — derived from the command id (ADR-PC-033).
        Assert.Equal($"hold-{commandId:N}", holdId);
        Assert.Null(GetNullableString(firstBody, "declined_reason"));
        var commitSequence = firstBody.GetProperty("commit_sequence").GetInt64();

        // Firing #2 — a replay of the SAME command id. It returns the ORIGINAL verdict: the same hold_id and
        // the same commit_sequence, with NO second decision and NO second append.
        var replay = await AuthorizeAsync(accountId, amountCents: 30_000, commandId);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayBody = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AUTHORIZED", replayBody.GetProperty("outcome").GetString());
        Assert.Equal(holdId, replayBody.GetProperty("hold_id").GetString());
        Assert.Equal(commitSequence, replayBody.GetProperty("commit_sequence").GetInt64());

        // Exactly ONE operations.HoldPlaced on the account stream (after the open) — the earmark was placed
        // once. operations.HoldPlaced is the spine cross-cutting event, NOT a current_account event.
        Assert.Equal(
            ["current_account.AccountOpened", "operations.HoldPlaced"],
            await EventTypesAsync(accountId));
    }

    [Fact]
    public async Task Two_debits_exceeding_the_balance_the_second_is_declined_read_your_writes()
    {
        // The drain-before-decide safety (ADR-PC-033 / ADR-PC-034 property 4): two DISTINCT authorizations
        // against one account are serialised by the earmark, not a lock. The first authorize's HoldPlaced
        // lowers the available balance the second one reads — so the second, which alone would fit the
        // balance, is declined once the first hold is in view. This is what the service's explicit
        // DrainOnceAsync buys: without it the second would read the stale pre-hold balance and double-spend.
        // A ca_pt_basic account (no arranged overdraft), so the second debit's shortfall is a plain
        // INSUFFICIENT_AVAILABLE_BALANCE — the read-your-writes proof is about the hold, not the overdraft.
        var accountId = await OpenAccountAsync("ca_pt_basic");
        await SeedClearedCreditAsync(accountId, 100_000);

        // #1: 600.00 of the 1000.00 balance — authorized, earmarking 600.00 (available now 400.00).
        var first = await AuthorizeAsync(accountId, amountCents: 60_000, Guid.NewGuid());
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AUTHORIZED", firstBody.GetProperty("outcome").GetString());

        // #2 (a DIFFERENT command id): another 600.00 — it fit the ORIGINAL balance but not the available
        // balance net of #1's hold, so it is declined INSUFFICIENT_AVAILABLE_BALANCE.
        var second = await AuthorizeAsync(accountId, amountCents: 60_000, Guid.NewGuid());
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DECLINED", secondBody.GetProperty("outcome").GetString());
        Assert.Equal("INSUFFICIENT_AVAILABLE_BALANCE", secondBody.GetProperty("declined_reason").GetString());

        // One earmark placed, one refusal recorded — the second debit moved nothing.
        Assert.Equal(
            ["current_account.AccountOpened", "operations.HoldPlaced", "current_account.AuthorizationDeclined"],
            await EventTypesAsync(accountId));
    }

    [Fact]
    public async Task A_declined_authorize_records_one_refusal_fact_and_a_replay_returns_the_original_code()
    {
        // A decline is an APPENDED auditable fact (ADR-PC-033 slot 5 / ADR-PC-037 §D6), not a silent
        // non-append — and it is idempotent exactly like an approval: a replay returns the original code with
        // no second refusal fact. A fresh ca_pt_basic account (no overdraft) has no funds, so a debit is
        // INSUFFICIENT_AVAILABLE_BALANCE (not the arranged-overdraft path).
        var accountId = await OpenAccountAsync("ca_pt_basic");
        var commandId = Guid.NewGuid();

        var first = await AuthorizeAsync(accountId, amountCents: 5_000, commandId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DECLINED", firstBody.GetProperty("outcome").GetString());
        Assert.Equal("INSUFFICIENT_AVAILABLE_BALANCE", firstBody.GetProperty("declined_reason").GetString());
        Assert.Null(GetNullableString(firstBody, "hold_id"));
        var commitSequence = firstBody.GetProperty("commit_sequence").GetInt64();

        var replay = await AuthorizeAsync(accountId, amountCents: 5_000, commandId);
        var replayBody = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DECLINED", replayBody.GetProperty("outcome").GetString());
        Assert.Equal("INSUFFICIENT_AVAILABLE_BALANCE", replayBody.GetProperty("declined_reason").GetString());
        Assert.Equal(commitSequence, replayBody.GetProperty("commit_sequence").GetInt64());

        // Exactly ONE current_account.AuthorizationDeclined refusal fact — the family-owned, store-only audit
        // event (no operations.HoldPlaced: nothing was earmarked).
        Assert.Equal(
            ["current_account.AccountOpened", "current_account.AuthorizationDeclined"],
            await EventTypesAsync(accountId));
    }

    [Fact]
    public async Task An_arranged_overdraft_authorizes_a_debit_within_the_limit_and_declines_one_beyond_it_OVERDRAFT_LIMIT_EXCEEDED()
    {
        // ARRANGED_OVERDRAFT_PACK_BOUNDED end-to-end (ADR-PC-037 §D5, CA-1): a ca_pt_standard account carries
        // the shipped EUR 500 arranged overdraft, resolved from its product config over HTTP. On a zero
        // balance a debit BEYOND the limit is refused (OVERDRAFT_LIMIT_EXCEEDED, earmarking nothing), and a
        // debit WITHIN the limit overdraws the account into the arranged window and is authorized — the
        // pack-value read proven through the running engine, not just the pure decider.
        var accountId = await OpenAccountAsync("ca_pt_standard");

        // EUR 600 on a zero balance overdraws BEYOND the EUR 500 arranged limit → declined, nothing earmarked.
        var beyond = await AuthorizeAsync(accountId, amountCents: 60_000, Guid.NewGuid());
        var beyondBody = await beyond.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DECLINED", beyondBody.GetProperty("outcome").GetString());
        Assert.Equal("OVERDRAFT_LIMIT_EXCEEDED", beyondBody.GetProperty("declined_reason").GetString());

        // EUR 400 on the still-zero balance overdraws WITHIN the EUR 500 arranged window → authorized.
        var within = await AuthorizeAsync(accountId, amountCents: 40_000, Guid.NewGuid());
        var withinBody = await within.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AUTHORIZED", withinBody.GetProperty("outcome").GetString());
        Assert.NotNull(withinBody.GetProperty("hold_id").GetString());

        // The refusal fact then the earmark — the beyond-limit debit moved nothing, the within-limit one held.
        Assert.Equal(
            ["current_account.AccountOpened", "current_account.AuthorizationDeclined", "operations.HoldPlaced"],
            await EventTypesAsync(accountId));
    }

    [Fact]
    public async Task A_dormant_account_declines_ACCOUNT_NOT_ACTIVE_even_with_funds()
    {
        // The family lifecycle gate (ADR-PC-037 §D2/§D6): a non-active account cannot authorize a debit,
        // regardless of balance — proving the gate runs, and that ACCOUNT_NOT_ACTIVE is a business DECLINED
        // on the 200 body (an appended refusal fact), not a 422 lifecycle rejection.
        var accountId = await OpenAccountAsync();
        await SeedClearedCreditAsync(accountId, 100_000);
        await MarkDormantAsync(accountId);

        var response = await AuthorizeAsync(accountId, amountCents: 10_000, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DECLINED", body.GetProperty("outcome").GetString());
        Assert.Equal("ACCOUNT_NOT_ACTIVE", body.GetProperty("declined_reason").GetString());
    }

    [Fact]
    public async Task A_missing_idempotency_key_is_400_and_appends_nothing()
    {
        var accountId = await OpenAccountAsync();
        await SeedClearedCreditAsync(accountId, 100_000);

        // No Idempotency-Key header at all — the mandatory money-mover command id is absent (ADR-PC-029).
        var response = await _client.PostAsJsonAsync(
            $"/v1/accounts/{accountId}/authorize", new { amount_cents = 10_000L, value_date = "2026-03-05" }, SnakeCase);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Nothing decided or appended: the stream still carries only the open.
        Assert.Equal(["current_account.AccountOpened"], await EventTypesAsync(accountId));
    }

    [Fact]
    public async Task A_non_positive_amount_is_400()
    {
        var accountId = await OpenAccountAsync();

        var response = await AuthorizeAsync(accountId, amountCents: 0, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Opens a current account of the given product. Defaults to ca_pt_standard (the canonical account,
    // arranged overdraft EUR 500); the balance-mechanics tests below open ca_pt_basic (no overdraft) so the
    // arranged overdraft does not change their INSUFFICIENT_AVAILABLE_BALANCE outcomes — the overdraft path
    // is proven by the dedicated arranged-overdraft test.
    private async Task<Guid> OpenAccountAsync(string productCode = "ca_pt_standard")
    {
        var accountId = Guid.NewGuid();
        var open = await _client.PostAsJsonAsync("/v1/accounts", new
        {
            account_id = accountId,
            product_code = productCode,
            currency = "EUR",
        }, SnakeCase);
        Assert.Equal(HttpStatusCode.Created, open.StatusCode);
        return accountId;
    }

    private async Task MarkDormantAsync(Guid accountId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/accounts/{accountId}/dormancy")
        {
            Content = JsonContent.Create(new { reason = "INACTIVITY_HORIZON" }, options: SnakeCase),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<HttpResponseMessage> AuthorizeAsync(
        Guid accountId, long amountCents, Guid commandId, string valueDate = "2026-03-05")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/accounts/{accountId}/authorize")
        {
            Content = JsonContent.Create(new { amount_cents = amountCents, value_date = valueDate }, options: SnakeCase),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", commandId.ToString());
        return await _client.SendAsync(request);
    }

    // Fund the account by recording a cleared inbound CREDIT directly on the spine movement ledger — the
    // rebuildable read model AccountBalanceReader sums (ADR-PC-032 / ACCOUNT_BALANCE_IS_A_FOLD). It stands
    // in for a settled inbound posting observed via the capture/settlement feed (MovementOrigin.Observed,
    // e.g. a matured-deposit payout landing in the demand account); the current-account posting-feed PRODUCER
    // is dormant in v1 (POSTING-1 Planned). The operation code is immaterial to the authorize decision — only
    // the credit amount and account_ref move the available balance. A fresh StreamId keeps the seed's
    // producing-event identity distinct from the account's own events, so it never appears on the account stream.
    private async Task SeedClearedCreditAsync(Guid accountId, long cents)
    {
        var movements = _factory.Services.GetRequiredService<IMovementLedgerStore>();
        await movements.AppendAsync([new MovementLedgerEntry(
            AccountRef: accountId.ToString(),
            StreamId: Guid.NewGuid(),
            SequenceNumber: 0,
            MovementIndex: 0,
            Direction: nameof(SettlementDirection.Credit),
            AmountCents: cents,
            ValueDate: new DateOnly(2026, 3, 1),
            Operation: nameof(MovementOperation.PayMaturity),
            Origin: nameof(MovementOrigin.Observed),
            CommandId: Guid.NewGuid())]);
    }

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

    private static string? GetNullableString(JsonElement body, string propertyName) =>
        body.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static string PacksDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "packs", "pt.2026.1", "pack.yaml")))
        {
            dir = dir.Parent;
        }

        return dir is not null
            ? Path.Combine(dir.FullName, "packs")
            : throw new InvalidOperationException("Could not locate packs/pt.2026.1/pack.yaml above the test base directory.");
    }
}
