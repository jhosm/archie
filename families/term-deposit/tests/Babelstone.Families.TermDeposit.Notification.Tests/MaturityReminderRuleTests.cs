using System.Net;
using System.Text;
using System.Text.Json;
using Babelstone.Notification;
using Xunit;

namespace Babelstone.Families.TermDeposit.Notification.Tests;

/// <summary>
/// Tests for <see cref="MaturityReminderRule"/> — the term-deposit family's notification contribution
/// (ADR-IC-019 §D1 + Amendment 2026-06-24 / ADR-PC-023 §6 / ADR-PC-025). They cover the family-shaped half
/// of the bd babelstone-60n8.2 acceptance criteria — the part ADR-IC-019 §D1 keeps OUT of the core:
/// <list type="bullet">
/// <item>a pass reads the maturity calendar as-of a date and selects deposits entering the pre-maturity
/// opt-out window (02 §2.4.4);</item>
/// <item>the window boundary (in on day N, out on day N+1) and the Active-only filter;</item>
/// <item>each decision carries the <c>pt.notice.maturity</c> template + the structural interpolation values
/// (no PII — ADR-PC-025 PII rule).</item>
/// </list>
/// The composite-id derivation and the "re-runs don't re-notify" dedupe are CORE concerns
/// (<c>NotificationSchedulePass</c>), tested in Babelstone.Notification.Tests — a family rule never
/// reimplements idempotency. Docker-free and engine-free: the rule reads the calendar over a fake
/// <see cref="HttpMessageHandler"/> driving a real <see cref="DepositReadClient"/>.
/// </summary>
public sealed class MaturityReminderRuleTests
{
    private const int WindowDays = TermDepositNotificationModule.DefaultOptOutWindowDays;
    private static readonly DateOnly Today = new(2026, 6, 24);

    [Fact]
    public async Task A_pass_selects_active_deposits_returned_for_the_opt_out_window()
    {
        var d1 = Guid.NewGuid();
        var d2 = Guid.NewGuid();
        DateOnly? capturedFrom = null;
        DateOnly? capturedTo = null;

        var handler = new RecordingHandler((from, to) =>
        {
            capturedFrom = from;
            capturedTo = to;
            return Maturities(
                Row(d1, "Active", Today.AddDays(3)),
                Row(d2, "Active", Today.AddDays(13)));
        });
        var rule = NewRule(handler);

        var decisions = await rule.EvaluateAsync(Today);

        // The window the rule asks the engine for is the half-open [today, today + N + 1), which catches
        // every maturity up to AND INCLUDING today + N — the engine's opt-out gate opens the window at
        // maturity_date − N, so today + N is the first day the opt-out right exists (02 §2.4.4).
        Assert.Equal(Today, capturedFrom);
        Assert.Equal(Today.AddDays(WindowDays + 1), capturedTo);

        Assert.Equal(2, decisions.Count);
        Assert.Contains(decisions, x => x.InstanceId == d1);
        Assert.Contains(decisions, x => x.InstanceId == d2);
        Assert.All(decisions, x => Assert.Equal(MaturityReminderRule.MaturityTemplateRef, x.TemplateRef));
        Assert.All(decisions, x => Assert.Equal(Today, x.DueAt));
        // The structural interpolation values ride the decision — no PII (ADR-PC-025 PII rule).
        Assert.All(decisions, x => Assert.Equal(1_006_480L, x.Amounts["total_payout_cents"]));
        Assert.All(decisions, x => Assert.Equal(6_480L, x.Amounts["net_interest_cents"]));
        // The occurrence key is the deposit's maturity date (the composite-id occurrence, ADR-PC-025 slot 4).
        Assert.Contains(decisions, x => x.InstanceId == d1 && x.OccurrenceKey == Today.AddDays(3));
    }

    [Fact]
    public async Task The_window_includes_maturity_on_day_N_and_excludes_day_N_plus_one()
    {
        // Boundary: the opt-out window opens at maturity_date − N, so a deposit maturing exactly N days out
        // is IN-window; one N+1 days out is NOT. The fake engine HONOURS the requested half-open window, so
        // the scan boundary is what decides — exactly as the real range-scan resource would.
        var dayN = Guid.NewGuid();
        var dayNPlusOne = Guid.NewGuid();
        var all = new[]
        {
            (dayN, "Active", Today.AddDays(WindowDays)),
            (dayNPlusOne, "Active", Today.AddDays(WindowDays + 1)),
        };
        var handler = new RecordingHandler((from, to) => Maturities(
            all.Where(r => r.Item3 >= from && r.Item3 < to)
               .Select(r => Row(r.Item1, r.Item2, r.Item3)).ToArray()));
        var rule = NewRule(handler);

        var decisions = await rule.EvaluateAsync(Today);

        Assert.Single(decisions);
        Assert.Equal(dayN, decisions[0].InstanceId);
    }

    [Fact]
    public async Task A_non_active_deposit_in_the_window_is_not_a_reminder_target()
    {
        // A deposit already Matured / Renewed has no live opt-out window — it must not be reminded.
        var active = Guid.NewGuid();
        var matured = Guid.NewGuid();
        var handler = new RecordingHandler((_, _) => Maturities(
            Row(active, "Active", Today.AddDays(5)),
            Row(matured, "Matured", Today.AddDays(6))));
        var rule = NewRule(handler);

        var decisions = await rule.EvaluateAsync(Today);

        Assert.Single(decisions);
        Assert.Equal(active, decisions[0].InstanceId);
    }

    [Fact]
    public void The_rule_rejects_a_non_positive_window()
    {
        var client = new DepositReadClient(new HttpClient(new RecordingHandler((_, _) => Maturities()))
        {
            BaseAddress = new Uri("http://engine.test/"),
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => new MaturityReminderRule(client, 0));
    }

    // --- helpers ---

    private static MaturityReminderRule NewRule(RecordingHandler handler)
    {
        var client = new DepositReadClient(new HttpClient(handler) { BaseAddress = new Uri("http://engine.test/") });
        return new MaturityReminderRule(client, WindowDays);
    }

    private static (Guid Id, string Lifecycle, DateOnly Maturity) Row(Guid id, string lifecycle, DateOnly maturity) =>
        (id, lifecycle, maturity);

    /// <summary>Builds the snake_case maturities wire JSON the host emits, with only the fields the
    /// notification core binds populated meaningfully — the rest are present but structurally inert.</summary>
    private static HttpResponseMessage Maturities(params (Guid Id, string Lifecycle, DateOnly Maturity)[] rows)
    {
        var deposits = rows.Select(r => new
        {
            deposit_id = r.Id,
            sor = "engine",
            principal_cents = 1_000_000L,
            maturity_date = r.Maturity.ToString("yyyy-MM-dd"),
            interest_variant = "AT_MATURITY",
            accrued_gross_interest_cents = 9_000L,
            withholding_to_date_cents = 2_520L,
            net_interest_cents = 6_480L,
            total_payout_cents = 1_006_480L,
            coupons_paid = 0,
            lifecycle = r.Lifecycle,
            last_sequence = 4L,
            last_updated = "2026-06-21T09:00:00+00:00",
        });

        var body = JsonSerializer.Serialize(new { deposits });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>A fake <see cref="HttpMessageHandler"/> that parses the <c>from</c>/<c>to</c> query
    /// params and hands them to a responder — enough to assert the requested window with no network.</summary>
    private sealed class RecordingHandler(Func<DateOnly, DateOnly, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = ParseQuery(request.RequestUri!.Query);
            var from = DateOnly.ParseExact(query["from"], "yyyy-MM-dd");
            var to = DateOnly.ParseExact(query["to"], "yyyy-MM-dd");
            return Task.FromResult(responder(from, to));
        }

        private static Dictionary<string, string> ParseQuery(string query) =>
            query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
    }
}
