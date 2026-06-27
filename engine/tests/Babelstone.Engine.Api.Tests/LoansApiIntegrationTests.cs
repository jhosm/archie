using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Babelstone.EventStore.Migrations;
using Babelstone.RateSheets;
using Babelstone.TestFixtures;
using Microsoft.AspNetCore.Mvc.Testing;
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

        // 2. Pay the first installment (mandatory Idempotency-Key). The loan stays ACTIVE.
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
    public async Task Paying_an_installment_without_an_idempotency_key_is_rejected_400()
    {
        var loanId = Guid.NewGuid();
        await _client.PostAsJsonAsync("/v1/loans", new
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

        // No Idempotency-Key header — the money-mover contract (ADR-PC-029 slot 4) fails loud at 400.
        var response = await _client.PostAsJsonAsync(
            $"/v1/loans/{loanId}/installment", new { collection_account_ref = "acct-ref-001" }, SnakeCase);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_an_unknown_loan_is_404()
    {
        var response = await _client.GetAsync($"/v1/loans/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<HttpResponseMessage> PayInstallmentAsync(Guid loanId, string collectionAccountRef)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/loans/{loanId}/installment")
        {
            Content = JsonContent.Create(new { collection_account_ref = collectionAccountRef }, options: SnakeCase),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await _client.SendAsync(request);
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
