using System.Net;
using System.Text.Json;
using Babelstone.Engine.Hosting;
using Babelstone.Lifecycle;
using Xunit;

namespace Babelstone.Lifecycle.Tests;

/// <summary>
/// The end-to-end smoke test for the command-POST sink (ADR-PC-036 §Decision 2 acceptance criterion: "a smoke
/// test POSTs a command end-to-end through the sink"; bd babelstone-6cpq.7). It drives the real
/// <see cref="HttpLifecycleCommandSink"/> against a fake <see cref="HttpMessageHandler"/> (no live engine, no
/// Docker) and asserts the wire request the engine's ADR-PC-029 command surface would receive:
/// <list type="bullet">
/// <item>a POST to the engine base URL + the decision's command path;</item>
/// <item>the canonical, server-derived, number-pinned id (LCD-1) carried as the <c>Idempotency-Key</c> header
/// — so a retry replays the original outcome at <c>command_dedup</c> rather than moving money twice;</item>
/// <item>the body as the engine's snake_case wire shape;</item>
/// <item>the scoped, non-interactive SCA principal on a money-mover route, and ONLY when the decision carries
/// one (ADR-PC-036 §Decision 1);</item>
/// <item>a non-success engine response throws — backpressure the pass turns into "don't record, retry next
/// pass".</item>
/// </list>
/// </summary>
public sealed class HttpLifecycleCommandSinkTests
{
    private const string PayInstallment = "pay_installment";
    private static readonly Uri EngineBase = new("http://engine.local/");

    [Fact]
    public async Task It_posts_the_installment_command_with_the_server_derived_idempotency_key()
    {
        var loan = Guid.NewGuid();
        var commandId = LifecycleCommandKey.Derive(loan, PayInstallment, 3);
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var sink = new HttpLifecycleCommandSink(new HttpClient(handler) { BaseAddress = EngineBase });

        var decision = new LifecycleCommandDecision(
            InstanceId: loan,
            CommandKind: PayInstallment,
            OccurrenceKey: 3,
            RequestPath: $"/v1/loans/{loan:D}/installment",
            Body: new Dictionary<string, object?> { ["collection_account_ref"] = "acct-ref-001" },
            DueAt: new DateOnly(2026, 9, 1));

        await sink.DispatchAsync(decision, commandId);

        var sent = handler.Captured;
        Assert.NotNull(sent);
        Assert.Equal(HttpMethod.Post, sent!.Method);
        Assert.Equal($"http://engine.local/v1/loans/{loan:D}/installment", sent.RequestUri!.ToString());

        // The number-pinned, server-derived key rides as the Idempotency-Key (LCD-1, ADR-PC-029 slot 4).
        Assert.Equal(commandId.ToString("D"), Assert.Single(sent.Headers.GetValues(HttpLifecycleCommandSink.IdempotencyHeader)));

        // The body is the engine's snake_case wire shape (the field names the API binds).
        using var body = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal("acct-ref-001", body.RootElement.GetProperty("collection_account_ref").GetString());

        // The loan installment endpoint derives its key server-side and is not step-up-gated — no SCA principal.
        Assert.False(sent.Headers.Contains(HttpLifecycleCommandSink.ServicePrincipalHeader));
    }

    [Fact]
    public async Task It_presents_the_scoped_sca_principal_on_a_money_mover_route()
    {
        var deposit = Guid.NewGuid();
        var commandId = LifecycleCommandKey.Derive(deposit, "mature_deposit", 1);
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var sink = new HttpLifecycleCommandSink(new HttpClient(handler) { BaseAddress = EngineBase });

        var decision = new LifecycleCommandDecision(
            InstanceId: deposit,
            CommandKind: "mature_deposit",
            OccurrenceKey: 1,
            RequestPath: $"/v1/deposits/{deposit:D}/maturity",
            Body: new Dictionary<string, object?>(),
            DueAt: new DateOnly(2026, 7, 1),
            ServicePrincipalScope: "lifecycle:deposit-money-mover");

        await sink.DispatchAsync(decision, commandId);

        // The non-interactive driver authorises the maturity money-mover by the scoped, gateway-attested
        // service principal (ADR-PC-036 §Decision 1) — it has no human to pass a step-up SCA challenge.
        Assert.Equal(
            "lifecycle:deposit-money-mover",
            Assert.Single(handler.Captured!.Headers.GetValues(HttpLifecycleCommandSink.ServicePrincipalHeader)));
    }

    [Fact]
    public async Task A_non_success_engine_response_is_backpressure_and_throws()
    {
        var loan = Guid.NewGuid();
        var handler = new CapturingHandler(HttpStatusCode.ServiceUnavailable);
        var sink = new HttpLifecycleCommandSink(new HttpClient(handler) { BaseAddress = EngineBase });

        var decision = new LifecycleCommandDecision(
            InstanceId: loan,
            CommandKind: PayInstallment,
            OccurrenceKey: 1,
            RequestPath: $"/v1/loans/{loan:D}/installment",
            Body: new Dictionary<string, object?> { ["collection_account_ref"] = "acct-ref-001" },
            DueAt: new DateOnly(2026, 9, 1));

        // A 5xx is backpressure: the sink throws so the pass leaves the occurrence un-recorded and the next
        // pass retries it (the engine deduping the re-POST).
        await Assert.ThrowsAsync<HttpRequestException>(
            () => sink.DispatchAsync(decision, LifecycleCommandKey.Derive(loan, PayInstallment, 1)));
    }

    /// <summary>An <see cref="HttpMessageHandler"/> that captures the single request it is sent (and its body)
    /// and returns a fixed status — the fake engine the sink POSTs to, no socket.</summary>
    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Captured { get; private set; }
        public string? CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Captured = request;
            CapturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status);
        }
    }
}
