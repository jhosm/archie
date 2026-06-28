using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Babelstone.Lifecycle;

/// <summary>
/// The production <see cref="ILifecycleCommandSink"/> — it POSTs a due lifecycle command to the engine's
/// ADR-PC-029 command endpoint over the published HTTP surface (ADR-PC-036 §Decision 2). In plain terms: this
/// is the one and only path the driver takes to the engine. It builds the POST from the family rule's decision
/// (the endpoint path + the snake_case JSON body), presents the canonical server-derived idempotency key as the
/// <c>Idempotency-Key</c> header, presents the scoped SCA service principal on a money-mover route, and treats
/// any non-success engine response as backpressure (it throws, so the occurrence is NOT recorded dispatched and
/// the next pass retries it — the engine deduping the re-POST at <c>command_dedup</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reaches the engine ONLY over the command surface (ADR-PC-029), never the byte store.</b> The driver is
/// downstream and clockless-engine-honoring: it owns the clock and POSTs commands; the engine emits no
/// clock-driven signal (NO_CLOCK_DRIVEN_ENGINE_SIGNAL holds — the driver never makes the engine read a clock).
/// The engine base URL is a service ENDPOINT, not a credential, so the host wires it from configuration onto
/// this client's <c>BaseAddress</c> (the same posture as the notification host's read endpoint and the
/// orchestrator's). The relative <see cref="LifecycleCommandDecision.RequestPath"/> resolves against it.
/// </para>
/// <para>
/// <b>The idempotency key is the canonical, server-derived, number-pinned id (LCD-1, ADR-PC-036 §Decision 1+3).</b>
/// The sink presents <c>commandId</c> — the value derived the SAME way the engine derives it — as the
/// <c>Idempotency-Key</c> header. For the loan installment endpoint the engine ALSO derives the key server-side
/// (and ignores a caller key), so the two converge by construction; presenting it is belt-and-suspenders and
/// makes the maturity path (which dedupes on the threaded command id) uniform. The wire body is the engine
/// API's <c>JsonNamingPolicy.SnakeCaseLower</c> shape, money as integer cents, NO PII (ADR-PC-004 §P2).
/// </para>
/// <para>
/// <b>Scoped, non-interactive SCA principal on money-mover routes (ADR-PC-036 §Decision 1).</b> An automated
/// driver has no human behind it to pass a fresh step-up SCA challenge, so the deposit money-mover routes
/// authorise it by a SCOPED, gateway-attested service-principal token instead. When the decision carries a
/// <see cref="LifecycleCommandDecision.ServicePrincipalScope"/>, the sink presents it on the
/// <see cref="ServicePrincipalHeader"/> header. In production the gateway (Kong) OVERWRITES that header from the
/// validated token, so the driver's value is the in-cluster fast-path the engine attests; the loan installment
/// route needs no principal (its key is server-derived and it is not step-up-gated).
/// </para>
/// </remarks>
public sealed class HttpLifecycleCommandSink(HttpClient http, ILogger<HttpLifecycleCommandSink>? logger = null)
    : ILifecycleCommandSink
{
    /// <summary>The deterministic engine idempotency-key header (ADR-PC-029 slot 4) — the canonical
    /// server-derived, number-pinned command id rides here so a retry replays the original outcome at
    /// <c>command_dedup</c> rather than moving money twice.</summary>
    public const string IdempotencyHeader = "Idempotency-Key";

    /// <summary>The gateway-attested scoped SCA service-principal header (ADR-PC-036 §Decision 1) — kept in
    /// lock-step with the engine-side constant
    /// <c>Babelstone.Families.TermDeposit.Application.ScaServicePrincipal.PrincipalHeader</c>. Named locally
    /// here (not referenced) so the generic driver core stays free of any family-application dependency; the
    /// engine authorises the route from the gateway-attested value, never the caller's word.</summary>
    public const string ServicePrincipalHeader = "X-SCA-Service-Principal";

    /// <summary>Matches the engine API host's wire contract: snake_case property names
    /// (<c>JsonNamingPolicy.SnakeCaseLower</c>), money as integer cents.</summary>
    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <inheritdoc />
    public async Task DispatchAsync(
        LifecycleCommandDecision decision, Guid commandId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        using var request = new HttpRequestMessage(HttpMethod.Post, decision.RequestPath)
        {
            // The body is the engine API's snake_case wire shape; an empty body still posts {} so the endpoint
            // binds its optional fields from defaults.
            Content = JsonContent.Create(decision.Body, options: WireJson),
        };

        // The canonical server-derived idempotency key (LCD-1) — the SAME value the engine derives, so the
        // driver, a manual operator and the MCP agent converge on one dedupe receipt per occurrence.
        request.Headers.TryAddWithoutValidation(IdempotencyHeader, commandId.ToString("D"));

        // The scoped, non-interactive SCA principal on a money-mover route (ADR-PC-036 §Decision 1); absent for
        // routes that need none (e.g. the server-derived-key loan installment endpoint).
        if (!string.IsNullOrEmpty(decision.ServicePrincipalScope))
        {
            request.Headers.TryAddWithoutValidation(ServicePrincipalHeader, decision.ServicePrincipalScope);
        }

        using var response = await _http.SendAsync(request, ct);

        // A non-success engine response (4xx/5xx) is backpressure: throw so the occurrence is NOT recorded
        // dispatched and the next pass retries it (the engine deduping any re-POST). EnsureSuccessStatusCode
        // throws HttpRequestException, which the worker loop treats as a back-off-and-retry signal.
        if (!response.IsSuccessStatusCode)
        {
            logger?.LogWarning(
                "Lifecycle command POST {Path} for instance {InstanceId} occurrence {OccurrenceKey} returned "
                + "{Status}; treating as backpressure (the next pass retries — command_dedup makes it safe).",
                decision.RequestPath, decision.InstanceId, decision.OccurrenceKey, (int)response.StatusCode);
        }

        response.EnsureSuccessStatusCode();
    }
}
