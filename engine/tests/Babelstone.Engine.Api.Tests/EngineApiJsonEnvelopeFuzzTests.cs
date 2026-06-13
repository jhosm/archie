using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.Families.TermDeposit;
using Babelstone.Families.TermDeposit.Application;
using Babelstone.Packs;
using Babelstone.RateSheets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Babelstone.Engine.Api.Tests;

/// <summary>
/// In plain English: this test throws hundreds of broken request bodies at every Engine.Api endpoint
/// that takes JSON and checks the host always answers with a polite "your request was bad" (a 4xx),
/// never a server crash (a 500), and never hangs. A malformed payload is the caller's fault, so it
/// must be a 400-class error — a 500 would mean the boundary leaked an unhandled exception, and that
/// is exactly the under-tested error path Engine.Api's low branch coverage flags.
///
/// <para>
/// Formally: a LIGHT, bounded, deterministic property-fuzz of the ADR-PC-021 §D5 command boundary
/// (<see cref="DepositsEndpoints"/>). It is a UNIT-lane test (ADR-IC-009 unit/integration tiering) —
/// it boots NO PostgreSQL container and is NOT the scheduled Go fuzz lane (<c>fuzz.yml</c>). Instead
/// it composes a minimal in-memory <see cref="TestServer"/> that maps the REAL endpoints with the
/// REAL host JSON options (snake_case, case-sensitive), the REAL <c>AddProblemDetails()</c> +
/// <c>UseExceptionHandler()</c> error pipeline, and the REAL deciders/runtime — only the leaf I/O
/// ports (event store, rate-sheet store, read model, settlement) are in-memory fakes. The fakes are
/// shaped so that any well-formed-but-invalid body that survives model binding still lands on a domain
/// rejection (a 422), never an infrastructure 500: an empty event stream folds to
/// <see cref="DepositLifecycle.Pending"/> (so maturity/interest are illegal transitions), and the
/// rate-sheet resolve returns null (so constitution is an unpriced rejection). That isolates exactly
/// the model-binding + error branches that are Engine.Api's weak coverage spot.
/// </para>
///
/// <para>
/// The mutation corpus is generated from a FIXED seed (<see cref="Seed"/>) so the run is fully
/// deterministic — the engine values replay-determinism, and a flaky fuzz test is unacceptable. The
/// corpus is small and the per-request timeout is short, so the whole suite stays well inside the
/// per-PR unit budget.
/// </para>
/// </summary>
public sealed class EngineApiJsonEnvelopeFuzzTests : IAsyncLifetime
{
    /// <summary>Fixed seed: the corpus must be identical on every run (no flakiness, ADR-IC-009 unit tier).</summary>
    private const int Seed = unchecked((int)0x_BABE_5709);

    /// <summary>Per-request ceiling: a malformed body must complete fast (never hang). Generous for a loaded CI box.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // A minimal host that maps the REAL DepositsEndpoints over an in-memory TestServer — no
        // Kestrel socket, no PostgreSQL, no pack OCI loader. The host JSON contract and the error
        // pipeline are reproduced verbatim from Program.cs so the boundary under test behaves exactly
        // as production does for a bad body; only the leaf I/O ports are faked.
        _host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();

                    // The SAME wire contract as Program.cs: snake_case, case-sensitive. Case-sensitivity
                    // matters here — a type-swapped or wrong-cased field must not be silently coerced.
                    services.ConfigureHttpJsonOptions(options =>
                    {
                        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
                        options.SerializerOptions.PropertyNameCaseInsensitive = false;
                    });

                    // The SAME typed-ProblemDetails error pipeline as Program.cs: an unhandled
                    // exception becomes a 500 ProblemDetails (which this test asserts NEVER happens),
                    // and a model-binding failure becomes a 400.
                    services.AddProblemDetails();

                    // The family-agnostic spine ports, faked in-memory. The runtime is the REAL
                    // AggregateRuntime<DepositPosition> (a sealed class — composed, not mocked) over an
                    // empty event store, so every LoadAsync folds DepositPosition.Empty (Version -1).
                    services.AddSingleton(TimeProvider.System);
                    services.AddSingleton<IEventStore, EmptyEventStore>();
                    services.AddSingleton<IEventSink, RejectingEventSink>();
                    services.AddSingleton<IEventSerializer, JsonEventSerializer>();
                    services.AddSingleton<IPiiProtector, NullPiiProtector>();
                    services.AddSingleton<IRateSheetStore, UnpricedRateSheetStore>();
                    services.AddSingleton<ISettlementPort, NoopSettlementPort>();
                    services.AddSingleton<IDepositReadModelStore, EmptyDepositReadModelStore>();

                    services.AddSingleton(serviceProvider => new AggregateRuntime<DepositPosition>(
                        serviceProvider.GetRequiredService<IEventStore>(),
                        serviceProvider.GetRequiredService<IEventSink>(),
                        TermDepositFamilyModule.Registry(),
                        serviceProvider.GetRequiredService<IEventSerializer>(),
                        serviceProvider.GetRequiredService<IPiiProtector>(),
                        serviceProvider.GetRequiredService<TimeProvider>(),
                        () => DepositPosition.Empty));

                    // The REAL decider. Its constructor needs a VerifiedPack, but no path this test
                    // reaches dereferences it: constitution rejects on the null rate-sheet resolve
                    // BEFORE the pack is read, and maturity/interest reject on the Pending-state
                    // lifecycle guard BEFORE ResolvePrimitives(). The on-disk pt.2026.1 pack the
                    // integration tests use is loaded once here purely to satisfy the ctor.
                    var pack = HostPack.Load(PacksDir(), "pt.2026.1");
                    services.AddSingleton(serviceProvider => new TermDepositConstitutionService(
                        serviceProvider.GetRequiredService<AggregateRuntime<DepositPosition>>(),
                        serviceProvider.GetRequiredService<IRateSheetStore>(),
                        serviceProvider.GetRequiredService<ISettlementPort>(),
                        pack,
                        dayCountPrimitive: "act_360",
                        withholdingPrimitive: "irs_juros"));
                })
                .Configure(app =>
                {
                    app.UseExceptionHandler();
                    app.UseRouting();
                    app.UseEndpoints(DepositsEndpoints.Map);
                }))
            .StartAsync();

        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    /// <summary>
    /// Every JSON-bodied POST endpoint, against every mutated body, must answer 4xx (never 5xx) and
    /// must complete inside <see cref="RequestTimeout"/> (never hang). The id route argument is a fixed
    /// valid GUID so the body — not the route — is what is being fuzzed.
    /// </summary>
    [Theory]
    [MemberData(nameof(JsonBodyEndpoints))]
    public async Task A_malformed_json_body_is_always_a_4xx_never_a_500_and_never_hangs(string route, string seedJson)
    {
        foreach (var body in MutationCorpus(seedJson))
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var perRequest = new CancellationTokenSource(RequestTimeout);

            HttpResponseMessage response;
            try
            {
                response = await _client.PostAsync(route, content, perRequest.Token);
            }
            catch (OperationCanceledException) when (perRequest.IsCancellationRequested)
            {
                Assert.Fail($"POST {route} hung past {RequestTimeout.TotalSeconds:0}s on body: {Truncate(body)}");
                throw; // unreachable; satisfies definite-assignment of `response`
            }

            // The contract: a bad body is the CALLER's fault (a 4xx — a 400 from model binding, or a
            // 422 from a domain rejection for a well-formed-but-invalid body). A 5xx would mean an
            // unhandled exception leaked through the boundary — the failure this fuzz exists to catch.
            var status = (int)response.StatusCode;
            Assert.True(
                status >= 400 && status < 500,
                $"POST {route} returned {status} (expected 4xx) on body: {Truncate(body)}");
            Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

            response.Dispose();
        }
    }

    /// <summary>
    /// A malformed body that DOES happen to parse and survive binding must still be a domain rejection
    /// (422), never an infrastructure 500 — the constitution path's unpriced-rate-sheet branch. This
    /// pins the "well-formed JSON, wrong domain" half of the corpus to its expected 4xx, distinct from
    /// the "unparseable JSON → 400" half the binder rejects.
    /// </summary>
    [Fact]
    public async Task A_well_formed_but_unpriced_constitution_body_is_a_422_domain_rejection()
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["principal_cents"] = 1_000_000,
            ["product_id"] = "anything_unpriced",
            ["role"] = "standard",
            ["term_days"] = 365,
            ["start_date"] = "2026-01-15",
            ["interest_variant"] = "AT_MATURITY",
            ["auto_renewal_policy"] = "NONE",
            ["funding_account"] = "PT50-DDA-001",
        });

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/v1/deposits", content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    /// <summary>The JSON-bodied endpoints under fuzz, each paired with a VALID seed body the mutator perturbs.</summary>
    public static TheoryData<string, string> JsonBodyEndpoints()
    {
        var id = "11111111-2222-3333-4444-555555555555";
        return new TheoryData<string, string>
        {
            // POST /v1/deposits — the constitution command (all fields populated).
            {
                "/v1/deposits",
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["principal_cents"] = 1_000_000,
                    ["product_id"] = "dpz_pt_12m_juros_venc",
                    ["role"] = "standard",
                    ["term_days"] = 365,
                    ["start_date"] = "2026-01-15",
                    ["interest_variant"] = "AT_MATURITY",
                    ["auto_renewal_policy"] = "NONE",
                    ["funding_account"] = "PT50-DDA-001",
                    ["payment_period_months"] = 0,
                })
            },
            // POST /v1/deposits/{id}/maturity — every field optional (host-stamped).
            {
                $"/v1/deposits/{id}/maturity",
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["matured_at"] = "2027-01-15T00:00:00+00:00",
                    ["payout_account"] = "PT50-DDA-001",
                    ["actor"] = "mcp:dev",
                })
            },
            // POST /v1/deposits/{id}/interest — every field optional (host-stamped).
            {
                $"/v1/deposits/{id}/interest",
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["paid_at"] = "2026-04-15T00:00:00+00:00",
                    ["payout_account"] = "PT50-DDA-001",
                    ["actor"] = "mcp:dev",
                })
            },
        };
    }

    /// <summary>
    /// A small, bounded, deterministic set of mutated/malformed bodies derived from a valid seed —
    /// the classic JSON-envelope abuse cases the brief enumerates: truncated JSON, type-swapped
    /// fields, missing required fields, extra unknown fields, wrong value domains, empty/oversized
    /// strings, and bad encodings. Seeded RNG keeps the corpus identical across runs.
    /// </summary>
    private static IEnumerable<string> MutationCorpus(string seedJson)
    {
        var random = new Random(Seed);
        var document = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(seedJson)!;
        var keys = new List<string>(document.Keys);

        // --- Structurally malformed / unparseable (the binder must 400) ---
        yield return string.Empty;                                  // empty body
        yield return "   ";                                         // whitespace only
        yield return "{";                                           // unterminated object
        yield return seedJson[..(seedJson.Length / 2)];             // truncated mid-document
        yield return seedJson + seedJson;                           // two documents concatenated
        yield return "[" + seedJson + "]";                         // a JSON array where an object is expected
        yield return "\"" + seedJson.Replace("\"", "\\\"") + "\""; // a JSON string where an object is expected
        yield return "42";                                         // a bare number
        yield return "true";                                       // a bare boolean
        yield return "null";                                       // a literal null body
        yield return "{ \"a\": }";                                 // missing value
        yield return "{ , }";                                       // stray comma
        yield return "{ \"a\": 00123 }";                           // illegal leading zeros
        yield return "{ \"a\": NaN }";                             // non-JSON NaN literal
        yield return "{ \"a\": 'single' }";                        // single-quoted string (invalid JSON)
        yield return "{ \"\\uD800\": 1 }";                         // lone high surrogate (bad encoding)
        yield return "﻿" + seedJson;                          // a leading BOM
        yield return "{ \"deposit_id\": \"not-a-guid\" }";         // wrong value domain for a Guid field

        // --- Well-formed JSON, semantically abused (each may bind to a 400 or fold to a 422) ---

        // Type-swap every field in turn: string→number, number→string, scalar→array, scalar→object.
        foreach (var key in keys)
        {
            yield return Rewrite(document, key, JsonSerializer.SerializeToElement(random.Next(0, 1000)));
            yield return Rewrite(document, key, JsonSerializer.SerializeToElement("not-the-right-type"));
            yield return Rewrite(document, key, JsonSerializer.SerializeToElement(new[] { 1, 2, 3 }));
            yield return Rewrite(document, key, JsonSerializer.SerializeToElement(new { nested = true }));
            yield return Rewrite(document, key, JsonSerializer.SerializeToElement((string?)null));
        }

        // Drop each field in turn (missing required fields).
        foreach (var key in keys)
        {
            var withoutKey = new Dictionary<string, JsonElement>(document);
            withoutKey.Remove(key);
            yield return JsonSerializer.Serialize(withoutKey);
        }

        // Empty object: every field missing at once.
        yield return "{}";

        // Extra unknown fields piled on top of the valid seed.
        var withExtras = new Dictionary<string, JsonElement>(document)
        {
            ["__unknown_a"] = JsonSerializer.SerializeToElement("x"),
            ["__unknown_b"] = JsonSerializer.SerializeToElement(random.Next()),
            ["principal_cents_typo"] = JsonSerializer.SerializeToElement(1),
        };
        yield return JsonSerializer.Serialize(withExtras);

        // Wrong value domains on the numeric/string fields, where present.
        yield return RewriteIfPresent(document, "principal_cents", JsonSerializer.SerializeToElement(-1));
        yield return RewriteIfPresent(document, "principal_cents", JsonSerializer.SerializeToElement(long.MaxValue));
        yield return RewriteIfPresent(document, "principal_cents",
            JsonSerializer.SerializeToElement("9999999999999999999999999999")); // overflows long
        yield return RewriteIfPresent(document, "term_days", JsonSerializer.SerializeToElement(-365));
        yield return RewriteIfPresent(document, "term_days", JsonSerializer.SerializeToElement(2147483648L)); // > int.MaxValue
        yield return RewriteIfPresent(document, "start_date", JsonSerializer.SerializeToElement("not-a-date"));
        yield return RewriteIfPresent(document, "start_date", JsonSerializer.SerializeToElement("2026-13-45"));
        yield return RewriteIfPresent(document, "matured_at", JsonSerializer.SerializeToElement("not-a-timestamp"));
        yield return RewriteIfPresent(document, "paid_at", JsonSerializer.SerializeToElement("13:99"));

        // Empty and oversized strings on the string fields, where present.
        foreach (var key in keys)
        {
            if (document[key].ValueKind == JsonValueKind.String)
            {
                yield return Rewrite(document, key, JsonSerializer.SerializeToElement(string.Empty));
                yield return Rewrite(document, key, JsonSerializer.SerializeToElement(new string('A', 100_000)));
                // A control character / bad-encoding-ish payload inside an otherwise valid string.
                yield return Rewrite(document, key, JsonSerializer.SerializeToElement(" �"));
            }
        }
    }

    private static string Rewrite(Dictionary<string, JsonElement> document, string key, JsonElement value)
    {
        var copy = new Dictionary<string, JsonElement>(document) { [key] = value };
        return JsonSerializer.Serialize(copy);
    }

    private static string RewriteIfPresent(Dictionary<string, JsonElement> document, string key, JsonElement value) =>
        document.ContainsKey(key) ? Rewrite(document, key, value) : JsonSerializer.Serialize(document);

    private static string Truncate(string body) =>
        body.Length <= 200 ? body : body[..200] + $"...(+{body.Length - 200} chars)";

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

    // --- In-memory leaf-port fakes. They never throw an infrastructure exception, so any 5xx the
    //     test sees is a genuine unhandled fault in the boundary code under fuzz, not a fake misbehaving. ---

    /// <summary>An event store with no streams: every LoadAsync folds to DepositPosition.Empty (Version -1).</summary>
    private sealed class EmptyEventStore : IEventStore
    {
        public Task AppendAsync(
            Guid streamId, long expectedVersion, IReadOnlyList<EventEnvelope> events,
            IReadOnlyList<OutboxRow> outboxRows, CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<EventEnvelope> LoadAsync(
            Guid streamId, long fromSequence = 0,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyList<Guid>> ReadStreamIdsAsync(string family, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    /// <summary>No command in this fuzz reaches an append (each rejects first); a call here would be a test bug.</summary>
    private sealed class RejectingEventSink : IEventSink
    {
        public Task AppendAsync(
            Guid streamId, long expectedVersion, IReadOnlyList<EventEnvelope> events,
            IReadOnlyList<OutboxRow> outboxRows, CancellationToken ct = default) =>
            throw new InvalidOperationException(
                "No fuzz body should reach an event append — every command rejects on a domain guard first.");
    }

    /// <summary>Resolves no rate sheet: constitution always fails loud as an unpriced domain rejection (422).</summary>
    private sealed class UnpricedRateSheetStore : IRateSheetStore
    {
        public Task InsertAsync(RateSheet sheet, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RateSheet?> TryGetAsync(string rateSheetVersionId, CancellationToken ct = default) =>
            Task.FromResult<RateSheet?>(null);

        public Task<RateSheetResolution?> ResolveAsync(string productFamily, DateTimeOffset asOf, CancellationToken ct = default) =>
            Task.FromResult<RateSheetResolution?>(null);
    }

    /// <summary>A settlement port that always succeeds — money legs are out of scope for an envelope fuzz.</summary>
    private sealed class NoopSettlementPort : ISettlementPort
    {
        public Task SettleAsync(SettlementInstruction instruction, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>An empty read model: GET point lookups miss (→ fold → 404) and range scans are empty.</summary>
    private sealed class EmptyDepositReadModelStore : IDepositReadModelStore
    {
        public Task UpsertAsync(DepositReadModelRow row, CancellationToken ct = default) => Task.CompletedTask;

        public Task<DepositReadModelRow?> GetAsync(Guid streamId, CancellationToken ct = default) =>
            Task.FromResult<DepositReadModelRow?>(null);

        public Task TruncateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<DepositReadModelRow>> ListByMaturityAsync(
            DateOnly fromInclusive, DateOnly toExclusive, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DepositReadModelRow>>([]);
    }
}
