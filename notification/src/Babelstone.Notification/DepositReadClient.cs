using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Babelstone.Notification;

/// <summary>
/// The notification service's READ window onto a deposit — over the family-agnostic, storage-opaque
/// read contract (ADR-IC-019 §D3 / ADR-PC-027), NOT the engine kernel. In plain terms: the maturity
/// scheduler this service will host needs to know, per deposit, when it matures and how much interest
/// has accrued and tax been withheld — and the engine already exposes all of that on one canonical
/// HTTP resource, <c>GET /v1/deposits/{id}</c>. This client is the thin, READ-ONLY adapter that calls
/// that resource and maps the JSON into <see cref="DepositView"/>. There is NO scheduling and NO
/// emission here (those are the downstream children, bd babelstone-60n8.2 / .3) — this skeleton just
/// proves the host can reach the read surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why HTTP, not the byte store.</b> ADR-IC-019 §D2/§P2 forbids the notification core from taking a
/// compile-time dependency on the engine kernel (<c>Babelstone.Engine</c> / <c>Babelstone.EventStore</c>)
/// or any product family. ADR-PC-027 slot 6 makes the deposit read surface <em>storage-opaque</em> — the
/// URL names the resource, not the storage — so the projection technology may change
/// (Postgres → Valkey/OpenSearch/DuckDB per ADR-IC-005's upgrade path) with zero contract change. A
/// consumer that bound the kernel's <c>IProjectionStorage</c> + a family's JSON codec (the shape the
/// skeleton first stood up) would couple to the exact storage tier the resource hides; reading the
/// published HTTP contract does not. The credential/secret boundary stays at the host composition root
/// (the engine endpoint is a service ENDPOINT, not a credential — ADR-PC-004 §P2).
/// </para>
/// <para>
/// <b>Why a local DTO.</b> <see cref="DepositView"/> is the notification core's OWN read model of the
/// wire contract — it deliberately does NOT reference the family's <c>DepositResponse</c> type (that
/// would re-introduce the families/** reference ADR-IC-019 §P2 forbids and the
/// <c>NOTIFICATION_FAMILY_AGNOSTIC</c> gate catches). It binds the JSON shape (snake_case, money as
/// integer cents — the host's <c>JsonNamingPolicy.SnakeCaseLower</c>), not the producer's CLR type,
/// exactly as the Python MCP server consumes the same resource. A deposit with no resource yet (a 404)
/// returns <see langword="null"/>.
/// </para>
/// </remarks>
public sealed class DepositReadClient
{
    /// <summary>Matches the engine API host's wire contract: snake_case property names
    /// (<c>JsonNamingPolicy.SnakeCaseLower</c>), money as integer cents.</summary>
    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    /// <summary>
    /// Composes the client over a typed <see cref="HttpClient"/> whose <c>BaseAddress</c> is the
    /// engine API endpoint, resolved at the host composition root (ADR-PC-027 / ADR-IC-019 §D3).
    /// </summary>
    public DepositReadClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <summary>
    /// The currently-believed <see cref="DepositView"/> for <paramref name="depositId"/> over
    /// <c>GET /v1/deposits/{id}</c>, or <see langword="null"/> if the engine has no such deposit
    /// resource yet (HTTP 404). Reads the current belief: a notification reflects the world as
    /// currently known, never a historical or counterfactual slice (an as-of read is a separate,
    /// later concern — ADR-PC-027 <c>?as_of_sequence</c>).
    /// </summary>
    public async Task<DepositView?> GetDepositAsync(Guid depositId, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"v1/deposits/{depositId}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DepositView>(WireJson, ct);
    }

    /// <summary>
    /// The maturity calendar slice — every deposit whose <c>maturity_date</c> falls in the half-open
    /// <c>[from, to)</c> window, ordered by maturity date — over the family-agnostic range-scan
    /// resource <c>GET /v1/deposits/maturities?from=&amp;to=</c> (the ADR-IC-005 <c>upcoming_maturities</c>
    /// projection; the maturity-calendar projection ADR-PC-023 §2 makes the temporal SIGNAL). This is
    /// the read the downstream maturity scheduler (ADR-PC-023 §6 — the engine owns no clock-driven
    /// emission) folds over to decide which deposits are entering their pre-maturity opt-out window.
    /// </summary>
    /// <remarks>
    /// Like <see cref="GetDepositAsync"/> this binds the host's snake_case wire JSON (money as integer
    /// cents) into the notification core's OWN <see cref="DepositMaturityView"/> — it does NOT reference
    /// the family's <c>DepositMaturitiesResponse</c>/<c>DepositResponse</c> CLR types (that would
    /// re-introduce the families/** reference ADR-IC-019 §P2 forbids and the
    /// <c>NOTIFICATION_FAMILY_AGNOSTIC</c> gate catches). The dates are passed in ISO-8601
    /// (<c>yyyy-MM-dd</c>), the same shape the host's <c>DateOnly</c> binder accepts. An empty or
    /// <c>from &gt;= to</c> window is a well-formed empty result, not an error (the engine returns
    /// <c>200 []</c>); a 5xx surfaces (it is not "nothing matures"), so the scheduler treats it as
    /// backpressure rather than silently skipping a cycle.
    /// </remarks>
    public async Task<IReadOnlyList<DepositMaturityView>> ListMaturitiesAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var fromIso = from.ToString("yyyy-MM-dd");
        var toIso = to.ToString("yyyy-MM-dd");

        using var response = await _http.GetAsync($"v1/deposits/maturities?from={fromIso}&to={toIso}", ct);
        response.EnsureSuccessStatusCode();

        var page = await response.Content.ReadFromJsonAsync<MaturitiesPage>(WireJson, ct);
        return page?.Deposits ?? [];
    }

    /// <summary>The notification core's local read model of the maturities collection
    /// (<c>{ "deposits": [ … ] }</c>) — mirrors the wire JSON, not the family's
    /// <c>DepositMaturitiesResponse</c> CLR type.</summary>
    private sealed record MaturitiesPage(IReadOnlyList<DepositMaturityView> Deposits);
}

/// <summary>
/// The notification core's local read model of the deposit resource (ADR-PC-027) — the subset of
/// <c>GET /v1/deposits/{id}</c> the notification service needs: the maturity date and the
/// accrued-interest / withholding / payout rollups that drive a maturity notice. Money is integer
/// cents (ADR-PC-010 §P1), never a float. It mirrors the wire JSON, NOT the family's
/// <c>DepositResponse</c> CLR type — so the notification core names no family type and the
/// <c>NOTIFICATION_FAMILY_AGNOSTIC</c> gate stays green (ADR-IC-019 §D2/§D3).
/// </summary>
/// <param name="DepositId">The deposit's stream id.</param>
/// <param name="Lifecycle">The deposit's lifecycle state (e.g. <c>Active</c>, <c>Matured</c>).</param>
/// <param name="MaturityDate">The scheduled maturity date — the driver of the maturity scheduler.</param>
/// <param name="AccruedGrossInterestCents">Gross interest accrued to date, in cents.</param>
/// <param name="WithholdingToDateCents">Tax withheld to date, in cents.</param>
/// <param name="NetInterestCents">Net interest to date (gross − withholding), in cents.</param>
/// <param name="TotalPayoutCents">Total payout (principal + net interest) at maturity, in cents.</param>
/// <param name="CouponsPaid">Number of PERIODIC coupons paid so far.</param>
/// <param name="LastSequence">The per-stream version this view reflects (ADR-IC-005 §P3).</param>
/// <param name="LastUpdated">The producing event's transaction time (for honest staleness display).</param>
public sealed record DepositView(
    Guid DepositId,
    string Lifecycle,
    DateOnly MaturityDate,
    long AccruedGrossInterestCents,
    long WithholdingToDateCents,
    long NetInterestCents,
    long TotalPayoutCents,
    int CouponsPaid,
    long LastSequence,
    DateTimeOffset LastUpdated);

/// <summary>
/// The notification core's local read model of ONE row in the maturity calendar
/// (<c>GET /v1/deposits/maturities</c>) — the subset of the deposit resource the maturity scheduler
/// needs to decide a reminder is due and to render it: which deposit, when it matures, and the payout
/// rollups a maturity notice interpolates. Like <see cref="DepositView"/> it binds the snake_case wire
/// JSON (money as integer cents — ADR-PC-010 §P1), NOT the family's <c>DepositResponse</c> CLR type, so
/// the notification core names no family type and the <c>NOTIFICATION_FAMILY_AGNOSTIC</c> gate stays
/// green (ADR-IC-019 §D2/§D3).
/// </summary>
/// <param name="DepositId">The deposit's stream id — the <c>instance_id</c> in the ADR-PC-025
/// composite notification key.</param>
/// <param name="Lifecycle">The deposit's lifecycle state (e.g. <c>Active</c>, <c>Matured</c>).</param>
/// <param name="MaturityDate">The scheduled maturity date — the driver of the 14-day pre-maturity window.</param>
/// <param name="TotalPayoutCents">Total payout (principal + net interest) at maturity, in cents.</param>
/// <param name="NetInterestCents">Net interest to date (gross − withholding), in cents.</param>
public sealed record DepositMaturityView(
    Guid DepositId,
    string Lifecycle,
    DateOnly MaturityDate,
    long TotalPayoutCents,
    long NetInterestCents);
