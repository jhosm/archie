using System.Net;
using System.Text;
using System.Text.Json;
using Babelstone.Notification;
using Xunit;

namespace Babelstone.Families.TermDeposit.Notification.Tests;

/// <summary>
/// Tests for <see cref="WithholdingStatementRule"/> — the term-deposit family's annual IRS-withholding
/// statement contribution (ADR-IC-019 §D1 + Amendment 2026-06-24 / ADR-PC-023 §6 / ADR-PC-025), now
/// slicing PER TAX YEAR off the dated withholding ledger (bd babelstone-60n8.8). They cover the
/// family-shaped half of the acceptance criteria — the part ADR-IC-019 §D1 keeps OUT of the core:
/// <list type="bullet">
/// <item>a pass reads the withholding population, then each deposit's DATED ledger, and emits one SCHEDULED
/// statement per deposit that withheld tax in the prior tax year — carrying the PER-YEAR slice, not the
/// cumulative-to-date figure;</item>
/// <item>the statement is keyed on the prior tax year (the annual occurrence the composite id dedupes on);</item>
/// <item>a deposit whose withholding fell in a DIFFERENT year gets no statement for this one;</item>
/// <item>an Erased deposit (no render-time PII) is not a statement target;</item>
/// <item>each decision carries the <c>pt.notice.withholding_statement</c> template + the structural per-year
/// cents figures (no PII — ADR-PC-025 PII rule).</item>
/// </list>
/// The composite-id derivation and the "re-runs don't re-notify" dedupe are CORE concerns
/// (<c>NotificationSchedulePass</c>), tested in Babelstone.Notification.Tests — a family rule never
/// reimplements idempotency. Docker-free and engine-free: the rule reads the population AND the per-stream
/// ledgers over a fake <see cref="HttpMessageHandler"/> driving a real <see cref="DepositReadClient"/>.
/// </summary>
public sealed class WithholdingStatementRuleTests
{
    // asOf 2026-02-15 → the prior tax year is 2025, boundary 2025-12-31.
    private static readonly DateOnly Today = new(2026, 2, 15);
    private const int TaxYear = 2025;

    [Fact]
    public async Task A_pass_emits_a_scheduled_per_tax_year_statement_per_deposit_keyed_on_the_prior_tax_year()
    {
        var d1 = Guid.NewGuid();
        var d2 = Guid.NewGuid();

        var handler = new RoutingHandler(
            population:
            [
                Row(d1, "Active"),
                Row(d2, "Matured"),
            ],
            ledgers: new Dictionary<Guid, LedgerFlow[]>
            {
                // d1: two 2025 flows that sum to gross 9_000 / tax 2_520 / net 6_480.
                [d1] =
                [
                    Flow(new DateOnly(2025, 4, 1), gross: 4_000, tax: 1_120, net: 2_880, "coupon"),
                    Flow(new DateOnly(2025, 12, 31), gross: 5_000, tax: 1_400, net: 3_600, "withholding"),
                ],
                [d2] =
                [
                    Flow(new DateOnly(2025, 7, 1), gross: 5_000, tax: 1_400, net: 3_600, "withholding"),
                ],
            });
        var rule = NewRule(handler);

        var decisions = await rule.EvaluateAsync(Today);

        // The rule reads the family-agnostic withholding-statements collection first.
        Assert.Contains("/v1/deposits/withholding-statements", handler.RequestedPaths);
        // ...then each non-erased deposit's per-stream DATED ledger (the per-tax-year slicing read).
        Assert.Contains($"/v1/deposits/{d1}/withholding-ledger", handler.RequestedPaths);
        Assert.Contains($"/v1/deposits/{d2}/withholding-ledger", handler.RequestedPaths);

        Assert.Equal(2, decisions.Count);
        Assert.All(decisions, x =>
            Assert.Equal(WithholdingStatementRule.WithholdingStatementTemplateRef, x.TemplateRef));
        Assert.All(decisions, x => Assert.Equal(Today, x.DueAt));
        // The occurrence is the PRIOR tax-year boundary — the annual key the composite id dedupes on, so a
        // re-run within the same calendar year does not re-notify (ADR-PC-025 slot 4).
        Assert.All(decisions, x => Assert.Equal(new DateOnly(TaxYear, 12, 31), x.OccurrenceKey));

        // d1 carries the SUM of its 2025 flows (the per-year slice), not a cumulative-to-date figure.
        var first = decisions.Single(x => x.InstanceId == d1);
        Assert.Equal(9_000L, first.Amounts["accrued_gross_interest_cents"]);
        Assert.Equal(2_520L, first.Amounts["withholding_to_date_cents"]);
        Assert.Equal(6_480L, first.Amounts["net_interest_cents"]);
        Assert.Contains(decisions, x => x.InstanceId == d2);
    }

    [Fact]
    public async Task The_slice_sums_only_the_target_tax_year_flows()
    {
        // A ledger with flows in BOTH 2024 and 2025 must report ONLY the 2025 slice — the cumulative v1
        // figure (which summed every flow) would over-report.
        var d1 = Guid.NewGuid();
        var handler = new RoutingHandler(
            population: [Row(d1, "Active")],
            ledgers: new Dictionary<Guid, LedgerFlow[]>
            {
                [d1] =
                [
                    Flow(new DateOnly(2024, 6, 1), gross: 100_000, tax: 28_000, net: 72_000, "coupon"),
                    Flow(new DateOnly(2025, 6, 1), gross: 5_000, tax: 1_400, net: 3_600, "coupon"),
                ],
            });
        var rule = NewRule(handler);

        var decisions = await rule.EvaluateAsync(Today);

        var only = Assert.Single(decisions);
        Assert.Equal(5_000L, only.Amounts["accrued_gross_interest_cents"]);
        Assert.Equal(1_400L, only.Amounts["withholding_to_date_cents"]);
        Assert.Equal(3_600L, only.Amounts["net_interest_cents"]);
    }

    [Fact]
    public async Task A_deposit_with_no_withholding_in_the_tax_year_gets_no_statement()
    {
        // Its only withholding fell in a DIFFERENT year (2024) — no statement is due for tax year 2025.
        var d1 = Guid.NewGuid();
        var handler = new RoutingHandler(
            population: [Row(d1, "Active")],
            ledgers: new Dictionary<Guid, LedgerFlow[]>
            {
                [d1] = [Flow(new DateOnly(2024, 6, 1), gross: 100_000, tax: 28_000, net: 72_000, "coupon")],
            });
        var rule = NewRule(handler);

        var decisions = await rule.EvaluateAsync(Today);

        Assert.Empty(decisions);
    }

    [Fact]
    public async Task An_erased_deposit_is_not_a_statement_target()
    {
        // A crypto-shredded (Erased) deposit cannot be rendered — its render-time PII reference is gone — so
        // it must not be sent a statement, even though its withholding facts remain. It is filtered BEFORE
        // its ledger is read, so no ledger request is made for it.
        var live = Guid.NewGuid();
        var erased = Guid.NewGuid();
        var handler = new RoutingHandler(
            population:
            [
                Row(live, "Active"),
                Row(erased, "Erased"),
            ],
            ledgers: new Dictionary<Guid, LedgerFlow[]>
            {
                [live] = [Flow(new DateOnly(2025, 6, 1), gross: 9_000, tax: 2_520, net: 6_480, "withholding")],
                [erased] = [Flow(new DateOnly(2025, 6, 1), gross: 9_000, tax: 2_520, net: 6_480, "withholding")],
            });
        var rule = NewRule(handler);

        var decisions = await rule.EvaluateAsync(Today);

        Assert.Single(decisions);
        Assert.Equal(live, decisions[0].InstanceId);
        // The erased deposit's ledger is never read (filtered first).
        Assert.DoesNotContain($"/v1/deposits/{erased}/withholding-ledger", handler.RequestedPaths);
    }

    [Fact]
    public async Task An_empty_population_yields_no_statements()
    {
        var handler = new RoutingHandler(population: [], ledgers: new Dictionary<Guid, LedgerFlow[]>());
        var rule = NewRule(handler);

        var decisions = await rule.EvaluateAsync(Today);

        Assert.Empty(decisions);
    }

    [Fact]
    public async Task A_deposit_whose_ledger_is_not_yet_materialised_404s_to_an_empty_slice()
    {
        // The withholding_ledger projection is async-materialised; a deposit in the population whose ledger
        // resource is not yet present (404) yields an empty slice, so no statement — never a crash.
        var d1 = Guid.NewGuid();
        var handler = new RoutingHandler(
            population: [Row(d1, "Active")],
            ledgers: new Dictionary<Guid, LedgerFlow[]>()); // no ledger for d1 → 404
        var rule = NewRule(handler);

        var decisions = await rule.EvaluateAsync(Today);

        Assert.Empty(decisions);
    }

    // --- helpers ---

    private static WithholdingStatementRule NewRule(RoutingHandler handler)
    {
        var client = new DepositReadClient(new HttpClient(handler) { BaseAddress = new Uri("http://engine.test/") });
        return new WithholdingStatementRule(client);
    }

    private static (Guid Id, string Lifecycle) Row(Guid id, string lifecycle) => (id, lifecycle);

    private static LedgerFlow Flow(DateOnly withheldOn, long gross, long tax, long net, string source) =>
        new(withheldOn, gross, tax, net, source);

    private sealed record LedgerFlow(DateOnly WithheldOn, long Gross, long Tax, long Net, string Source);

    /// <summary>A fake <see cref="HttpMessageHandler"/> that routes the withholding-statements population
    /// read and the per-stream withholding-ledger reads to the right canned wire JSON, recording every
    /// requested path — enough to assert the read sequence with no network.</summary>
    private sealed class RoutingHandler(
        (Guid Id, string Lifecycle)[] population,
        IReadOnlyDictionary<Guid, LedgerFlow[]> ledgers) : HttpMessageHandler
    {
        public List<string> RequestedPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            RequestedPaths.Add(path);

            if (path == "/v1/deposits/withholding-statements")
            {
                return Task.FromResult(Json(PopulationBody(population)));
            }

            // /v1/deposits/{guid}/withholding-ledger
            if (path.EndsWith("/withholding-ledger", StringComparison.Ordinal))
            {
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var id = Guid.Parse(segments[^2]);
                if (!ledgers.TryGetValue(id, out var flows))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                return Task.FromResult(Json(LedgerBody(id, flows)));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        /// <summary>The snake_case withholding-statements population wire JSON the host emits — only the
        /// fields the rule binds (deposit_id, lifecycle) populated meaningfully.</summary>
        private static string PopulationBody((Guid Id, string Lifecycle)[] rows)
        {
            var deposits = rows.Select(r => new
            {
                deposit_id = r.Id,
                sor = "engine",
                principal_cents = 1_000_000L,
                maturity_date = "2026-07-01",
                interest_variant = "AT_MATURITY",
                accrued_gross_interest_cents = 0L,
                withholding_to_date_cents = 0L,
                net_interest_cents = 0L,
                total_payout_cents = 1_000_000L,
                coupons_paid = 0,
                lifecycle = r.Lifecycle,
                last_sequence = 4L,
                last_updated = "2026-02-10T09:00:00+00:00",
            });
            return JsonSerializer.Serialize(new { deposits });
        }

        /// <summary>The snake_case per-stream withholding-ledger wire JSON the host emits — the DATED
        /// entries the rule slices by tax year.</summary>
        private static string LedgerBody(Guid id, LedgerFlow[] flows)
        {
            var entries = flows.Select(f => new
            {
                withheld_on = f.WithheldOn.ToString("yyyy-MM-dd"),
                gross_cents = f.Gross,
                tax_cents = f.Tax,
                net_cents = f.Net,
                source = f.Source,
            });
            return JsonSerializer.Serialize(new
            {
                deposit_id = id,
                entries,
                total_gross_cents = flows.Sum(f => f.Gross),
                total_tax_cents = flows.Sum(f => f.Tax),
                total_net_cents = flows.Sum(f => f.Net),
            });
        }
    }
}
