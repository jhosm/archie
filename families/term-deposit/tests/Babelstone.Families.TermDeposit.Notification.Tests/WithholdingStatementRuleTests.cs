using System.Net;
using System.Text;
using System.Text.Json;
using Babelstone.Notification;
using Xunit;

namespace Babelstone.Families.TermDeposit.Notification.Tests;

/// <summary>
/// Tests for <see cref="WithholdingStatementRule"/> — the term-deposit family's annual IRS-withholding
/// statement contribution (ADR-IC-019 §D1 + Amendment 2026-06-24 / ADR-PC-023 §6 / ADR-PC-025). They cover
/// the family-shaped half of the bd babelstone-q15c acceptance criteria — the part ADR-IC-019 §D1 keeps OUT
/// of the core:
/// <list type="bullet">
/// <item>a pass reads the withholding population and emits one SCHEDULED statement per deposit;</item>
/// <item>the statement is keyed on the prior tax year (the annual occurrence the composite id dedupes on);</item>
/// <item>an Erased deposit (no render-time PII) is not a statement target;</item>
/// <item>each decision carries the <c>pt.notice.withholding_statement</c> template + the structural
/// accrual/withholding cents figures (no PII — ADR-PC-025 PII rule).</item>
/// </list>
/// The composite-id derivation and the "re-runs don't re-notify" dedupe are CORE concerns
/// (<c>NotificationSchedulePass</c>), tested in Babelstone.Notification.Tests — a family rule never
/// reimplements idempotency. Docker-free and engine-free: the rule reads the population over a fake
/// <see cref="HttpMessageHandler"/> driving a real <see cref="DepositReadClient"/>.
/// </summary>
public sealed class WithholdingStatementRuleTests
{
    private static readonly DateOnly Today = new(2026, 2, 15);

    [Fact]
    public async Task A_pass_emits_a_scheduled_statement_per_deposit_keyed_on_the_prior_tax_year()
    {
        var d1 = Guid.NewGuid();
        var d2 = Guid.NewGuid();
        string? capturedPath = null;

        var handler = new RecordingHandler(path =>
        {
            capturedPath = path;
            return Population(
                Row(d1, "Active", accruedGross: 9_000, withholding: 2_520, net: 6_480),
                Row(d2, "Matured", accruedGross: 5_000, withholding: 1_400, net: 3_600));
        });
        var rule = NewRule(handler);

        var decisions = await rule.EvaluateAsync(Today);

        // The rule reads the family-agnostic withholding-statements collection (no window / no as-of —
        // the scheduler owns the as-of date and the annual cadence, ADR-PC-023 §6).
        Assert.Equal("/v1/deposits/withholding-statements", capturedPath);

        Assert.Equal(2, decisions.Count);
        Assert.All(decisions, x =>
            Assert.Equal(WithholdingStatementRule.WithholdingStatementTemplateRef, x.TemplateRef));
        Assert.All(decisions, x => Assert.Equal(Today, x.DueAt));
        // The occurrence is the PRIOR tax-year boundary — the annual key the composite id dedupes on, so a
        // re-run within the same calendar year does not re-notify (ADR-PC-025 slot 4).
        Assert.All(decisions, x => Assert.Equal(new DateOnly(2025, 12, 31), x.OccurrenceKey));

        var first = decisions.Single(x => x.InstanceId == d1);
        Assert.Equal(9_000L, first.Amounts["accrued_gross_interest_cents"]);
        Assert.Equal(2_520L, first.Amounts["withholding_to_date_cents"]);
        Assert.Equal(6_480L, first.Amounts["net_interest_cents"]);
        Assert.Contains(decisions, x => x.InstanceId == d2);
    }

    [Fact]
    public async Task An_erased_deposit_is_not_a_statement_target()
    {
        // A crypto-shredded (Erased) deposit cannot be rendered — its render-time PII reference is gone — so
        // it must not be sent a statement, even though its withholding facts remain on the read model.
        var live = Guid.NewGuid();
        var erased = Guid.NewGuid();
        var handler = new RecordingHandler(_ => Population(
            Row(live, "Active", accruedGross: 9_000, withholding: 2_520, net: 6_480),
            Row(erased, "Erased", accruedGross: 9_000, withholding: 2_520, net: 6_480)));
        var rule = NewRule(handler);

        var decisions = await rule.EvaluateAsync(Today);

        Assert.Single(decisions);
        Assert.Equal(live, decisions[0].InstanceId);
    }

    [Fact]
    public async Task An_empty_population_yields_no_statements()
    {
        var handler = new RecordingHandler(_ => Population());
        var rule = NewRule(handler);

        var decisions = await rule.EvaluateAsync(Today);

        Assert.Empty(decisions);
    }

    // --- helpers ---

    private static WithholdingStatementRule NewRule(RecordingHandler handler)
    {
        var client = new DepositReadClient(new HttpClient(handler) { BaseAddress = new Uri("http://engine.test/") });
        return new WithholdingStatementRule(client);
    }

    private static (Guid Id, string Lifecycle, long AccruedGross, long Withholding, long Net) Row(
        Guid id, string lifecycle, long accruedGross, long withholding, long net) =>
        (id, lifecycle, accruedGross, withholding, net);

    /// <summary>Builds the snake_case withholding-statements wire JSON the host emits, with only the fields
    /// the notification core binds populated meaningfully — the rest are present but structurally inert.</summary>
    private static HttpResponseMessage Population(
        params (Guid Id, string Lifecycle, long AccruedGross, long Withholding, long Net)[] rows)
    {
        var deposits = rows.Select(r => new
        {
            deposit_id = r.Id,
            sor = "engine",
            principal_cents = 1_000_000L,
            maturity_date = "2026-07-01",
            interest_variant = "AT_MATURITY",
            accrued_gross_interest_cents = r.AccruedGross,
            withholding_to_date_cents = r.Withholding,
            net_interest_cents = r.Net,
            total_payout_cents = 1_000_000L + r.Net,
            coupons_paid = 0,
            lifecycle = r.Lifecycle,
            last_sequence = 4L,
            last_updated = "2026-02-10T09:00:00+00:00",
        });

        var body = JsonSerializer.Serialize(new { deposits });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>A fake <see cref="HttpMessageHandler"/> that records the requested path and hands it to a
    /// responder — enough to assert the requested resource with no network.</summary>
    private sealed class RecordingHandler(Func<string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request.RequestUri!.AbsolutePath));
    }
}
