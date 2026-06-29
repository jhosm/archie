using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Babelstone.Engine;
using Babelstone.Engine.Hosting;
using Babelstone.EventStore;
using Babelstone.EventStore.Migrations;
using Babelstone.Families.PersonalLoan;
using Babelstone.Families.PersonalLoan.Application;
using Babelstone.RateSheets;
using Babelstone.TestFixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Engine.Api.Tests;

/// <summary>
/// End-to-end disburse → pay → settle over the real Babelstone.Engine.Api host + real PostgreSQL
/// (bd babelstone-9g77): the proof that the personal_loan (credito_pessoal) family is OPERABLE in the
/// running engine, not just reachable from its own test projects. The host boots via
/// <see cref="WebApplicationFactory{Program}"/> (the SAME Program the deposits integration tests boot), so
/// it discovers <c>PersonalLoanHostModule</c> by assembly-scan (ADR-PC-031 §P5), cross-checks it against the
/// pinned pack's family-manifest (now pinning <c>personal_loan@2026.1</c>, fail-closed — ADR-PC-009 §P1),
/// composes its <c>AggregateRuntime&lt;LoanPosition&gt;</c> + decider, and maps the <c>/v1/loans</c> surface.
///
/// The flow exercises the whole amortizing lifecycle through HTTP: disburse a 2-installment loan, pay the
/// first installment (the loan stays Active), then pay the final installment (the balance reaches zero and
/// the loan SETTLES). No eager settlement on any path — each money leg rides an append-first Movement for
/// the substrate-owned settlement saga (ADR-PC-032 slot 5). Tagged Integration (Testcontainers lane).
/// </summary>
[Trait("Category", "Integration")]
[Collection(EngineApiHostCollection.Name)]
public sealed class LoansApiIntegrationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions SnakeCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _pg.GatedStartAsync();
        // Engine event-store schema first (it creates the babelstone_engine role family read models GRANT
        // on), then the term-deposit family's read-model migration — the host's term-deposit module applies
        // it at boot too, but applying it up front keeps the boot deterministic. personal_loan needs NO
        // family migration (the bitemporal projections suffice — no denormalized loan read-model table).
        await new MigrationRunner(_pg.GetConnectionString()).ApplyAsync();
        await new Babelstone.Families.TermDeposit.Application.Migrations.MigrationRunner(
            _pg.GetConnectionString()).ApplyAsync();

        // Deploy the personal_loan rate sheet the disburse flow resolves (900 bps for the test product).
        var rateSheets = new PostgresRateSheetStore(_pg.GetConnectionString());
        await rateSheets.InsertAsync(new RateSheet(
            RateSheetVersionId: "pt-loans-2026.1",
            ProductFamily: "personal_loan",
            PackVersion: "pt.2026.1",
            EffectiveFrom: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Body: new RateSheetBody
            {
                Products = new Dictionary<string, Dictionary<string, RoleRates>>
                {
                    ["cp_pt_general_2m"] = new()
                    {
                        ["standard"] = new RoleRates { Bands = [new RateBand(0L, null, 900)] },
                    },
                },
            },
            ApprovedBy: "alm@bank.pt",
            ApprovalRef: "RC-2026-LOAN-001",
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
    public async Task Disburse_then_pay_both_installments_settles_the_loan_end_to_end()
    {
        var loanId = Guid.NewGuid();

        // 1. Disburse a 2-installment loan (POST /v1/loans). 201 Created, the loan folds to ACTIVE.
        var disburse = await _client.PostAsJsonAsync("/v1/loans", new
        {
            loan_id = loanId,
            principal_cents = 200_000L,
            product_id = "cp_pt_general_2m",
            role = "standard",
            term_months = 2,
            start_date = "2026-01-15",
            purpose = "general",
            disbursement_account_ref = "acct-ref-001",
        }, SnakeCase);
        Assert.Equal(HttpStatusCode.Created, disburse.StatusCode);

        var afterDisburse = await GetLoanAsync(loanId);
        Assert.Equal("ACTIVE", afterDisburse.GetProperty("status").GetString());
        Assert.Equal(2, afterDisburse.GetProperty("term_months").GetInt32());
        Assert.Equal(0, afterDisburse.GetProperty("installments_paid").GetInt32());
        Assert.True(afterDisburse.GetProperty("outstanding_balance_cents").GetInt64() > 0);

        // 2. Pay the first installment (server-derived number-pinned key — no caller key). The loan stays ACTIVE.
        var pay1 = await PayInstallmentAsync(loanId, "acct-ref-001");
        Assert.Equal(HttpStatusCode.OK, pay1.StatusCode);
        var afterPay1 = await GetLoanAsync(loanId);
        Assert.Equal("ACTIVE", afterPay1.GetProperty("status").GetString());
        Assert.Equal(1, afterPay1.GetProperty("installments_paid").GetInt32());

        // 3. Pay the final installment — the balance reaches zero and the loan SETTLES (LoanInstallmentPaid
        //    pairs with a closing LoanSettled).
        var pay2 = await PayInstallmentAsync(loanId, "acct-ref-001");
        Assert.Equal(HttpStatusCode.OK, pay2.StatusCode);
        var afterPay2 = await GetLoanAsync(loanId);
        Assert.Equal("SETTLED", afterPay2.GetProperty("status").GetString());
        Assert.Equal(2, afterPay2.GetProperty("installments_paid").GetInt32());
        Assert.Equal(0L, afterPay2.GetProperty("outstanding_balance_cents").GetInt64());
    }

    [Fact]
    public async Task Paying_an_installment_needs_no_caller_idempotency_key_the_key_is_server_derived()
    {
        // The installment key provenance INVERTED from caller-supplied to server-derived (ADR-PC-036
        // §Decision 1+3 / LCD-1; ADR-PC-029 slot 4, AMENDED): the old mandatory-caller-key contract is
        // retired. A POST with NO Idempotency-Key header now SUCCEEDS — the endpoint derives the
        // number-pinned key from the stable installment NUMBER, so the manual path is provably idempotent
        // without the caller having to invent a key.
        var loanId = Guid.NewGuid();
        await DisburseTwoInstallmentLoanAsync(loanId);

        // No Idempotency-Key header, no caller key of any kind — the installment still applies (200 OK). SCA
        // is a SEPARATE axis (bd babelstone-6cpq.14): the installment is now a money-mover behind the step-up
        // gate, so fresh gateway-attested SCA is supplied — but what's under test here is the absence of a
        // CALLER idempotency key, NOT the SCA proof, so the key is still deliberately omitted.
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/loans/{loanId}/installment")
        {
            Content = JsonContent.Create(new { collection_account_ref = "acct-ref-001" }, options: SnakeCase),
        };
        AddFreshSca(request);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await GetLoanAsync(loanId)).GetProperty("installments_paid").GetInt32());
    }

    [Fact]
    public async Task LIFECYCLE_COMMAND_NUMBER_PINNED_IDEMPOTENT_a_redated_retry_of_an_installment_dedupes_to_one_leg()
    {
        // LCD-1 (ADR-PC-036 §Decision 1+3; ADR-PC-029 slot 4). In plain English: when the lifecycle-command
        // driver (or a person) pays the SAME loan installment twice — even on DIFFERENT business dates — the
        // engine must collect the money only ONCE. It guarantees this by deriving the idempotency key
        // SERVER-side from the stable installment NUMBER, never the due-date, so the two firings carry the
        // SAME key and the second is swallowed by command_dedup. Because PayInstallment is legal repeatedly
        // from Active, that key is the ONLY guard — the legality gate gives no backstop.
        var loanId = Guid.NewGuid();
        // A 2-installment loan, so occurrence 1 is an INTERMEDIATE (non-final) installment that leaves the
        // loan Active — the repeatable case the number-pinned key must guard (a final installment would
        // settle and lean on the legality gate instead).
        await DisburseTwoInstallmentLoanAsync(loanId);

        // Firing #1 (the driver's first pass): pay occurrence 1 on its scheduled due-date. 200 OK.
        var firstDueDate = new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero);
        var first = await PayInstallmentAsync(loanId, "acct-ref-001", firstDueDate);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(1, (await GetLoanAsync(loanId)).GetProperty("installments_paid").GetInt32());

        // The endpoint derived occurrence 1's key from the stable installment NUMBER, server-side: the
        // command_dedup receipt exists under EXACTLY LifecycleCommandKey.Derive(loan, "pay_installment", 1),
        // proving the key is number-pinned and server-derived (not caller-supplied, not due-date-derived).
        var occurrence1Key = LifecycleCommandKey.Derive(loanId, "pay_installment", 1);
        var commandLog = _factory.Services.GetRequiredService<ICommandLog>();
        var receipt = await commandLog.TryGetAsync(occurrence1Key);
        Assert.NotNull(receipt);
        Assert.Equal(loanId, receipt.StreamId);

        // Firing #2 — a re-dated / backfilled retry of the SAME occurrence 1 on a DIFFERENT due-date (e.g.
        // the driver re-firing after an outage with a later business date). It presents occurrence 1's
        // number-pinned key with PaidAt = secondDueDate. command_dedup swallows it: DuplicateCommandException
        // carrying the ORIGINAL head, NO second append. The due-date (secondDueDate != firstDueDate) played
        // no part in the key — the stable installment NUMBER did.
        var service = _factory.Services.GetRequiredService<PersonalLoanConstitutionService>();
        var secondDueDate = new DateTimeOffset(2026, 3, 20, 0, 0, 0, TimeSpan.Zero);
        var dup = await Assert.ThrowsAsync<DuplicateCommandException>(() =>
            service.PayInstallmentAsync(new PayInstallmentCommand(
                loanId, secondDueDate, "acct-ref-001", "ops:loan-officer", occurrence1Key)));
        Assert.Equal(occurrence1Key, dup.CommandId);
        Assert.Equal(receipt.CommitSequence, dup.CommitSequence); // the ORIGINAL outcome, verbatim

        // ONE Originated money leg: exactly one LoanInstallmentPaid on the stream (after the disbursement),
        // and the paid-count is still 1 — the re-dated retry moved no money twice.
        Assert.Equal(
            ["personal_loan.LoanDisbursed", "personal_loan.LoanInstallmentPaid"],
            await EventTypesAsync(loanId));
        Assert.Equal(1, (await GetLoanAsync(loanId)).GetProperty("installments_paid").GetInt32());
    }

    [Fact]
    public async Task Get_an_unknown_loan_is_404()
    {
        var response = await _client.GetAsync($"/v1/loans/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── §P8 step-up-SCA gate on the loan money-movers (bd babelstone-6cpq.14) ───────────────────────────
    // The loan installment is an irreversible money-mover (it collects the scheduled installment from the
    // customer), so — exactly like the deposit maturity / coupon — it refuses to settle without FRESH
    // gateway-attested SCA. This is what makes the MCP_SCA_GATE_CANNOT_BYPASS invariant genuinely cover
    // loans: no money-mover settles on the agent's word; the gate transitions on the bank's own signal (the
    // AS-signed acr/auth_time Kong attests), enforced by the SHARED ScaPreconditionFilter on the money-mover
    // route group in LoansEndpoints.Map (ADR-IC-010 §P8 / ADR-PC-010 §P5). These are the engine half.

    [Fact]
    public async Task MCP_SCA_GATE_CANNOT_BYPASS_paying_an_installment_without_SCA_is_422_and_does_not_collect()
    {
        var loanId = Guid.NewGuid();
        await DisburseTwoInstallmentLoanAsync(loanId);

        // No X-SCA-Acr / X-SCA-Auth-Time at all — the gateway attested no fresh SCA. The route-group filter
        // 422s BEFORE any side effect, so the installment is NEVER collected.
        var response = await _client.PostAsJsonAsync(
            $"/v1/loans/{loanId}/installment", new { collection_account_ref = "acct-ref-001" }, SnakeCase);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertScaRequiredAsync(response);
        // NOTHING collected: the stream still carries only the disbursement, and the paid-count is unchanged.
        Assert.Equal(["personal_loan.LoanDisbursed"], await EventTypesAsync(loanId));
        Assert.Equal(0, (await GetLoanAsync(loanId)).GetProperty("installments_paid").GetInt32());
    }

    [Fact]
    public async Task Paying_an_installment_with_a_stale_SCA_auth_time_is_422_and_does_not_collect()
    {
        var loanId = Guid.NewGuid();
        await DisburseTwoInstallmentLoanAsync(loanId);

        // SCA happened, but too long ago (auth_time well beyond ScaPrecondition.MaxAgeSeconds): a money-mover
        // needs RECENT SCA, not merely ever-completed — so the gate 422s and nothing is collected.
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/loans/{loanId}/installment")
        {
            Content = JsonContent.Create(new { collection_account_ref = "acct-ref-001" }, options: SnakeCase),
        };
        request.Headers.TryAddWithoutValidation(ScaPrecondition.AcrHeader, "urn:bank:sca:psd2");
        var stale = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (ScaPrecondition.MaxAgeSeconds + 60);
        request.Headers.TryAddWithoutValidation(ScaPrecondition.AuthTimeHeader, stale.ToString());

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertScaRequiredAsync(response);
        Assert.Equal(["personal_loan.LoanDisbursed"], await EventTypesAsync(loanId));
        Assert.Equal(0, (await GetLoanAsync(loanId)).GetProperty("installments_paid").GetInt32());
    }

    [Fact]
    public async Task Paying_an_installment_with_fresh_attested_SCA_settles_the_installment()
    {
        var loanId = Guid.NewGuid();
        await DisburseTwoInstallmentLoanAsync(loanId);

        // The success posture: fresh gateway-attested SCA (the agent / customer flow after step-up).
        var response = await PayInstallmentAsync(loanId, "acct-ref-001");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await GetLoanAsync(loanId)).GetProperty("installments_paid").GetInt32());
    }

    [Fact]
    public async Task Paying_an_installment_with_the_scoped_service_principal_and_no_human_SCA_settles()
    {
        var loanId = Guid.NewGuid();
        await DisburseTwoInstallmentLoanAsync(loanId);

        // NO human X-SCA-Acr / X-SCA-Auth-Time — the ADR-PC-036 lifecycle-command driver is a machine actor
        // with none. The scoped X-SCA-Service-Principal claim carrying the LOAN money-mover scope (bd
        // babelstone-6cpq.9 / .14) alone authorises the loan installment money-mover. It is the loan-specific
        // scope, NOT the deposit one — cross-family isolation (the next test pins the deposit scope is refused).
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/loans/{loanId}/installment")
        {
            Content = JsonContent.Create(new { collection_account_ref = "acct-ref-001" }, options: SnakeCase),
        };
        request.Headers.TryAddWithoutValidation(
            ScaServicePrincipal.PrincipalHeader, ScaServicePrincipal.LoanMoneyMoverScope);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await GetLoanAsync(loanId)).GetProperty("installments_paid").GetInt32());
    }

    [Fact]
    public async Task A_deposit_scoped_principal_cannot_pay_a_loan_installment_cross_family_isolation()
    {
        var loanId = Guid.NewGuid();
        await DisburseTwoInstallmentLoanAsync(loanId);

        // Scoped-not-blanket ACROSS families (bd babelstone-6cpq.14): a principal carrying the DEPOSIT
        // money-mover scope — not the loan one — is refused on the loan installment route, and with no human
        // SCA either it falls through to the fail-closed 422. A leaked deposit-driver token cannot collect a
        // loan installment.
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/loans/{loanId}/installment")
        {
            Content = JsonContent.Create(new { collection_account_ref = "acct-ref-001" }, options: SnakeCase),
        };
        request.Headers.TryAddWithoutValidation(
            ScaServicePrincipal.PrincipalHeader, ScaServicePrincipal.DepositMoneyMoverScope);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertScaRequiredAsync(response);
        Assert.Equal(["personal_loan.LoanDisbursed"], await EventTypesAsync(loanId));
        Assert.Equal(0, (await GetLoanAsync(loanId)).GetProperty("installments_paid").GetInt32());
    }

    private async Task<HttpResponseMessage> PayInstallmentAsync(
        Guid loanId, string collectionAccountRef, DateTimeOffset? paidAt = null)
    {
        // The installment endpoint derives its idempotency key SERVER-side (ADR-PC-036 / LCD-1), so the
        // client sends NO Idempotency-Key header — the old mandatory-caller-key contract is retired. But the
        // installment is now an irreversible money-mover behind the step-up-SCA gate (bd babelstone-6cpq.14),
        // so a SUCCESSFUL POST must carry FRESH gateway-attested SCA, exactly as the deposit money-movers do.
        // The optional paid_at lets a test contrast two firings on DIFFERENT due-dates (host-stamped when null).
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/loans/{loanId}/installment")
        {
            Content = JsonContent.Create(
                new { collection_account_ref = collectionAccountRef, paid_at = paidAt }, options: SnakeCase),
        };
        AddFreshSca(request);
        return await _client.SendAsync(request);
    }

    /// <summary>Attest a FRESH, sufficiently-strong SCA proof on the request, exactly as Kong's set_header
    /// attestation would from an AS-signed token (acr present, auth_time = now) — the agent / customer
    /// success posture on a loan money-mover (bd babelstone-6cpq.14, mirrors the deposit suite).</summary>
    private static void AddFreshSca(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation(ScaPrecondition.AcrHeader, "urn:bank:sca:psd2");
        request.Headers.TryAddWithoutValidation(
            ScaPrecondition.AuthTimeHeader, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
    }

    /// <summary>Assert a 422 response carries the stable <c>SCA_REQUIRED</c> code (no PII).</summary>
    private static async Task AssertScaRequiredAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ScaPrecondition.RequiredCode, problem.GetProperty("code").GetString());
    }

    private async Task DisburseTwoInstallmentLoanAsync(Guid loanId)
    {
        var disburse = await _client.PostAsJsonAsync("/v1/loans", new
        {
            loan_id = loanId,
            principal_cents = 200_000L,
            product_id = "cp_pt_general_2m",
            role = "standard",
            term_months = 2,
            start_date = "2026-01-15",
            purpose = "general",
            disbursement_account_ref = "acct-ref-001",
        }, SnakeCase);
        Assert.Equal(HttpStatusCode.Created, disburse.StatusCode);
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

    private async Task<JsonElement> GetLoanAsync(Guid loanId)
    {
        var response = await _client.GetAsync($"/v1/loans/{loanId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(SnakeCase);
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
