using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Babelstone.Notification.Delivery;

/// <summary>
/// The production <see cref="IPiiResolveClient"/> — <c>GET /v1/pii/resolve?subject=…&amp;fields=…</c>
/// against the engine-owned authorised PII-resolve surface (ADR-PC-025 §PII), the one and only path this
/// estate takes to a customer's PII. The engine decrypts internally via OpenBao transit (ADR-PC-004 §P2 —
/// this client holds no key material and no crypto knowledge) and returns plaintext by field name, with
/// <see langword="null"/>/absent fields for a crypto-shredded subject (§P3).
/// </summary>
/// <remarks>
/// <b>Tolerant of the surface not existing yet.</b> The resolve endpoint is itself an unbuilt ADR-PC-025
/// residual (named on bd babelstone-60n8.7); an engine answering 404 for the ROUTE is indistinguishable
/// from one answering 404 for the SUBJECT, and both resolve to the same safe outcome — an empty map, a
/// notice rendered without PII (the same rendering a shredded subject gets). A 5xx or transport failure
/// throws instead: the surface exists but is down, which is retryable backpressure (ADR-PC-025 residual —
/// the render is retried later on the delivery backoff).
/// </remarks>
public sealed class EnginePiiResolveClient(
    IHttpClientFactory httpClientFactory,
    ILogger<EnginePiiResolveClient>? logger = null) : IPiiResolveClient
{
    /// <summary>The named <see cref="IHttpClientFactory"/> client this resolver calls the engine on —
    /// its <c>BaseAddress</c> is the engine API endpoint, wired at composition (a service ENDPOINT, not a
    /// credential — the same posture as the deposit read client's).</summary>
    public const string HttpClientName = "engine-pii-resolve";

    /// <summary>Matches the engine API host's wire contract: snake_case, case-insensitive bind.</summary>
    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory =
        httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        Guid subjectRef, IReadOnlyList<string> fields, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var path = $"v1/pii/resolve?subject={subjectRef:D}&fields={Uri.EscapeDataString(string.Join(',', fields))}";
        using var response = await _httpClientFactory.CreateClient(HttpClientName).GetAsync(path, ct);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.NoContent)
        {
            // Unknown subject, crypto-shredded subject, or a surface not deployed yet — all the same safe
            // outcome: no PII, render structurally (ADR-PC-004 §P3; see class remarks).
            logger?.LogInformation(
                "PII resolve returned {StatusCode} for subject reference {SubjectRef}; rendering without PII.",
                (int)response.StatusCode, subjectRef);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        response.EnsureSuccessStatusCode(); // 5xx → retryable backpressure for the delivery pass

        // The engine answers a flat field→value map; a shredded/absent field arrives null and is dropped
        // (never rendered as the string "null").
        var resolved = await response.Content.ReadFromJsonAsync<Dictionary<string, string?>>(WireJson, ct);
        var pii = new Dictionary<string, string>(StringComparer.Ordinal);
        if (resolved is not null)
        {
            foreach (var (field, value) in resolved)
            {
                if (value is not null)
                {
                    pii[field] = value;
                }
            }
        }

        return pii;
    }
}
