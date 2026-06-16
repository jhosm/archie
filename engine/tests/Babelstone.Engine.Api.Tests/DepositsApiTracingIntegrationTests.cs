using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Babelstone.Engine.Api;
using Babelstone.EventStore.Migrations;
using Babelstone.RateSheets;
using Babelstone.TestFixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Engine.Api.Tests;

/// <summary>
/// OBS_TRACE_ID_SURFACED_HTTP (OBS-5, ADR-IC-007 §P1 Layer 1 / §P4):
/// the engine-side trace join (bd babelstone-2dex; ADR-IC-007 Layer 1) over the real
/// Babelstone.Engine.Api host: a POST carrying a W3C <c>traceparent</c> must produce
/// <c>deposit.*</c> spans nested under THAT trace id (not new roots), and every response must hand
/// the active trace id back to the caller on the <see cref="TraceResponseHeader.Name"/> header.
///
/// Spans are observed in-process with an <see cref="ActivityListener"/> — the same OTel
/// <c>System.Diagnostics.Activity</c> surface the host's tracing pipeline records, with no
/// dependency on a live OTLP collector. The Tempo round-trip (returned id resolves in Grafana) is
/// the dev-stack verification leg, asserted by hand, not in this lane.
/// </summary>
[Trait("Category", "Integration")]
[Collection(EngineApiHostCollection.Name)]
public sealed class DepositsApiTracingIntegrationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions SnakeCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    // The W3C Trace Context spec's own example trace/span ids — a valid, non-zero traceparent.
    private const string InboundTraceId = "0af7651916cd43dd8448eb211c80319c";
    private const string InboundTraceparent = $"00-{InboundTraceId}-b7ad6b7169203331-01";

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _pg.GatedStartAsync();
        await new MigrationRunner(_pg.GetConnectionString()).ApplyAsync();
        await new Babelstone.Families.TermDeposit.Application.Migrations.MigrationRunner(
            _pg.GetConnectionString()).ApplyAsync();

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
    public async Task An_inbound_traceparent_becomes_the_parent_of_the_deposit_spans_and_is_returned_to_the_caller()
    {
        // Observe every Activity the host starts in-process (the OTel Activity surface).
        var spans = new ConcurrentBag<Activity>();
        using var listener = CaptureAllSpans(spans);

        // POST /v1/deposits carrying a W3C traceparent (ADR-IC-007 Layer 1 — traceparent is the join).
        var response = await PostConstituteAsync(InboundTraceparent);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // (a) the deposit.constituted span nests under the INBOUND trace id — it is NOT a new root.
        var constituted = spans.FirstOrDefault(a => a.OperationName == "deposit.constituted");
        Assert.NotNull(constituted);
        Assert.Equal(InboundTraceId, constituted.TraceId.ToHexString());
        // It also has a parent (the inbound request's SERVER span), proving it is not a root.
        Assert.NotEqual(default, constituted.ParentSpanId);

        // (b) the response carries the trace-id header, equal to that same (the request's current) trace id.
        Assert.True(response.Headers.TryGetValues(TraceResponseHeader.Name, out var values));
        Assert.Equal(InboundTraceId, Assert.Single(values));
    }

    [Fact]
    public async Task With_no_inbound_traceparent_the_request_still_forms_one_trace_and_returns_an_opaque_id()
    {
        var spans = new ConcurrentBag<Activity>();
        using var listener = CaptureAllSpans(spans);

        var response = await PostConstituteAsync(traceparent: null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // The server span started a fresh trace and the deposit.constituted span nests under it —
        // one connected trace either way (not a root).
        var constituted = spans.FirstOrDefault(a => a.OperationName == "deposit.constituted");
        Assert.NotNull(constituted);
        Assert.NotEqual(default, constituted.ParentSpanId);

        // The returned id is the SAME trace and is an opaque 32-hex identifier — never PII
        // (ADR-IC-007 §P4 / ADR-PC-004 §P2).
        Assert.True(response.Headers.TryGetValues(TraceResponseHeader.Name, out var values));
        var returned = Assert.Single(values);
        Assert.Equal(constituted.TraceId.ToHexString(), returned);
        Assert.Equal(32, returned.Length);
        Assert.All(returned, c => Assert.True(Uri.IsHexDigit(c)));
    }

    [Fact]
    public async Task A_query_response_also_carries_the_trace_id_header()
    {
        // The header is set by middleware for EVERY response, not just commands: a plain GET carries it.
        var response = await _client.GetAsync($"/v1/deposits/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Headers.TryGetValues(TraceResponseHeader.Name, out var values));
        Assert.Equal(32, Assert.Single(values).Length);
    }

    private static ActivityListener CaptureAllSpans(ConcurrentBag<Activity> sink)
    {
        var listener = new ActivityListener
        {
            // Listen only to the engine + ASP.NET Core server sources — NOT System.Net.Http.
            // Enabling the HttpClient source would make the test's outbound request mint its own
            // activity and OVERWRITE the manual traceparent header we send, defeating the test.
            ShouldListenTo = source => source.Name is "Babelstone.Engine" or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = sink.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private Task<HttpResponseMessage> PostConstituteAsync(string? traceparent)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/deposits")
        {
            Content = JsonContent.Create(new ConstituteDepositRequest(
                PrincipalCents: 1_000_000,
                ProductId: "dpz_pt_12m_juros_venc",
                Role: "standard",
                TermDays: 365,
                StartDate: new DateOnly(2026, 1, 15),
                InterestVariant: "AT_MATURITY",
                AutoRenewalPolicy: "NONE",
                FundingAccount: "PT50-DDA-001"), options: SnakeCase),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        if (traceparent is not null)
        {
            request.Headers.TryAddWithoutValidation("traceparent", traceparent);
        }

        return _client.SendAsync(request);
    }

    private static RateSheetBody FlatPriced(string productId, string role, int tanBasisPoints) => new()
    {
        Products = new Dictionary<string, Dictionary<string, RoleRates>>
        {
            [productId] = new()
            {
                [role] = new RoleRates { Bands = [new RateBand(0L, null, tanBasisPoints)] },
            },
        },
    };

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
