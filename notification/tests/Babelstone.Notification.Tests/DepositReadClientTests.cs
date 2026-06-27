using System.Net;
using System.Text;
using Babelstone.Notification;
using Xunit;

namespace Babelstone.Notification.Tests;

/// <summary>
/// Pure unit tests for <see cref="DepositReadClient"/> — the notification service's read window onto
/// a deposit over the family-agnostic, storage-opaque read contract (ADR-PC-027 / ADR-IC-019 §D3).
/// Docker-free and engine-free: they drive the client over a fake <see cref="HttpMessageHandler"/>
/// (no real engine, no network), asserting that the client (1) calls <c>GET /v1/deposits/{id}</c>,
/// (2) maps the host's snake_case wire JSON into the core-local <see cref="DepositView"/> (money as
/// integer cents — never the family's <c>DepositResponse</c> CLR type, which the core does not
/// reference), and (3) returns <see langword="null"/> on a 404 (no deposit resource yet). The
/// end-to-end read against a real engine is the engine API's own integration tests' job.
/// </summary>
public sealed class DepositReadClientTests
{
    private const string WireJson = """
        {
          "deposit_id": "11111111-1111-1111-1111-111111111111",
          "sor": "engine",
          "principal_cents": 1000000,
          "tan_basis_points": 320,
          "rate_sheet_version_id": "rs-2026-1",
          "product_code": "TD-STD",
          "term_days": 365,
          "start_date": "2026-03-15",
          "maturity_date": "2027-03-15",
          "interest_variant": "AT_MATURITY",
          "auto_renewal_policy": "NONE",
          "payment_period_months": 0,
          "accrued_gross_interest_cents": 1234,
          "withholding_to_date_cents": 345,
          "net_interest_cents": 889,
          "total_payout_cents": 1000889,
          "coupons_paid": 0,
          "lifecycle": "Active",
          "last_sequence": 7,
          "last_updated": "2026-06-15T14:23:00+00:00"
        }
        """;

    [Fact]
    public async Task GetDeposit_calls_the_canonical_resource_and_maps_the_snake_case_wire_shape()
    {
        var depositId = new Guid("11111111-1111-1111-1111-111111111111");
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, WireJson));
        var client = NewClient(handler);

        var view = await client.GetDepositAsync(depositId);

        // The client reads the ONE canonical resource, relative to the configured base address.
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal($"http://engine.test/v1/deposits/{depositId}", handler.LastRequest.RequestUri!.ToString());

        // snake_case → the core-local DepositView (money as integer cents, dates as DateOnly).
        Assert.NotNull(view);
        Assert.Equal(depositId, view.DepositId);
        Assert.Equal("Active", view.Lifecycle);
        Assert.Equal(new DateOnly(2027, 3, 15), view.MaturityDate);
        Assert.Equal(1234, view.AccruedGrossInterestCents);
        Assert.Equal(345, view.WithholdingToDateCents);
        Assert.Equal(889, view.NetInterestCents);
        Assert.Equal(1_000_889, view.TotalPayoutCents);
        Assert.Equal(0, view.CouponsPaid);
        Assert.Equal(7, view.LastSequence);
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 14, 23, 0, TimeSpan.Zero), view.LastUpdated);
    }

    [Fact]
    public async Task GetDeposit_returns_null_when_the_deposit_resource_does_not_exist()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = NewClient(handler);

        var view = await client.GetDepositAsync(Guid.NewGuid());

        Assert.Null(view);
    }

    [Fact]
    public async Task GetDeposit_throws_on_a_server_error_rather_than_silently_swallowing_it()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = NewClient(handler);

        // A 5xx is not a "no deposit" answer — surfacing it (not returning null) keeps a broken read
        // surface from being mistaken for "nothing to notify on".
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetDepositAsync(Guid.NewGuid()));
    }

    // --- the maturity-calendar range scan (bd babelstone-60n8.2) ---

    private const string MaturitiesWireJson = """
        {
          "deposits": [
            {
              "deposit_id": "22222222-2222-2222-2222-222222222222",
              "sor": "engine",
              "principal_cents": 500000,
              "tan_basis_points": 280,
              "rate_sheet_version_id": "rs-2026-1",
              "product_code": "TD-STD",
              "term_days": 365,
              "start_date": "2025-07-01",
              "maturity_date": "2026-07-01",
              "interest_variant": "AT_MATURITY",
              "auto_renewal_policy": "AUTO",
              "payment_period_months": 0,
              "accrued_gross_interest_cents": 5000,
              "withholding_to_date_cents": 1400,
              "net_interest_cents": 3600,
              "total_payout_cents": 503600,
              "coupons_paid": 0,
              "lifecycle": "Active",
              "last_sequence": 3,
              "last_updated": "2026-06-20T09:00:00+00:00"
            },
            {
              "deposit_id": "33333333-3333-3333-3333-333333333333",
              "sor": "engine",
              "principal_cents": 1000000,
              "tan_basis_points": 320,
              "rate_sheet_version_id": "rs-2026-1",
              "product_code": "TD-STD",
              "term_days": 365,
              "start_date": "2025-07-05",
              "maturity_date": "2026-07-05",
              "interest_variant": "AT_MATURITY",
              "auto_renewal_policy": "NONE",
              "payment_period_months": 0,
              "accrued_gross_interest_cents": 9000,
              "withholding_to_date_cents": 2520,
              "net_interest_cents": 6480,
              "total_payout_cents": 1006480,
              "coupons_paid": 0,
              "lifecycle": "Active",
              "last_sequence": 4,
              "last_updated": "2026-06-21T09:00:00+00:00"
            }
          ]
        }
        """;

    [Fact]
    public async Task ListMaturities_calls_the_range_scan_with_iso_dates_and_maps_each_row()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, MaturitiesWireJson));
        var client = NewClient(handler);

        var rows = await client.ListMaturitiesAsync(new DateOnly(2026, 6, 24), new DateOnly(2026, 7, 8));

        // The half-open [from, to) window is passed as ISO-8601 yyyy-MM-dd query params on the
        // family-agnostic range-scan resource (ADR-IC-005 upcoming_maturities / ADR-PC-027).
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal(
            "http://engine.test/v1/deposits/maturities?from=2026-06-24&to=2026-07-08",
            handler.LastRequest.RequestUri!.ToString());

        // snake_case → the core-local DepositMaturityView (money as integer cents, dates as DateOnly).
        Assert.Equal(2, rows.Count);
        Assert.Equal(new Guid("22222222-2222-2222-2222-222222222222"), rows[0].DepositId);
        Assert.Equal("Active", rows[0].Lifecycle);
        Assert.Equal(new DateOnly(2026, 7, 1), rows[0].MaturityDate);
        Assert.Equal(503_600, rows[0].TotalPayoutCents);
        Assert.Equal(3_600, rows[0].NetInterestCents);
        Assert.Equal(new Guid("33333333-3333-3333-3333-333333333333"), rows[1].DepositId);
        Assert.Equal(new DateOnly(2026, 7, 5), rows[1].MaturityDate);
    }

    [Fact]
    public async Task ListMaturities_returns_empty_when_nothing_matures_in_the_window()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{ "deposits": [] }"""));
        var client = NewClient(handler);

        var rows = await client.ListMaturitiesAsync(new DateOnly(2026, 6, 24), new DateOnly(2026, 7, 8));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task ListMaturities_throws_on_a_server_error_so_a_broken_read_is_not_mistaken_for_empty()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = NewClient(handler);

        // A 5xx is NOT "nothing matures" — surfacing it lets the scheduler treat it as backpressure
        // (back off + retry) rather than silently skipping a cycle (and missing a reminder).
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.ListMaturitiesAsync(new DateOnly(2026, 6, 24), new DateOnly(2026, 7, 8)));
    }

    // --- the withholding-statements collection (bd babelstone-q15c) ---

    private const string WithholdingStatementsWireJson = """
        {
          "deposits": [
            {
              "deposit_id": "44444444-4444-4444-4444-444444444444",
              "sor": "engine",
              "principal_cents": 1000000,
              "maturity_date": "2026-07-01",
              "interest_variant": "AT_MATURITY",
              "accrued_gross_interest_cents": 9000,
              "withholding_to_date_cents": 2520,
              "net_interest_cents": 6480,
              "total_payout_cents": 1006480,
              "coupons_paid": 0,
              "lifecycle": "Active",
              "last_sequence": 4,
              "last_updated": "2026-02-10T09:00:00+00:00"
            }
          ]
        }
        """;

    [Fact]
    public async Task ListWithholdingStatements_calls_the_collection_and_maps_the_withholding_rollups()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, WithholdingStatementsWireJson));
        var client = NewClient(handler);

        var rows = await client.ListWithholdingStatementsAsync();

        // The family-agnostic collection resource — no window, no as-of (the scheduler owns those).
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal(
            "http://engine.test/v1/deposits/withholding-statements",
            handler.LastRequest.RequestUri!.ToString());

        // snake_case → the core-local DepositWithholdingView (money as integer cents).
        Assert.Single(rows);
        Assert.Equal(new Guid("44444444-4444-4444-4444-444444444444"), rows[0].DepositId);
        Assert.Equal("Active", rows[0].Lifecycle);
        Assert.Equal(9_000, rows[0].AccruedGrossInterestCents);
        Assert.Equal(2_520, rows[0].WithholdingToDateCents);
        Assert.Equal(6_480, rows[0].NetInterestCents);
    }

    [Fact]
    public async Task ListWithholdingStatements_returns_empty_when_nobody_had_withholding()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{ "deposits": [] }"""));
        var client = NewClient(handler);

        var rows = await client.ListWithholdingStatementsAsync();

        Assert.Empty(rows);
    }

    [Fact]
    public async Task ListWithholdingStatements_throws_on_a_server_error_rather_than_returning_empty()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = NewClient(handler);

        // A 5xx is NOT "nobody had withholding" — surfacing it lets the scheduler back off rather than
        // silently skip an annual run.
        await Assert.ThrowsAsync<HttpRequestException>(() => client.ListWithholdingStatementsAsync());
    }

    // --- helpers ---

    private static DepositReadClient NewClient(FakeHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://engine.test/") });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A canned <see cref="HttpMessageHandler"/> that records the last request and returns
    /// whatever the supplied responder produces — enough to assert the client's path + JSON mapping
    /// with no network.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(responder(request));
        }
    }
}
