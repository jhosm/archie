using System.Net;
using System.Text;
using System.Text.Json;
using Babelstone.Notification;
using Xunit;

namespace Babelstone.Notification.Tests;

/// <summary>
/// Tests for <see cref="MaturityScheduler"/> — the downstream, clock-owning maturity scheduler
/// (ADR-PC-023 §6 / ADR-PC-025). They cover the bd babelstone-60n8.2 acceptance criteria directly:
/// <list type="bullet">
/// <item>a pass reads the maturity calendar as-of a date and selects deposits entering the 14-day
/// pre-maturity opt-out window (02 §2.4.4);</item>
/// <item>running the pass TWICE over the same calendar state produces NO duplicate notification —
/// dedupe on the composite <c>notification_id</c> (ADR-PC-025 slot 4);</item>
/// <item>the as-of date is an INPUT (no clock read inside the pass) — the determinism the engine's
/// fold has, kept here too even though the gate does not constrain this component.</item>
/// </list>
/// Docker-free and engine-free: the scheduler reads the calendar over a fake
/// <see cref="HttpMessageHandler"/> driving a real <see cref="DepositReadClient"/> — no real engine,
/// no network. The dedupe ledger is the real <see cref="InMemoryNotificationDedupeLedger"/>.
/// </summary>
public sealed class MaturitySchedulerTests
{
    private static readonly DateOnly Today = new(2026, 6, 24);

    [Fact]
    public async Task A_pass_selects_active_deposits_returned_for_the_14_day_window()
    {
        // The fake engine returns two Active deposits maturing inside the window. The scheduler's job
        // is to turn the calendar slice into "reminder due" decisions for the Active ones.
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
        var scheduler = NewScheduler(handler, out _);

        var decisions = await scheduler.RunOnceAsync(Today);

        // The window the scheduler asked the engine for is the half-open [today, today + 15), which
        // catches every maturity up to AND INCLUDING today + 14 — the engine's opt-out gate opens the
        // window at maturity_date − 14, so today + 14 is the first day the opt-out right exists and
        // must be in scope (02 §2.4.4 / TermDepositConstitutionService §3a).
        Assert.Equal(Today, capturedFrom);
        Assert.Equal(Today.AddDays(15), capturedTo);

        // Both Active deposits become decisions, each carrying the maturity template + the structural
        // interpolation values (no PII — ADR-PC-025 PII rule).
        Assert.Equal(2, decisions.Count);
        Assert.Contains(decisions, x => x.InstanceId == d1);
        Assert.Contains(decisions, x => x.InstanceId == d2);
        Assert.All(decisions, x => Assert.Equal(MaturityScheduler.MaturityTemplateRef, x.TemplateRef));
        Assert.All(decisions, x => Assert.Equal(Today, x.DueAt));
    }

    [Fact]
    public async Task The_window_includes_maturity_on_day_14_and_excludes_day_15()
    {
        // Boundary: the opt-out window opens at maturity_date − 14, so a deposit maturing exactly
        // 14 days out is IN-window (the first day its opt-out right exists); one 15 days out is NOT.
        // The fake engine HONOURS the requested half-open [from, to) window so the scan boundary is
        // what decides, exactly as the real range-scan resource would.
        var dayFourteen = Guid.NewGuid();
        var dayFifteen = Guid.NewGuid();
        var all = new[]
        {
            (dayFourteen, "Active", Today.AddDays(14)),
            (dayFifteen, "Active", Today.AddDays(15)),
        };
        var handler = new RecordingHandler((from, to) => Maturities(
            all.Where(r => r.Item3 >= from && r.Item3 < to)
               .Select(r => Row(r.Item1, r.Item2, r.Item3)).ToArray()));
        var scheduler = NewScheduler(handler, out _);

        var decisions = await scheduler.RunOnceAsync(Today);

        Assert.Single(decisions);
        Assert.Equal(dayFourteen, decisions[0].InstanceId);
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
        var scheduler = NewScheduler(handler, out _);

        var decisions = await scheduler.RunOnceAsync(Today);

        Assert.Single(decisions);
        Assert.Equal(active, decisions[0].InstanceId);
    }

    [Fact]
    public async Task Running_the_pass_twice_over_the_same_calendar_produces_no_duplicate_notification()
    {
        // THE core acceptance criterion (ADR-PC-025 slot 4): a second pass over the SAME calendar state
        // re-derives the SAME notification_id for the same deposit, so the dedupe ledger absorbs it and
        // raises nothing. No double-notify across re-runs or projection refreshes.
        var deposit = Guid.NewGuid();
        var maturity = Today.AddDays(7);
        var handler = new RecordingHandler((_, _) => Maturities(Row(deposit, "Active", maturity)));
        var scheduler = NewScheduler(handler, out _);

        var first = await scheduler.RunOnceAsync(Today);
        var second = await scheduler.RunOnceAsync(Today);

        // First pass raises exactly one; the identical second pass raises NONE.
        Assert.Single(first);
        Assert.Empty(second);

        // The id the first pass minted is exactly the deterministic composite key (slot 4).
        var expected = MaturityScheduler.ComputeNotificationId(
            deposit, MaturityScheduler.MaturityTemplateRef, maturity);
        Assert.Equal(expected, first[0].NotificationId);
    }

    [Fact]
    public async Task A_genuinely_new_deposit_on_a_later_pass_is_still_raised()
    {
        // Dedupe must not be a blunt "already ran" flag: a deposit that enters the window on a LATER
        // pass (a new instance, or one whose maturity just crossed into the 14-day band) is new and
        // must be raised, even though an earlier pass already raised a different deposit.
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var calendar = new List<(Guid Id, string Lifecycle, DateOnly Maturity)>
        {
            (first, "Active", Today.AddDays(2)),
        };
        var handler = new RecordingHandler((_, _) => Maturities(
            calendar.Select(c => Row(c.Id, c.Lifecycle, c.Maturity)).ToArray()));
        var scheduler = NewScheduler(handler, out _);

        var pass1 = await scheduler.RunOnceAsync(Today);

        // A second deposit now enters the window.
        calendar.Add((second, "Active", Today.AddDays(4)));
        var pass2 = await scheduler.RunOnceAsync(Today);

        Assert.Single(pass1);
        Assert.Equal(first, pass1[0].InstanceId);
        Assert.Single(pass2);
        Assert.Equal(second, pass2[0].InstanceId);
    }

    [Fact]
    public void ComputeNotificationId_is_deterministic_and_distinguishes_the_three_composite_parts()
    {
        var instance = Guid.NewGuid();
        var other = Guid.NewGuid();
        var maturity = new DateOnly(2026, 7, 1);

        var id = MaturityScheduler.ComputeNotificationId(instance, MaturityScheduler.MaturityTemplateRef, maturity);

        // Stable: the same three inputs always yield the same id (replay-stable — slot 4).
        Assert.Equal(id, MaturityScheduler.ComputeNotificationId(instance, MaturityScheduler.MaturityTemplateRef, maturity));

        // Each of the three parts is load-bearing: changing any one changes the id.
        Assert.NotEqual(id, MaturityScheduler.ComputeNotificationId(other, MaturityScheduler.MaturityTemplateRef, maturity));
        Assert.NotEqual(id, MaturityScheduler.ComputeNotificationId(instance, "pt.notice.other", maturity));
        Assert.NotEqual(id, MaturityScheduler.ComputeNotificationId(instance, MaturityScheduler.MaturityTemplateRef, maturity.AddDays(1)));

        // It is a well-formed RFC-4122 v5 GUID (name-based, deterministic) — never the zero GUID.
        // The version nibble is the high nibble of the time_hi_and_version field; in the
        // System.Guid.ToByteArray() layout (mixed-endian Data3) that is byte index 6.
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(5, (id.ToByteArray()[6] >> 4) & 0x0F);
    }

    // --- helpers ---

    private static MaturityScheduler NewScheduler(RecordingHandler handler, out InMemoryNotificationDedupeLedger ledger)
    {
        var client = new DepositReadClient(new HttpClient(handler) { BaseAddress = new Uri("http://engine.test/") });
        ledger = new InMemoryNotificationDedupeLedger();
        return new MaturityScheduler(client, ledger);
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
            tan_basis_points = 320,
            rate_sheet_version_id = "rs-2026-1",
            product_code = "TD-STD",
            term_days = 365,
            start_date = r.Maturity.AddDays(-365).ToString("yyyy-MM-dd"),
            maturity_date = r.Maturity.ToString("yyyy-MM-dd"),
            interest_variant = "AT_MATURITY",
            auto_renewal_policy = "AUTO",
            payment_period_months = 0,
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
