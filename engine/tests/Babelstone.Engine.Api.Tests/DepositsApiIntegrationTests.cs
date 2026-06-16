using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Babelstone.Engine.Api;
using Babelstone.EventStore.Migrations;
using Babelstone.RateSheets;
using Babelstone.TestFixtures;
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
        await _pg.GatedStartAsync();
        // Engine event-store schema first (it creates the babelstone_engine role the family read
        // model GRANTs on), then the term-deposit family's OWN read-model migration set
        // (read_model.deposits, ADR-PC-021 family-owned ownership) — engine-before-family ordering.
        // The host's ReadModelMigrationHostedService would also apply the family migration on boot,
        // but this fixture applies it up front so the schema is present before the host starts.
        await new MigrationRunner(_pg.GetConnectionString()).ApplyAsync();
        await new Babelstone.Families.TermDeposit.Application.Migrations.MigrationRunner(
            _pg.GetConnectionString()).ApplyAsync();

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
        // The Idempotency-Key is MANDATORY on constitution (ADR-PC-029 slot 1); a fresh key per test.
        var constituteResponse = await PostConstituteAsync(new ConstituteDepositRequest(
            PrincipalCents: 1_000_000,
            ProductId: "dpz_pt_12m_juros_venc",
            Role: "standard",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            FundingAccount: "PT50-DDA-001"), Guid.NewGuid().ToString());

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
        // in order — the "events" leg of E.6's events+projection+published-messages triad,
        // driven through MCP's HTTP surface rather than the runtime directly. ALL FOUR are in
        // the store (appended/folded/replayable from the JSON event store).
        Assert.Equal(
            ["term_deposit.DepositConstituted", "term_deposit.InterestAccrued",
             "term_deposit.WithholdingApplied", "term_deposit.DepositMatured"],
            await EventTypesAsync(depositId));
        Assert.Equal(4, await CountAsync("events", "stream_id", depositId));
        // But ONLY the catalogued subset gets an outbox row (the bus surface) — the catalog-gated
        // relay (ADR-IC-017 §P1) is wired in the host. After the §P4 promotion pass the AT_MATURITY
        // flow's catalogued events are DepositConstituted + DepositMatured (2); the de-promoted
        // InterestAccrued / WithholdingApplied accrual mechanics are store-only, so no outbox row.
        // (InterestPaid, the promoted coupon-payout event, is NOT emitted by the AT_MATURITY flow.)
        Assert.Equal(2, await CountAsync("outbox", "aggregate_id", depositId));
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
        // The Idempotency-Key is MANDATORY on constitution (ADR-PC-029 slot 1); a fresh key per test.
        var constituteResponse = await PostConstituteAsync(new ConstituteDepositRequest(
            PrincipalCents: 1_000_000,
            ProductId: "dpz_pt_12m_juros_venc",
            Role: "standard",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            FundingAccount: "PT50-DDA-001"), Guid.NewGuid().ToString());
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
        // The Idempotency-Key is MANDATORY on constitution (ADR-PC-029 slot 1); a fresh key per test.
        var constituteResponse = await PostConstituteAsync(new ConstituteDepositRequest(
            PrincipalCents: 1_000_000,
            ProductId: "dpz_pt_12m_juros_venc",
            Role: "standard",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            FundingAccount: "PT50-DDA-001"), Guid.NewGuid().ToString());
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

    [Fact]
    public async Task READ_AS_OF_SEQUENCE_folds_to_the_historical_state_at_a_point_not_current()
    {
        // The as-of / point-in-time read (I.2, bd babelstone-b4wp): drive a deposit through its full
        // lifecycle (constitute → mature) so the stream carries several events, then ask
        // "what did it look like AS OF the constitution sequence?" — the answer must reflect the
        // HISTORICAL state at that point (Active, no accrued interest) and NOT the current Matured
        // head. The axis is transaction-time by commit_sequence (deterministic, no wall-clock in the
        // fold, ADR-PC-027 slot 3/4 generalised): fold the stream up to and including as_of_sequence.
        var constituteResponse = await PostConstituteAsync(new ConstituteDepositRequest(
            PrincipalCents: 1_000_000,
            ProductId: "dpz_pt_12m_juros_venc",
            Role: "standard",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            FundingAccount: "PT50-DDA-001"), Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, constituteResponse.StatusCode);
        var constituted = (await constituteResponse.Content.ReadFromJsonAsync<ConstituteDepositResponse>(SnakeCase))!;
        var depositId = constituted.DepositId;
        // The constitution event is sequence 0 (the head version the append reached).
        var asOfConstitution = constituted.CommitSequence;

        // Mature it — appends three more events (InterestAccrued, WithholdingApplied, DepositMatured),
        // advancing the head well past the constitution sequence.
        var maturityResponse = await _client.PostAsJsonAsync(
            $"/v1/deposits/{depositId}/maturity", new MatureDepositRequest(), SnakeCase);
        Assert.Equal(HttpStatusCode.OK, maturityResponse.StatusCode);
        var maturedHead = (await maturityResponse.Content.ReadFromJsonAsync<DepositResponse>(SnakeCase))!;
        Assert.Equal("Matured", maturedHead.Lifecycle);
        Assert.True(maturedHead.LastSequence > asOfConstitution);

        // As-of the constitution sequence: the historical projection — Active, nothing accrued, no tax,
        // no payout beyond principal — NOT the current matured numbers.
        var asOf = await _client.GetFromJsonAsync<DepositResponse>(
            $"/v1/deposits/{depositId}?as_of_sequence={asOfConstitution}", SnakeCase);
        Assert.NotNull(asOf);
        Assert.Equal(depositId, asOf.DepositId);
        Assert.Equal("Active", asOf.Lifecycle);                 // historical lifecycle, not Matured
        Assert.Equal(asOfConstitution, asOf.LastSequence);      // the answer reflects exactly that point
        Assert.Equal(0, asOf.AccruedGrossInterestCents);        // nothing had accrued at constitution
        Assert.Equal(0, asOf.WithholdingToDateCents);
        Assert.Equal(0, asOf.NetInterestCents);
        Assert.Equal(1_000_000, asOf.PrincipalCents);
        Assert.Equal(0, asOf.CouponsPaid);

        // Sanity: the current head (no as_of) is the matured state, proving as-of read a DIFFERENT point.
        var current = await _client.GetFromJsonAsync<DepositResponse>($"/v1/deposits/{depositId}", SnakeCase);
        Assert.NotNull(current);
        Assert.Equal("Matured", current.Lifecycle);
        Assert.True(current.AccruedGrossInterestCents > 0);
    }

    [Fact]
    public async Task READ_AS_OF_SEQUENCE_beyond_head_is_a_clean_4xx_not_a_500()
    {
        // An as_of_sequence past the stream head is a client error (the caller asked for a point that
        // does not exist yet), surfaced as a clean 4xx ProblemDetails — never a 500 and never a silent
        // fold-to-head that would pretend a future point is "now".
        var constituteResponse = await PostConstituteAsync(new ConstituteDepositRequest(
            PrincipalCents: 1_000_000,
            ProductId: "dpz_pt_12m_juros_venc",
            Role: "standard",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            FundingAccount: "PT50-DDA-001"), Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, constituteResponse.StatusCode);
        var depositId = (await constituteResponse.Content.ReadFromJsonAsync<ConstituteDepositResponse>(SnakeCase))!.DepositId;

        var response = await _client.GetAsync($"/v1/deposits/{depositId}?as_of_sequence=999999");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task READ_AS_OF_SEQUENCE_negative_is_a_clean_400_not_a_500()
    {
        // A malformed (negative) as_of_sequence is a bad request — a clean 400, never a 500.
        var constituteResponse = await PostConstituteAsync(new ConstituteDepositRequest(
            PrincipalCents: 1_000_000,
            ProductId: "dpz_pt_12m_juros_venc",
            Role: "standard",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            FundingAccount: "PT50-DDA-001"), Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, constituteResponse.StatusCode);
        var depositId = (await constituteResponse.Content.ReadFromJsonAsync<ConstituteDepositResponse>(SnakeCase))!.DepositId;

        var response = await _client.GetAsync($"/v1/deposits/{depositId}?as_of_sequence=-1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task READ_AS_OF_SEQUENCE_on_an_unknown_deposit_is_404()
    {
        // An as-of read of a deposit that never existed is the same 404 the default read gives — the
        // as_of axis does not change the unknown-stream verdict.
        var response = await _client.GetAsync($"/v1/deposits/{Guid.NewGuid()}?as_of_sequence=0");
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
        // A valid Idempotency-Key is supplied so the request reaches the domain check (the rejection
        // is the 422 under test) rather than short-circuiting on the mandatory-key 400.
        var response = await PostConstituteAsync(new ConstituteDepositRequest(
            PrincipalCents: 1_000_000,
            ProductId: "unpriced_product",
            Role: "standard",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            FundingAccount: "PT50-DDA-001"), Guid.NewGuid().ToString());

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

    [Fact]
    public async Task ENGINE_COMMAND_IDEMPOTENT_a_replayed_idempotency_key_returns_the_original_and_appends_once()
    {
        // ADR-PC-029 slot 4 end-to-end: two POSTs with the SAME Idempotency-Key (the saga
        // dispatcher's at-least-once retry) yield ONE deposit. The second is short-circuited by the
        // engine's command-dedup pre-check and replays the original outcome — same deposit_id, same
        // commit_sequence — with no second DepositConstituted on the stream.
        var key = Guid.NewGuid().ToString();
        var body = new ConstituteDepositRequest(
            PrincipalCents: 1_000_000,
            ProductId: "dpz_pt_12m_juros_venc",
            Role: "standard",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            FundingAccount: "PT50-DDA-001");

        var first = await PostConstituteAsync(body, key);
        var second = await PostConstituteAsync(body, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var firstBody = await first.Content.ReadFromJsonAsync<ConstituteDepositResponse>(SnakeCase);
        var secondBody = await second.Content.ReadFromJsonAsync<ConstituteDepositResponse>(SnakeCase);
        Assert.NotNull(firstBody);
        Assert.NotNull(secondBody);

        // The replay returns the ORIGINAL identity + read-your-writes token, verbatim.
        Assert.Equal(firstBody.DepositId, secondBody.DepositId);
        Assert.Equal(firstBody.CommitSequence, secondBody.CommitSequence);

        // NO second append: exactly one DepositConstituted for the single deposit.
        Assert.Equal(["term_deposit.DepositConstituted"], await EventTypesAsync(firstBody.DepositId));
        Assert.Equal(1, await CountAsync("events", "stream_id", firstBody.DepositId));
    }

    [Theory]
    [InlineData(null)]          // absent: the Idempotency-Key is MANDATORY (ADR-PC-029 slot 1)
    [InlineData("not-a-uuid")]  // malformed: the command id must be a deterministic UUID
    public async Task An_absent_or_malformed_idempotency_key_is_a_400(string? key)
    {
        // The command id MUST be a UUID supplied by the caller (ADR-PC-029 slot 1, the deterministic
        // saga_outbox row id). The engine never accepts a non-idempotent constitution: an absent or
        // non-UUID key fails loud (400), never a silent non-idempotent append.
        var body = new ConstituteDepositRequest(
            PrincipalCents: 1_000_000,
            ProductId: "dpz_pt_12m_juros_venc",
            Role: "standard",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            FundingAccount: "PT50-DDA-001");

        var response = await PostConstituteAsync(body, idempotencyKey: key);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Constitute_with_minimal_body_product_code_only_resolves_and_succeeds()
    {
        // Fork B rework (bd t7o3.11 / 3k10 / c8d8): the saga now POSTs a MINIMAL body — product_id +
        // principal_cents + funding_account (+ deposit_id) — with NO structural facts. The engine
        // RESOLVES the term / interest variant / renewal policy / coupon cadence / role from its
        // deployed product-config store at constitution, IN-TRANSACTION with the rate-sheet resolve
        // (ADR-PC-008 §S2 / ADR-PC-009), so the orchestrator carries NO product-family knowledge.
        var depositId = Guid.NewGuid();
        var body = new ConstituteDepositRequest(
            PrincipalCents: 1_000_000,
            ProductId: "dpz_pt_12m_juros_venc",
            FundingAccount: "PT50-DDA-001",
            DepositId: depositId);

        var response = await PostConstituteAsync(body, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var constituted = await response.Content.ReadFromJsonAsync<ConstituteDepositResponse>(SnakeCase);
        Assert.NotNull(constituted);
        Assert.Equal("ACTIVE", constituted.Status);
        // The engine honoured the supplied deposit_id as the stream id (ce_subject = process_id pin).
        Assert.Equal(depositId, constituted.DepositId);

        // Read the position back — the STRUCTURAL facts were resolved ENGINE-side from the product config
        // (none were sent on the wire), and the TAN was resolved in-transaction from the rate sheet.
        var active = await _client.GetFromJsonAsync<DepositResponse>(
            $"/v1/deposits/{depositId}", SnakeCase);
        Assert.NotNull(active);
        Assert.Equal(1_000_000, active.PrincipalCents);
        Assert.Equal(365, active.TermDays);
        Assert.Equal("AT_MATURITY", active.InterestVariant);
        Assert.Equal("NONE", active.AutoRenewalPolicy);
        Assert.Equal(0, active.PaymentPeriodMonths);
        Assert.Equal("dpz_pt_12m_juros_venc", active.ProductCode);
        Assert.Equal(300, active.TanBasisPoints);
        Assert.Equal("pt-deposits-2026.1", active.RateSheetVersionId);
        Assert.Equal("Active", active.Lifecycle);
    }

    [Fact]
    public async Task Constitute_with_minimal_body_for_an_unknown_product_is_a_422()
    {
        // The engine is the fail-loud authority on whether a product code is known: a minimal body for a
        // product the engine holds NO config for is a clean 422 (DomainRejectedException), never a silent
        // default — the same fail-loud discipline as an unpriced (product, role).
        var body = new ConstituteDepositRequest(
            PrincipalCents: 1_000_000,
            ProductId: "dpz_pt_unknown_product",
            FundingAccount: "PT50-DDA-001");

        var response = await PostConstituteAsync(body, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Renewal_saga_legs_constitute_renewal_then_link_drive_the_full_renewal_over_HTTP()
    {
        // bd babelstone-mtto PR B end-to-end over the HTTP surface: constitute → mature (autonomous) →
        // constitute-renewal (201 + Location to the NEW stream) → renewal-link (200), driving the SAME
        // postconditions the retired monolithic RenewAsync did — the closing stream folds Renewed
        // (terminal), the new stream is Active at the carried-forward rate. SAME_TERM_SAME_RATE keeps the
        // single 300bps fixture sheet sufficient (no re-resolution).
        var closingId = await ConstituteAndMatureAsync("SAME_TERM_SAME_RATE");
        var newDepositId = Guid.NewGuid();

        // Step 2: constitute-renewal — opens the new stream, 201 with Location pointing at /v1/deposits/{newId}.
        var constituteRenewal = await PostJsonAsync(
            $"/v1/deposits/{closingId}/constitute-renewal",
            new ConstituteRenewalRequest(
                NewDepositId: newDepositId, RenewedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero)),
            Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Created, constituteRenewal.StatusCode);
        Assert.Equal($"/v1/deposits/{newDepositId}", constituteRenewal.Headers.Location?.ToString());
        var renewalBody = await constituteRenewal.Content.ReadFromJsonAsync<ConstituteRenewalResponse>(SnakeCase);
        Assert.NotNull(renewalBody);
        Assert.Equal(closingId, renewalBody.DepositId);
        Assert.Equal(newDepositId, renewalBody.NewDepositId);
        Assert.Equal("ACTIVE", renewalBody.Status);

        // The new stream reads back Active at the carried-forward 300bps original rate, with the product
        // code RECOVERED from the closing deposit (mtto.5: the minimal renewal body carries no product —
        // the engine resolves it from the closing deposit's folded state).
        var renewed = await _client.GetFromJsonAsync<DepositResponse>($"/v1/deposits/{newDepositId}", SnakeCase);
        Assert.NotNull(renewed);
        Assert.Equal("Active", renewed.Lifecycle);
        Assert.Equal(300, renewed.TanBasisPoints);
        Assert.Equal(1_000_000, renewed.PrincipalCents);
        Assert.Equal("dpz_pt_12m_juros_venc", renewed.ProductCode);

        // Step 3: renewal-link — folds the closing stream Matured → Renewed, 200.
        var link = await PostJsonAsync(
            $"/v1/deposits/{closingId}/renewal-link",
            new LinkRenewalRequest(
                NewDepositId: newDepositId, RenewedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero)),
            Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.OK, link.StatusCode);
        var linkBody = await link.Content.ReadFromJsonAsync<LinkRenewalResponse>(SnakeCase);
        Assert.NotNull(linkBody);
        Assert.Equal("RENEWED", linkBody.Status);

        // The closing stream is now terminal Renewed (read-your-writes via the returned commit token).
        var closingRequest = new HttpRequestMessage(HttpMethod.Get, $"/v1/deposits/{closingId}");
        closingRequest.Headers.TryAddWithoutValidation(
            "If-Min-Sequence", linkBody.CommitSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var closingResponse = await _client.SendAsync(closingRequest);
        var closing = await closingResponse.Content.ReadFromJsonAsync<DepositResponse>(SnakeCase);
        Assert.NotNull(closing);
        Assert.Equal("Renewed", closing.Lifecycle);

        // The new instance's DepositConstituted roots its causation at the closing DepositMatured (02 §2.4.4).
        var maturedEventId = await EventIdAsync(closingId, "term_deposit.DepositMatured");
        Assert.Equal(maturedEventId, await FirstEventCausationIdAsync(newDepositId));
    }

    [Fact]
    public async Task ENGINE_COMMAND_IDEMPOTENT_constitute_renewal_replay_returns_the_original_and_appends_once()
    {
        // ADR-PC-029 slot 4: two POSTs to constitute-renewal with the SAME Idempotency-Key (the
        // dispatcher's at-least-once retry) open the new stream ONCE. The second is short-circuited by the
        // engine's command-dedup pre-check and replays the original outcome verbatim.
        var closingId = await ConstituteAndMatureAsync("SAME_TERM_SAME_RATE");
        var newDepositId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();
        var body = new ConstituteRenewalRequest(
            NewDepositId: newDepositId, RenewedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero));

        var first = await PostJsonAsync($"/v1/deposits/{closingId}/constitute-renewal", body, key);
        var second = await PostJsonAsync($"/v1/deposits/{closingId}/constitute-renewal", body, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<ConstituteRenewalResponse>(SnakeCase);
        var secondBody = await second.Content.ReadFromJsonAsync<ConstituteRenewalResponse>(SnakeCase);
        Assert.NotNull(firstBody);
        Assert.NotNull(secondBody);

        // The replay returns the ORIGINAL identity + read-your-writes token, verbatim.
        Assert.Equal(firstBody.NewDepositId, secondBody.NewDepositId);
        Assert.Equal(firstBody.CommitSequence, secondBody.CommitSequence);

        // NO second append: exactly one DepositConstituted on the new stream.
        Assert.Equal(1, await CountAsync("events", "stream_id", newDepositId));
    }

    [Fact]
    public async Task ENGINE_COMMAND_IDEMPOTENT_renewal_link_replay_returns_the_original_and_appends_once()
    {
        // ADR-PC-029 slot 4: two POSTs to renewal-link with the SAME Idempotency-Key append DepositRenewed
        // ONCE on the closing stream and replay the original outcome.
        var closingId = await ConstituteAndMatureAsync("SAME_TERM_SAME_RATE");
        var newDepositId = Guid.NewGuid();
        await PostJsonAsync(
            $"/v1/deposits/{closingId}/constitute-renewal",
            new ConstituteRenewalRequest(
                NewDepositId: newDepositId, RenewedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero)),
            Guid.NewGuid().ToString());

        var key = Guid.NewGuid().ToString();
        var body = new LinkRenewalRequest(
            NewDepositId: newDepositId, RenewedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero));

        var first = await PostJsonAsync($"/v1/deposits/{closingId}/renewal-link", body, key);
        var second = await PostJsonAsync($"/v1/deposits/{closingId}/renewal-link", body, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<LinkRenewalResponse>(SnakeCase);
        var secondBody = await second.Content.ReadFromJsonAsync<LinkRenewalResponse>(SnakeCase);
        Assert.NotNull(firstBody);
        Assert.NotNull(secondBody);
        Assert.Equal(firstBody.CommitSequence, secondBody.CommitSequence);

        // Closing stream = Constituted, Accrued, Withheld, Matured, Renewed (5) — DepositRenewed once.
        Assert.Equal(5, await CountAsync("events", "stream_id", closingId));
    }

    [Theory]
    [InlineData(null)]          // absent: the Idempotency-Key is MANDATORY (ADR-PC-029 slot 4)
    [InlineData("not-a-uuid")]  // malformed: the command id must be a deterministic UUID
    public async Task A_renewal_leg_without_a_valid_idempotency_key_is_a_400(string? key)
    {
        var closingId = await ConstituteAndMatureAsync("SAME_TERM_SAME_RATE");
        var response = await PostJsonAsync(
            $"/v1/deposits/{closingId}/constitute-renewal",
            new ConstituteRenewalRequest(NewDepositId: Guid.NewGuid()),
            key);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Constitute_renewal_on_a_non_matured_deposit_is_a_422()
    {
        // The Matured-precondition guard at the HTTP boundary: constitute-renewal on a still-Active deposit
        // (maturity has NOT run) is a clean 422 (DomainRejectedException), never opening the new stream.
        var activeId = (await (await PostConstituteAsync(new ConstituteDepositRequest(
            PrincipalCents: 1_000_000, ProductId: "dpz_pt_12m_juros_venc", Role: "standard", TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15), InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "SAME_TERM_SAME_RATE", FundingAccount: "PT50-DDA-001"), Guid.NewGuid().ToString()))
            .Content.ReadFromJsonAsync<ConstituteDepositResponse>(SnakeCase))!.DepositId;

        var response = await PostJsonAsync(
            $"/v1/deposits/{activeId}/constitute-renewal",
            new ConstituteRenewalRequest(NewDepositId: Guid.NewGuid()),
            Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    /// <summary>Constitute a SAME_TERM_* deposit and mature it over HTTP, returning the (now Matured)
    /// closing deposit id — the renewal saga's precondition head.</summary>
    private async Task<Guid> ConstituteAndMatureAsync(string policy)
    {
        var constitute = await PostConstituteAsync(new ConstituteDepositRequest(
            PrincipalCents: 1_000_000, ProductId: "dpz_pt_12m_juros_venc", Role: "standard", TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15), InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: policy, FundingAccount: "PT50-DDA-001"), Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, constitute.StatusCode);
        var depositId = (await constitute.Content.ReadFromJsonAsync<ConstituteDepositResponse>(SnakeCase))!.DepositId;

        var maturity = await _client.PostAsJsonAsync(
            $"/v1/deposits/{depositId}/maturity",
            new MatureDepositRequest(MaturedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero)), SnakeCase);
        Assert.Equal(HttpStatusCode.OK, maturity.StatusCode);
        return depositId;
    }

    private async Task<Guid> EventIdAsync(Guid streamId, string eventType)
    {
        await using var connection = new NpgsqlConnection(_pg.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT event_id FROM events WHERE stream_id = @id AND event_type = @type", connection);
        command.Parameters.AddWithValue("id", streamId);
        command.Parameters.AddWithValue("type", eventType);
        return (Guid)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException($"no {eventType} on stream {streamId}"));
    }

    private async Task<Guid?> FirstEventCausationIdAsync(Guid streamId)
    {
        await using var connection = new NpgsqlConnection(_pg.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT causation_id FROM events WHERE stream_id = @id AND sequence_number = 0", connection);
        command.Parameters.AddWithValue("id", streamId);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull or null ? null : (Guid)value;
    }

    private async Task<HttpResponseMessage> PostConstituteAsync(ConstituteDepositRequest body, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/deposits")
        {
            Content = JsonContent.Create(body, options: SnakeCase),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostJsonAsync<TBody>(string url, TBody body, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: SnakeCase),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return await _client.SendAsync(request);
    }
}
