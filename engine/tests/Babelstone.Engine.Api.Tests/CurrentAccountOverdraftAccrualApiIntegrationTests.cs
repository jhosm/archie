using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.EventStore.Migrations;
using Babelstone.RateSheets;
using Babelstone.TestFixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Engine.Api.Tests;

/// <summary>
/// End-to-end overdraft-interest ACCRUAL over the real Babelstone.Engine.Api host + real PostgreSQL: the
/// proof that the current_account (conta à ordem) overdraft-accrual path — unit-complete Docker-free (bd
/// babelstone-md1a: the decider fee math, the driver rule, the store-only fold) — drives the WHOLE path on
/// Postgres. A ca_pt_standard account is drawn genuinely negative through the shipped ADR-PC-043 capture
/// writer (bd babelstone-98mj.4: authorize → hold → <c>/capture</c> → HoldCaptured + AccountDebited), a
/// current_account overdraft rate sheet is deployed, then <c>POST /v1/accounts/{id}/overdraft/accrue</c>
/// resolves the overdraft TAN, appends <c>OverdraftInterestAccrued</c>, and the spine movement ledger deepens
/// the account's accounting balance by the fee — the OVERDRAFT_ACCRUAL_POSTS_MOVEMENT commitment (ADR-PC-037
/// §D5, CA-3) exercised through the running engine, not just the pure decider.
/// </summary>
/// <remarks>
/// The account is drawn negative WITHOUT a prior funding credit: on a zero balance a ca_pt_standard account's
/// EUR 500 arranged overdraft lets an authorize + capture overdraw the account into the arranged window, so the
/// capture's Debit Movement alone takes the accounting balance below zero — exactly the negative-balance
/// producer bd babelstone-t9ey was gated on. The accrual fee posts as an ADR-PC-043 <c>Observed</c> Debit
/// Movement (engine-internal already-effected: no external counterparty, no cash leg), deepening the balance
/// further below zero. Tagged Integration (Testcontainers lane) and reusing the shared non-parallel
/// <see cref="EngineApiHostCollection"/> so CI's <c>Category=Integration</c> engine job runs it — the gate that
/// legitimises CA-3's Live flip. All the accrual math is pinned to the cent by the Docker-free
/// <c>CurrentAccountOverdraftAccrualTests</c> / <c>CurrentAccountOverdraftTests</c>; this suite pins the
/// running-engine wiring (rate resolution → append → ledger deepening) the unit tier cannot reach.
/// </remarks>
[Trait("Category", "Integration")]
[Collection(EngineApiHostCollection.Name)]
public sealed class CurrentAccountOverdraftAccrualApiIntegrationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions SnakeCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    // The ca_pt_standard config references its overdraft-interest rate under family current_account, role
    // 'overdraft' (product-configs/current-account/ca_pt_standard.yaml → rate.role_selector: overdraft). The
    // accrual service resolves the TAN from the current_account rate sheet active as of the accrual date, then
    // ResolveTanBasisPoints(product_code, role, drawn_cents) — so the deployed sheet must price ca_pt_standard
    // under 'overdraft' for the whole drawn range.
    private const string OverdraftRole = "overdraft";
    private const string ProductCode = "ca_pt_standard";
    private const string RateSheetVersionId = "pt-ca-overdrafts-2026.1";

    // A clean, exact fee: EUR 400 drawn (a −40 000-cent balance) for ONE day at 3.65% (365 bps), Act/365, is
    // 40000 × 365 / (365 × 10000) = 4 cents — no rounding to obscure the shape. The Debit deepens the balance
    // from −40 000 to −40 004 cents.
    private const int OverdraftTanBps = 365;
    private const long DrawnCents = 40_000;
    private const long ExpectedFeeCents = 4;

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _pg.GatedStartAsync();
        // Engine event-store schema first (creates the babelstone_engine role the family read models grant on),
        // then the term-deposit family read-model migration the host's term-deposit module also applies at boot
        // — applied up front for a deterministic boot. current_account needs NO family migration: its
        // balances/holds are spine-owned folds over the shared movement_ledger / account_holds tables.
        await new MigrationRunner(_pg.GetConnectionString()).ApplyAsync();
        await new Babelstone.Families.TermDeposit.Application.Migrations.MigrationRunner(
            _pg.GetConnectionString()).ApplyAsync();

        // Deploy the current_account overdraft rate sheet the accrual resolves (365 bps for ca_pt_standard /
        // overdraft, across the whole drawn range). The accrual service resolves BY FAMILY (current_account) as
        // of the accrual date, so the sheet's ProductFamily must be current_account and its effective_from must
        // precede the accrual date — mirroring how DepositsApiIntegrationTests seeds the term_deposit sheet the
        // constitute flow resolves.
        var rateSheets = new PostgresRateSheetStore(_pg.GetConnectionString());
        await rateSheets.InsertAsync(new RateSheet(
            RateSheetVersionId: RateSheetVersionId,
            ProductFamily: "current_account",
            PackVersion: "pt.2026.1",
            EffectiveFrom: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Body: OverdraftPriced(ProductCode, OverdraftRole, OverdraftTanBps),
            ApprovedBy: "alm@bank.pt",
            ApprovalRef: "RC-2026-CA-001",
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

    [Fact]
    public async Task OVERDRAFT_ACCRUAL_POSTS_MOVEMENT_a_drawn_account_accrues_the_resolved_fee_as_an_observed_debit_movement()
    {
        // CA-3 (ADR-PC-037 §D5 / ADR-PC-043) end-to-end. In plain English: an account sitting below zero is
        // charged one day of overdraft interest, resolved from the deployed rate sheet, posted as a Debit that
        // deepens the overdraft — driven through the running engine, not just the pure decider.
        var accountId = await OpenAccountAsync();

        // Draw the account genuinely negative through the ADR-PC-043 capture writer: on a zero balance the
        // EUR 500 arranged overdraft lets a EUR 400 authorize + capture overdraw into the arranged window, so
        // the capture's Debit alone takes the accounting balance to −40 000 cents (the negative-balance producer
        // bd babelstone-t9ey was gated on — no prior funding credit needed).
        var holdId = await AuthorizeAsync(accountId, DrawnCents);
        await CaptureAsync(accountId, holdId, DrawnCents);
        Assert.Equal(-DrawnCents, await AccountingBalanceCentsAsync(accountId));

        // Accrue one day's overdraft interest as of the drawn value-date. 200 with the folded status; the
        // append is the OverdraftInterestAccrued fact.
        var accrue = await AccrueAsync(accountId, new DateOnly(2026, 3, 5), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, accrue.StatusCode);

        // The fee posted as an Observed Debit Movement (ADR-PC-043): the accounting balance deepened by exactly
        // the resolved fee — from −40 000 to −40 004 cents (365 bps on EUR 400 for one day, Act/365, = 4 cents).
        // The accrual's fee Movement is visible on the read model once the spine drive has folded it.
        await DrainSpineProjectionsAsync();
        Assert.Equal(-(DrawnCents + ExpectedFeeCents), await AccountingBalanceCentsAsync(accountId));

        // The durable event log carries the accrual fact after the open + the capture batch (the spine
        // HoldPlaced/HoldCaptured earmark pair + the family AccountDebited, then the family
        // OverdraftInterestAccrued). All are on the account stream, in order.
        Assert.Equal(
            [
                "current_account.AccountOpened",
                "operations.HoldPlaced",
                "operations.HoldCaptured",
                "current_account.AccountDebited",
                "current_account.OverdraftInterestAccrued",
            ],
            await EventTypesAsync(accountId));
    }

    [Fact]
    public async Task OVERDRAFT_ACCRUAL_POSTS_MOVEMENT_a_replayed_accrual_command_id_accrues_once()
    {
        // Idempotent on the command id (ADR-PC-029 slot 4): re-POSTing the SAME accrual under the same
        // Idempotency-Key charges the fee ONCE — the replay returns the original head off the single appended
        // event, no second OverdraftInterestAccrued and no second Debit (one accrual per account per day, the
        // LCD-1 guarantee the ADR-PC-036 driver's canonical dispatch id buys).
        var accountId = await OpenAccountAsync();
        var holdId = await AuthorizeAsync(accountId, DrawnCents);
        await CaptureAsync(accountId, holdId, DrawnCents);

        var commandId = Guid.NewGuid();
        var first = await AccrueAsync(accountId, new DateOnly(2026, 3, 5), commandId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var commitSequence = firstBody.GetProperty("commit_sequence").GetInt64();

        var replay = await AccrueAsync(accountId, new DateOnly(2026, 3, 5), commandId);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayBody = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(commitSequence, replayBody.GetProperty("commit_sequence").GetInt64());

        // Exactly ONE fee applied: the balance moved by a single day's interest, not two.
        await DrainSpineProjectionsAsync();
        Assert.Equal(-(DrawnCents + ExpectedFeeCents), await AccountingBalanceCentsAsync(accountId));

        // Exactly ONE OverdraftInterestAccrued on the stream — the replay appended nothing.
        Assert.Equal(
            1,
            (await EventTypesAsync(accountId)).Count(t => t == "current_account.OverdraftInterestAccrued"));
    }

    [Fact]
    public async Task OVERDRAFT_ACCRUAL_POSTS_MOVEMENT_a_non_drawn_account_accrues_nothing()
    {
        // Gate 2 (CurrentAccountOverdraftAccrualService): an account whose balance is NOT drawn owes no
        // overdraft interest — the accrual is a no-op that appends nothing and returns the current head (never a
        // 422 retry-storm), so the fresh, zero-balance account's stream carries only its open after the accrual.
        var accountId = await OpenAccountAsync();

        var accrue = await AccrueAsync(accountId, new DateOnly(2026, 3, 5), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, accrue.StatusCode);

        Assert.Equal(0, await AccountingBalanceCentsAsync(accountId));
        Assert.Equal(["current_account.AccountOpened"], await EventTypesAsync(accountId));
    }

    private async Task<Guid> OpenAccountAsync()
    {
        var accountId = Guid.NewGuid();
        var open = await _client.PostAsJsonAsync("/v1/accounts", new
        {
            account_id = accountId,
            product_code = ProductCode,
            currency = "EUR",
        }, SnakeCase);
        Assert.Equal(HttpStatusCode.Created, open.StatusCode);
        return accountId;
    }

    // Authorize a debit within the ca_pt_standard EUR 500 arranged overdraft on a zero balance and return the
    // placed hold id — the reservation the capture then settles into a real Debit.
    private async Task<string> AuthorizeAsync(Guid accountId, long amountCents)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/accounts/{accountId}/authorize")
        {
            Content = JsonContent.Create(new { amount_cents = amountCents, value_date = "2026-03-05" }, options: SnakeCase),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AUTHORIZED", body.GetProperty("outcome").GetString());
        var holdId = body.GetProperty("hold_id").GetString();
        Assert.NotNull(holdId);
        return holdId!;
    }

    // Capture the placed hold (the ADR-PC-043 settlement writer): the append command_id derives from the BODY's
    // intent_reference, NOT the HTTP Idempotency-Key, so no header is sent. The one atomic append is the spine
    // HoldCaptured + the family AccountDebited Debit Movement — which takes the accounting balance negative.
    private async Task CaptureAsync(Guid accountId, string holdId, long amountCents)
    {
        var response = await _client.PostAsJsonAsync($"/v1/accounts/{accountId}/capture", new
        {
            target_hold_id = holdId,
            amount_cents = amountCents,
            value_date = "2026-03-05",
            intent_reference = $"intent-{Guid.NewGuid():N}",
        }, SnakeCase);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Fold the capture's AccountDebited Debit Movement into the spine movement ledger before any balance
        // read: the capture appends the batch but its own drain is BEFORE the append (the read-your-writes hold
        // match), so the just-posted Debit is visible on the read model only once the spine drive has folded it.
        await DrainSpineProjectionsAsync();
    }

    // Drive the spine account-projection drive ONCE (the same SpineProjectionDrainer the host's background poll
    // loop uses, ADR-PC-032 / ADR-PC-033), folding the newly-appended Movements into the account-keyed
    // movement_ledger read model so a subsequent GetAccountingBalanceCentsAsync reads the posted balance.
    private async Task DrainSpineProjectionsAsync()
    {
        var drainer = _factory.Services.GetRequiredService<SpineProjectionDrainer>();
        await drainer.DrainOnceAsync();
    }

    // POST the projection-derived overdraft accrual (the ADR-PC-036 driver's surface): the body carries only the
    // economic accrual_date; the mandatory Idempotency-Key command id rides the header (ADR-PC-029).
    private async Task<HttpResponseMessage> AccrueAsync(Guid accountId, DateOnly accrualDate, Guid commandId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/accounts/{accountId}/overdraft/accrue")
        {
            Content = JsonContent.Create(new { accrual_date = accrualDate.ToString("O") }, options: SnakeCase),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", commandId.ToString());
        return await _client.SendAsync(request);
    }

    // Read the account's accounting balance off the spine-owned fold (ACCOUNT_BALANCE_IS_A_FOLD, ADR-PC-033) —
    // the authoritative post-append value the accrual deepened, read the same way the accrual service reads it.
    private async Task<long> AccountingBalanceCentsAsync(Guid accountId)
    {
        var balances = _factory.Services.GetRequiredService<AccountBalanceReader>();
        return await balances.GetAccountingBalanceCentsAsync(accountId.ToString());
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

    // One (product, role) priced flat across the whole drawn range — a single open-ended band from 0. The
    // accrual resolves ResolveTanBasisPoints(product_code, 'overdraft', drawn_cents), so the band must cover any
    // drawn principal; a flat band is the simplest covering sheet (mirrors the deposits test's FlatPriced).
    private static RateSheetBody OverdraftPriced(string productId, string role, int tanBasisPoints) =>
        new()
        {
            Products = new()
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
            : throw new InvalidOperationException("Could not locate packs/pt.2026.1/pack.yaml above the test base directory.");
    }
}
