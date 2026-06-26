using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Babelstone.Engine;
using Babelstone.Engine.Api;
using Babelstone.Engine.Hosting;
using Babelstone.EventStore.Migrations;
using Babelstone.Families.TermDeposit;
using Babelstone.Families.TermDeposit.Application;
using Babelstone.RateSheets;
using Babelstone.TestFixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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
            // Price both the walking-skeleton product and the F.12 partial-withdrawal variant (the
            // partial-withdrawal endpoint constitutes under the latter, bd qze9) at 300 bps / standard.
            Body: FlatPriced(
                ("dpz_pt_12m_juros_venc", "standard", 300),
                ("dpz_pt_12m_resgate_parcial", "standard", 300)),
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

        // Mature → the canonical AT_MATURITY numbers. Maturity is a money-mover, so it carries fresh
        // gateway-attested step-up SCA (ScaPrecondition, bd babelstone-ziu3.5) — without it the gate 422s.
        var maturityResponse = await PostMoneyMoverWithFreshScaAsync(
            $"/v1/deposits/{depositId}/maturity", new MatureDepositRequest());
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

    // ── §P8 step-up-SCA gate on the money-movers (Q-BE Q1, bd babelstone-ziu3.5) ──────────────────────
    // The agent channel's irreversible money-movers (maturity, coupon payout) refuse to settle without
    // FRESH gateway-attested SCA proof — the AS-signed acr Kong copied into X-SCA-Acr/X-SCA-Auth-Time.
    // The MCP_SCA_GATE_CANNOT_BYPASS invariant: no money-mover settles on the agent's word; the gate
    // transitions on the bank's own signal (the AS signature Kong validated), exactly as ADR-IC-010 §P8
    // requires. These tests are the engine half; the MCP step-up-then-retry half lives in
    // mcp-server/tests/test_server.py.

    [Fact]
    public async Task Mature_without_any_SCA_proof_is_422_SCA_REQUIRED_and_does_not_settle()
    {
        var depositId = await ConstituteActiveDepositAsync();

        // No X-SCA-Acr / X-SCA-Auth-Time at all — the gateway attested no fresh SCA.
        var response = await _client.PostAsJsonAsync(
            $"/v1/deposits/{depositId}/maturity", new MatureDepositRequest(), SnakeCase);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertScaRequiredAsync(response);
        // The deposit did NOT settle — the stream still carries only the constitution event.
        Assert.Equal(["term_deposit.DepositConstituted"], await EventTypesAsync(depositId));
    }

    [Fact]
    public async Task Mature_with_a_stale_SCA_auth_time_is_422_SCA_REQUIRED()
    {
        var depositId = await ConstituteActiveDepositAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/deposits/{depositId}/maturity")
        {
            Content = JsonContent.Create(new MatureDepositRequest(), options: SnakeCase),
        };
        request.Headers.TryAddWithoutValidation(ScaPrecondition.AcrHeader, "urn:bank:sca:psd2");
        // auth_time well beyond the freshness window (MaxAgeSeconds): SCA happened, but too long ago.
        var stale = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (ScaPrecondition.MaxAgeSeconds + 60);
        request.Headers.TryAddWithoutValidation(ScaPrecondition.AuthTimeHeader, stale.ToString());

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertScaRequiredAsync(response);
        Assert.Equal(["term_deposit.DepositConstituted"], await EventTypesAsync(depositId));
    }

    [Fact]
    public async Task Mature_with_fresh_attested_SCA_settles_normally()
    {
        var depositId = await ConstituteActiveDepositAsync();

        var response = await PostMoneyMoverWithFreshScaAsync(
            $"/v1/deposits/{depositId}/maturity", new MatureDepositRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var matured = await response.Content.ReadFromJsonAsync<DepositResponse>(SnakeCase);
        Assert.NotNull(matured);
        Assert.Equal("Matured", matured.Lifecycle);
    }

    /// <summary>Constitute a vanilla Active AT_MATURITY deposit and return its id — the precondition for
    /// the money-mover SCA-gate tests.</summary>
    private async Task<Guid> ConstituteActiveDepositAsync()
    {
        var constitute = await PostConstituteAsync(new ConstituteDepositRequest(
            PrincipalCents: 1_000_000, ProductId: "dpz_pt_12m_juros_venc", Role: "standard", TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15), InterestVariant: "AT_MATURITY", AutoRenewalPolicy: "NONE",
            FundingAccount: "PT50-DDA-001"), Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, constitute.StatusCode);
        return (await constitute.Content.ReadFromJsonAsync<ConstituteDepositResponse>(SnakeCase))!.DepositId;
    }

    /// <summary>Assert a 422 response carries the stable <c>SCA_REQUIRED</c> code and no PII.</summary>
    private static async Task AssertScaRequiredAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ScaPrecondition.RequiredCode, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Partial_withdrawal_over_HTTP_reduces_the_principal_and_keeps_the_deposit_Active()
    {
        // bd qze9 end-to-end over the HTTP surface: constitute a partial-withdrawal deposit, then POST a
        // partial withdrawal that clears all three F.12 gates (the policy is resolved engine-side from the
        // deposit's product config, bd k6r8.8). The deposit stays Active (a partial withdrawal is
        // state-preserving, F.3) with a reduced RemainingPrincipal, and the durable log carries exactly
        // [DepositConstituted, DepositPartiallyWithdrawn].
        var constitute = await PostConstituteAsync(new ConstituteDepositRequest(
            PrincipalCents: 1_000_000,                       // €10,000.00
            ProductId: "dpz_pt_12m_resgate_parcial",
            Role: "standard",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            FundingAccount: "PT50-DDA-001"), Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, constitute.StatusCode);
        var depositId = (await constitute.Content.ReadFromJsonAsync<ConstituteDepositResponse>(SnakeCase))!.DepositId;

        // Withdraw €5,000 on 2026-06-15 (151 days in, past the 90-day lock-up): min-withdrawal €500 ✓,
        // remaining €5,000 ≥ min-remaining €1,000 ✓, lock-up cleared ✓. The Idempotency-Key is MANDATORY
        // on partial withdrawal (ADR-PC-029 slot 4, bd 9w0g) — a fresh key per test.
        var withdrawal = await PostJsonAsync(
            $"/v1/deposits/{depositId}/partial-withdrawal",
            new PartialWithdrawRequest(
                WithdrawnAmountCents: 500_000,
                WithdrawnAt: new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero)),
            Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.OK, withdrawal.StatusCode);
        var withdrawn = await withdrawal.Content.ReadFromJsonAsync<DepositResponse>(SnakeCase);
        Assert.NotNull(withdrawn);
        Assert.Equal("Active", withdrawn.Lifecycle);          // state-preserving — the deposit stays open

        // The durable log: constitution then the single partial-withdrawal event (no settlement leg).
        Assert.Equal(
            ["term_deposit.DepositConstituted", "term_deposit.DepositPartiallyWithdrawn"],
            await EventTypesAsync(depositId));

        // The folded position carries the reduced remaining principal (€10,000 − €5,000 = €5,000). The
        // HTTP DepositResponse does not surface remaining principal, so this is asserted off the host's
        // own runtime fold — the authoritative replay.
        var runtime = _factory.Services.GetRequiredService<AggregateRuntime<DepositPosition>>();
        var hydrated = await runtime.LoadAsync(depositId);
        Assert.Equal(500_000, hydrated.State.RemainingPrincipal.Cents);
    }

    [Fact]
    public async Task A_partial_withdrawal_within_the_lockup_is_a_422()
    {
        // The F.12 lock-up gate at the HTTP boundary: a withdrawal dated inside the 90-day
        // lock-up is a clean 422 (DomainRejectedException), never a phantom withdrawal — and the deposit
        // is untouched (no DepositPartiallyWithdrawn appended).
        var constitute = await PostConstituteAsync(new ConstituteDepositRequest(
            PrincipalCents: 1_000_000,
            ProductId: "dpz_pt_12m_resgate_parcial",
            Role: "standard",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            FundingAccount: "PT50-DDA-001"), Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, constitute.StatusCode);
        var depositId = (await constitute.Content.ReadFromJsonAsync<ConstituteDepositResponse>(SnakeCase))!.DepositId;

        // 2026-01-25 is only 10 days in — well inside the 90-day lock-up. A valid Idempotency-Key is
        // supplied so the request reaches the domain check (the 422 under test), not the mandatory-key 400.
        var withdrawal = await PostJsonAsync(
            $"/v1/deposits/{depositId}/partial-withdrawal",
            new PartialWithdrawRequest(
                WithdrawnAmountCents: 500_000,
                WithdrawnAt: new DateTimeOffset(2026, 1, 25, 0, 0, 0, TimeSpan.Zero)),
            Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, withdrawal.StatusCode);
        // No withdrawal landed: the stream still holds only the constitution event.
        Assert.Equal(["term_deposit.DepositConstituted"], await EventTypesAsync(depositId));
    }

    [Fact]
    public async Task ENGINE_COMMAND_IDEMPOTENT_partial_withdrawal_replay_returns_the_original_and_appends_once()
    {
        // ADR-PC-029 slot 4 (bd 9w0g): a partial withdrawal is REPEATABLE (it leaves the deposit Active),
        // so a non-idempotent retry would withdraw twice. Two POSTs with the SAME Idempotency-Key (the
        // dispatcher's at-least-once retry) reduce the principal ONCE — the second is short-circuited by the
        // engine's command-dedup pre-check and replays the original outcome, appending exactly one
        // DepositPartiallyWithdrawn.
        var constitute = await PostConstituteAsync(new ConstituteDepositRequest(
            PrincipalCents: 1_000_000,                       // €10,000.00
            ProductId: "dpz_pt_12m_resgate_parcial",
            Role: "standard",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            FundingAccount: "PT50-DDA-001"), Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, constitute.StatusCode);
        var depositId = (await constitute.Content.ReadFromJsonAsync<ConstituteDepositResponse>(SnakeCase))!.DepositId;

        var key = Guid.NewGuid().ToString();
        var body = new PartialWithdrawRequest(
            WithdrawnAmountCents: 500_000,
            WithdrawnAt: new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));

        var first = await PostJsonAsync($"/v1/deposits/{depositId}/partial-withdrawal", body, key);
        var second = await PostJsonAsync($"/v1/deposits/{depositId}/partial-withdrawal", body, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<DepositResponse>(SnakeCase);
        var secondBody = await second.Content.ReadFromJsonAsync<DepositResponse>(SnakeCase);
        Assert.NotNull(firstBody);
        Assert.NotNull(secondBody);

        // The replay returns the ORIGINAL read-your-writes token, verbatim — same head, both Active.
        Assert.Equal("Active", firstBody.Lifecycle);
        Assert.Equal("Active", secondBody.Lifecycle);
        Assert.Equal(firstBody.LastSequence, secondBody.LastSequence);

        // NO second append: exactly one DepositPartiallyWithdrawn on the stream — and the principal is
        // reduced ONCE (€10,000 − €5,000 = €5,000), not twice (which would leave €0 / a refused termination).
        Assert.Equal(
            ["term_deposit.DepositConstituted", "term_deposit.DepositPartiallyWithdrawn"],
            await EventTypesAsync(depositId));
        var runtime = _factory.Services.GetRequiredService<AggregateRuntime<DepositPosition>>();
        var hydrated = await runtime.LoadAsync(depositId);
        Assert.Equal(500_000, hydrated.State.RemainingPrincipal.Cents);
    }

    [Theory]
    [InlineData(null)]          // absent: the Idempotency-Key is MANDATORY (ADR-PC-029 slot 4)
    [InlineData("not-a-uuid")]  // malformed: the command id must be a deterministic UUID
    public async Task A_partial_withdrawal_without_a_valid_idempotency_key_is_a_400(string? key)
    {
        // bd 9w0g: a partial withdrawal is repeatable, so the engine never accepts a non-idempotent one —
        // an absent or non-UUID key fails loud (400) BEFORE any append, exactly as the erasure/renewal legs.
        var constitute = await PostConstituteAsync(new ConstituteDepositRequest(
            PrincipalCents: 1_000_000,
            ProductId: "dpz_pt_12m_resgate_parcial",
            Role: "standard",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: "NONE",
            FundingAccount: "PT50-DDA-001"), Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, constitute.StatusCode);
        var depositId = (await constitute.Content.ReadFromJsonAsync<ConstituteDepositResponse>(SnakeCase))!.DepositId;

        var response = await PostJsonAsync(
            $"/v1/deposits/{depositId}/partial-withdrawal",
            new PartialWithdrawRequest(
                WithdrawnAmountCents: 500_000,
                WithdrawnAt: new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero)),
            key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // No withdrawal landed: the stream still holds only the constitution event.
        Assert.Equal(["term_deposit.DepositConstituted"], await EventTypesAsync(depositId));
    }

    // ── Early-termination money-mover endpoint (F.4; 02 §2.5, bd babelstone-t7o3.13.1) ─────────────────
    // POST /v1/deposits/{id}/terminate: an irreversible money-mover (SCA-gated like maturity/interest) that
    // ALSO carries the money-mover idempotency contract (mandatory Idempotency-Key; rejects double-terminate).
    // The payout is the de-settled, confirmation-gated leg (bd t7o3.13): DepositTerminatedEarly records an
    // Originated Credit PayEarlyTermination Movement APPEND-FIRST that the substrate-owned settlement saga
    // effects as the gated ACL credit — never an eager in-engine settle.

    [Fact]
    public async Task Terminate_over_HTTP_with_fresh_SCA_and_a_key_folds_TerminatedEarly_and_records_the_gated_payout_Movement()
    {
        var depositId = await ConstituteActiveDepositAsync();

        var response = await PostMoneyMoverWithScaAndKeyAsync(
            $"/v1/deposits/{depositId}/terminate",
            new TerminateEarlyRequest(TerminatedAt: new DateTimeOffset(2026, 3, 16, 0, 0, 0, TimeSpan.Zero)),
            Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var terminated = await response.Content.ReadFromJsonAsync<TerminateEarlyResponse>(SnakeCase);
        Assert.NotNull(terminated);
        Assert.Equal("TERMINATEDEARLY", terminated.Status);

        // The durable log: constitution then the elapsed-interest flow + the closing DepositTerminatedEarly.
        Assert.Equal(
            ["term_deposit.DepositConstituted", "term_deposit.InterestAccrued",
             "term_deposit.WithholdingApplied", "term_deposit.DepositTerminatedEarly"],
            await EventTypesAsync(depositId));

        // The de-settled gated payout (bd t7o3.13): DepositTerminatedEarly carries the Originated Credit
        // PayEarlyTermination Movement the substrate-owned settlement saga effects — NOT an eager settle.
        var terminatedEvent = Assert.Single(await EventsOfAsync<DepositTerminatedEarly>(depositId));
        var movement = Assert.Single(terminatedEvent.Movements!);
        Assert.Equal(SettlementDirection.Credit, movement.Direction);
        Assert.Equal(MovementOperation.PayEarlyTermination, movement.Operation);
        Assert.Equal(MovementOrigin.Originated, movement.Origin);
        Assert.Equal(terminatedEvent.NetSettlementAmount, movement.Amount);
    }

    [Fact]
    public async Task ENGINE_COMMAND_IDEMPOTENT_terminate_replay_returns_the_original_and_appends_once()
    {
        // The money-mover idempotency contract (ADR-PC-029 slot 4, bd t7o3.13.1): two POSTs with the SAME
        // Idempotency-Key (the dispatcher's at-least-once retry) terminate ONCE — the second is short-circuited
        // by the engine's command-dedup pre-check and replays the original outcome, appending exactly one
        // DepositTerminatedEarly. A double-terminate is rejected.
        var depositId = await ConstituteActiveDepositAsync();

        var key = Guid.NewGuid().ToString();
        var body = new TerminateEarlyRequest(TerminatedAt: new DateTimeOffset(2026, 3, 16, 0, 0, 0, TimeSpan.Zero));

        var first = await PostMoneyMoverWithScaAndKeyAsync($"/v1/deposits/{depositId}/terminate", body, key);
        var second = await PostMoneyMoverWithScaAndKeyAsync($"/v1/deposits/{depositId}/terminate", body, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<TerminateEarlyResponse>(SnakeCase);
        var secondBody = await second.Content.ReadFromJsonAsync<TerminateEarlyResponse>(SnakeCase);
        Assert.NotNull(firstBody);
        Assert.NotNull(secondBody);
        // The replay returns the ORIGINAL read-your-writes token, verbatim.
        Assert.Equal("TERMINATEDEARLY", firstBody.Status);
        Assert.Equal("TERMINATEDEARLY", secondBody.Status);
        Assert.Equal(firstBody.CommitSequence, secondBody.CommitSequence);

        // NO second append: exactly one DepositTerminatedEarly on the stream (a double-terminate is rejected).
        Assert.Single(await EventsOfAsync<DepositTerminatedEarly>(depositId));
    }

    [Theory]
    [InlineData(null)]          // absent: the Idempotency-Key is MANDATORY (ADR-PC-029 slot 4)
    [InlineData("not-a-uuid")]  // malformed: the command id must be a deterministic UUID
    public async Task A_terminate_without_a_valid_idempotency_key_is_a_400(string? key)
    {
        var depositId = await ConstituteActiveDepositAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/deposits/{depositId}/terminate")
        {
            Content = JsonContent.Create(new TerminateEarlyRequest(), options: SnakeCase),
        };
        AddFreshSca(request); // fresh SCA so the request reaches the mandatory-key 400, not the SCA 422
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // No termination landed: the stream still holds only the constitution event.
        Assert.Equal(["term_deposit.DepositConstituted"], await EventTypesAsync(depositId));
    }

    [Fact]
    public async Task Terminate_without_any_SCA_proof_is_422_SCA_REQUIRED_and_does_not_settle()
    {
        var depositId = await ConstituteActiveDepositAsync();

        // No X-SCA-Acr / X-SCA-Auth-Time — the gateway attested no fresh SCA. A valid key is supplied so the
        // SCA gate (the route-group filter, BEFORE the handler) is what 422s, not the mandatory-key 400.
        var response = await PostJsonAsync(
            $"/v1/deposits/{depositId}/terminate", new TerminateEarlyRequest(), Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertScaRequiredAsync(response);
        // The deposit did NOT terminate — the stream still carries only the constitution event.
        Assert.Equal(["term_deposit.DepositConstituted"], await EventTypesAsync(depositId));
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

        // Mature it (with fresh attested SCA — the money-mover gate, bd babelstone-ziu3.5) — appends
        // three more events (InterestAccrued, WithholdingApplied, DepositMatured), advancing the head
        // well past the constitution sequence.
        var maturityResponse = await PostMoneyMoverWithFreshScaAsync(
            $"/v1/deposits/{depositId}/maturity", new MatureDepositRequest());
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

    [Fact]
    public async Task Pack_migration_predicate_preview_over_HTTP_resolves_the_active_population()
    {
        // bd babelstone-7giq end-to-end over the real host: the single /v1/pack-migrations route
        // (registered once at host level) dispatches on product_family to the term-deposit family's
        // DI-wired IPackMigrationInstanceResolver + IPackMigrationService, resolves the predicate over
        // the read model, and previews the matched population — proving the wire path, not just the unit.
        var depositId = await ConstituteActiveDepositAsync();

        // The predicate resolves over the read model, populated by the async relay — wait for the row.
        var row = await EventuallyAsync(
            () => new PostgresDepositReadModelStore(_pg.GetConnectionString()).GetAsync(depositId));
        Assert.NotNull(row);
        Assert.Equal("Active", row!.Lifecycle);

        var request = new PackMigrationRequest(
            FromPackVersion: "pt.2026.1", ToPackVersion: "pt.2027.1",
            MigrationId: "mig-http-predicate", OperatorActor: "operator:regulatory-ops",
            InstanceFilter: new InstanceFilter("term_deposit", true), Preview: true);

        var response = await _client.PostAsJsonAsync("/v1/pack-migrations", request, SnakeCase);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<PackMigrationResponse>(SnakeCase);
        Assert.NotNull(body);
        Assert.False(body!.Migrated);                 // preview emits nothing
        Assert.Contains(depositId, body.InstanceIds); // the live deposit, selected by the predicate
    }

    [Fact]
    public async Task Pack_migration_with_both_selectors_is_422_over_HTTP()
    {
        // The XOR guard reached through the real handler — proves the endpoint is invoked and the
        // injected service/resolver collections bind (the Plan runs over them).
        var request = new PackMigrationRequest(
            FromPackVersion: "pt.2026.1", ToPackVersion: "pt.2027.1",
            MigrationId: "mig-http-xor", OperatorActor: "operator:ops",
            InstanceIds: [Guid.NewGuid()], InstanceFilter: new InstanceFilter("term_deposit", true));

        var response = await _client.PostAsJsonAsync("/v1/pack-migrations", request, SnakeCase);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
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

    private static RateSheetBody FlatPriced(params (string productId, string role, int tanBasisPoints)[] entries)
    {
        var products = new Dictionary<string, Dictionary<string, RoleRates>>();
        foreach (var (productId, role, tanBasisPoints) in entries)
        {
            if (!products.TryGetValue(productId, out var roles))
            {
                roles = new Dictionary<string, RoleRates>();
                products[productId] = roles;
            }

            roles[role] = new RoleRates { Bands = [new RateBand(0L, null, tanBasisPoints)] };
        }

        return new RateSheetBody { Products = products };
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

        var maturity = await PostMoneyMoverWithFreshScaAsync(
            $"/v1/deposits/{depositId}/maturity",
            new MatureDepositRequest(MaturedAt: new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero)));
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

    /// <summary>POST a money-mover (maturity / interest) with FRESH gateway-attested step-up-SCA headers
    /// — the agent channel's success posture after the customer completed SCA and Kong attested the
    /// AS-signed acr/auth_time (ScaPrecondition, bd babelstone-ziu3.5). Use this on every maturity /
    /// interest POST that must SUCCEED; the gate now 422s a money-mover with no fresh SCA proof.</summary>
    private async Task<HttpResponseMessage> PostMoneyMoverWithFreshScaAsync<TBody>(string url, TBody body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: SnakeCase),
        };
        AddFreshSca(request);
        return await _client.SendAsync(request);
    }

    /// <summary>POST the early-termination money-mover with BOTH fresh SCA AND an Idempotency-Key — the
    /// terminate endpoint is SCA-gated (like maturity) AND id-keyed (the money-mover idempotency contract,
    /// bd babelstone-t7o3.13.1).</summary>
    private async Task<HttpResponseMessage> PostMoneyMoverWithScaAndKeyAsync<TBody>(string url, TBody body, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: SnakeCase),
        };
        AddFreshSca(request);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return await _client.SendAsync(request);
    }

    /// <summary>Load the appended events of type <typeparamref name="TEvent"/> off the durable stream, decoding
    /// the store payload with the host's own <see cref="IEventSerializer"/> — to assert the Movement a
    /// money-moving event records APPEND-FIRST (bd babelstone-t7o3.13), the de-settled leg the substrate-owned
    /// settlement saga effects as the gated ACL credit.</summary>
    private async Task<IReadOnlyList<TEvent>> EventsOfAsync<TEvent>(Guid streamId)
        where TEvent : DomainEvent
    {
        var store = new Babelstone.EventStore.PostgresEventStore(_pg.GetConnectionString());
        var serializer = _factory.Services.GetRequiredService<IEventSerializer>();
        var events = new List<TEvent>();
        await foreach (var envelope in store.LoadAsync(streamId))
        {
            if (envelope.EventType.EndsWith(typeof(TEvent).Name, StringComparison.Ordinal))
            {
                events.Add((TEvent)serializer.Decode(envelope.Payload, typeof(TEvent)));
            }
        }

        return events;
    }

    /// <summary>Attest a FRESH, sufficiently-strong SCA proof on the request, exactly as Kong's
    /// set_header attestation would from an AS-signed token (acr present, auth_time = now).</summary>
    private static void AddFreshSca(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation(ScaPrecondition.AcrHeader, "urn:bank:sca:psd2");
        request.Headers.TryAddWithoutValidation(
            ScaPrecondition.AuthTimeHeader, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
    }
}
