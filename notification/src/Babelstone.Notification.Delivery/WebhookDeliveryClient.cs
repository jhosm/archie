using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Babelstone.Notification.Delivery;

/// <summary>How one delivery attempt ended — the ADR-IC-011 §D4 status-handling taxonomy.</summary>
public enum WebhookDeliveryOutcome
{
    /// <summary>2xx — the receiver confirmed; the obligation is discharged.</summary>
    Delivered,

    /// <summary>5xx, timeout, connection failure, or 429 — the receiver is momentarily unable; retry per
    /// the §D4 backoff schedule (honouring <see cref="WebhookDeliveryResult.RetryAfter"/> when given).</summary>
    TransientFailure,

    /// <summary>A non-429 4xx — the endpoint is misconfigured; retrying cannot fix it (§D4). The record
    /// is abandoned immediately for human review.</summary>
    PermanentFailure,
}

/// <summary>The classified result of one attempt: the outcome, the HTTP status when one was received,
/// the receiver's <c>Retry-After</c> when it sent one (429, §D4), and a short diagnostic detail.</summary>
public sealed record WebhookDeliveryResult(
    WebhookDeliveryOutcome Outcome, int? StatusCode, TimeSpan? RetryAfter, string? Detail);

/// <summary>
/// The outbound HTTP leg — ONE signed POST per delivery attempt, per the ADR-IC-011 §P2 envelope. In
/// plain terms: this is the code that actually knocks on the communications-system consumer's door. It
/// serialises the delivery envelope (snake_case wire JSON, the repo's published-contract shape), signs the
/// exact bytes with HMAC-SHA256 over <c>"{timestamp}.{raw_body}"</c> (§D3), stamps the <c>X-Webhook-*</c>
/// headers, POSTs, and CLASSIFIES the response (§D4) — it never retries itself; retry timing belongs to
/// the outbox + pass, so a slow receiver ties up nothing but its own record.
/// </summary>
/// <remarks>
/// <para>
/// <b>The idempotency key is the composite <c>notification_id</c> (ADR-PC-025 slot 4 / bd
/// babelstone-60n8.4).</b> Every attempt of a delivery carries the SAME <c>idempotency_key</c> — the
/// receiver dedupes on it, which is what makes at-least-once safe. <c>delivery_attempt</c> increments and
/// <c>event_id</c> is fresh per attempt (§D2 — receiver logging, never dedupe).
/// </para>
/// <para>
/// <b>Redirects are not followed (ADR-IC-011 §P1).</b> The registered endpoint must be the final HTTPS
/// endpoint; the named <see cref="IHttpClientFactory"/> client is configured with auto-redirect off at
/// composition, and a 3xx classifies as a permanent misconfiguration here.
/// </para>
/// </remarks>
public sealed class WebhookDeliveryClient(
    IHttpClientFactory httpClientFactory,
    WebhookDeliveryOptions options,
    TimeProvider clock,
    ILogger<WebhookDeliveryClient>? logger = null)
{
    /// <summary>The named <see cref="IHttpClientFactory"/> client this delivery leg sends on — named (not
    /// typed) so this singleton never captures a pooled handler beyond its rotation window.</summary>
    public const string HttpClientName = "notification-webhook";

    /// <summary>Matches the repo's published wire contracts: snake_case property names, enums as their
    /// SCREAMING_SNAKE_CASE contract symbols (<c>SCHEDULED</c>…), dates as <c>yyyy-MM-dd</c>, money already
    /// integer-cent strings inside <c>data</c>.</summary>
    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) },
    };

    private readonly IHttpClientFactory _httpClientFactory =
        httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

    private readonly WebhookDeliveryOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Make ONE delivery attempt for <paramref name="record"/> and classify the outcome (§D4). Never
    /// throws for a delivery-shaped failure — connection refusal, DNS failure, and timeout classify as
    /// transient; only a caller cancellation propagates.
    /// </summary>
    /// <param name="record">The outbox record being attempted (its signal + enqueue instant ride the envelope).</param>
    /// <param name="attempt">This attempt's 1-based number (<c>record.Attempts + 1</c> at the call site).</param>
    /// <param name="rendered">The per-attempt rendered notice for an EVENT_DRIVEN delivery
    /// (bd babelstone-60n8.7) — carried on the envelope's <c>rendered</c> slot and DISCARDED with the
    /// request (its PII is transient by contract, ADR-PC-025 §PII); <see langword="null"/> for the
    /// SCHEDULED leg, whose rendering is the downstream consumer's (ADR-PC-025 Decision 2).</param>
    /// <param name="ct">Cancellation from the worker loop.</param>
    public async Task<WebhookDeliveryResult> DeliverAsync(
        DeliveryRecord record, int attempt, RenderedNotice? rendered = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        var signal = record.Signal;
        var envelope = new WebhookEnvelope(
            IdempotencyKey: signal.NotificationId,
            DeliveryAttempt: attempt,
            EventId: Guid.NewGuid(),
            OccurredAt: record.EnqueuedAt,
            Notification: new NotificationPayload(
                signal.NotificationId,
                signal.InstanceId,
                signal.CustomerRef,
                signal.TemplateRef,
                signal.TemplatePackVersion,
                signal.TriggerKind,
                signal.CausationId,
                signal.Data,
                signal.DueAt),
            Rendered: rendered is null
                ? null
                : new RenderedPayload(
                    rendered.TemplateRef, rendered.TemplatePackVersion, rendered.Fields, rendered.PiiResolved));

        // Serialise ONCE and sign the exact string sent — the §D3 signature covers the raw body byte for
        // byte, so the body must not be re-serialised after signing.
        var rawBody = JsonSerializer.Serialize(envelope, WireJson);
        var timestamp = _clock.GetUtcNow().ToUnixTimeSeconds();

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.EndpointUrl)
        {
            Content = new StringContent(rawBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(
            WebhookSignature.SignatureHeader, WebhookSignature.Compute(_options.Secret, timestamp, rawBody));
        request.Headers.TryAddWithoutValidation(
            WebhookSignature.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(WebhookSignature.DeliveryIdHeader, envelope.EventId.ToString("D"));
        if (_options.SubscriptionId is { Length: > 0 } subscriptionId)
        {
            request.Headers.TryAddWithoutValidation(WebhookSignature.SubscriptionIdHeader, subscriptionId);
        }

        try
        {
            using var response = await _httpClientFactory.CreateClient(HttpClientName).SendAsync(request, ct);
            return Classify(response);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // graceful shutdown — not a delivery failure
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Connection refused / DNS failure / client-side timeout — the receiver is unreachable, the
            // §D4 transient case. The pass schedules the backoff; nothing to unwind (post-flag).
            logger?.LogWarning(
                ex,
                "Webhook delivery attempt {Attempt} for notification {NotificationId} failed to reach the receiver.",
                attempt, signal.NotificationId);
            return new WebhookDeliveryResult(
                WebhookDeliveryOutcome.TransientFailure, StatusCode: null, RetryAfter: null, Detail: ex.Message);
        }
    }

    private WebhookDeliveryResult Classify(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
        {
            return new WebhookDeliveryResult(WebhookDeliveryOutcome.Delivered, status, null, null);
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // §D4: wait the receiver's Retry-After (when parseable), then resume the backoff schedule.
            var retryAfter = response.Headers.RetryAfter switch
            {
                { Delta: { } delta } => delta,
                { Date: { } date } => date - _clock.GetUtcNow(),
                _ => (TimeSpan?)null,
            };
            if (retryAfter is { Ticks: < 0 })
            {
                retryAfter = null; // a Retry-After date already in the past — fall back to the schedule
            }

            return new WebhookDeliveryResult(
                WebhookDeliveryOutcome.TransientFailure, status, retryAfter, "429 Too Many Requests");
        }

        // Non-429 4xx (and an unexpected 3xx — redirects are not followed, §P1): the endpoint is
        // misconfigured; §D4 abandons immediately rather than retrying what retrying cannot fix.
        if (status is >= 300 and < 500)
        {
            return new WebhookDeliveryResult(
                WebhookDeliveryOutcome.PermanentFailure, status, null, $"receiver answered {status}");
        }

        // 5xx — temporarily unavailable; retry per the schedule (§D4).
        return new WebhookDeliveryResult(
            WebhookDeliveryOutcome.TransientFailure, status, null, $"receiver answered {status}");
    }

    /// <summary>The ADR-IC-011 §P2 delivery envelope: the stable idempotency key, the per-attempt
    /// bookkeeping, the notification payload the consumer renders from, and — for an EVENT_DRIVEN
    /// delivery — the notice this estate already rendered (<c>rendered</c> is <c>null</c> on the
    /// SCHEDULED leg, where rendering is the consumer's).</summary>
    private sealed record WebhookEnvelope(
        Guid IdempotencyKey,
        int DeliveryAttempt,
        Guid EventId,
        DateTimeOffset OccurredAt,
        NotificationPayload Notification,
        RenderedPayload? Rendered);

    /// <summary>The rendered-notice slot (bd babelstone-60n8.7): the full interpolation field set — the
    /// structural values plus the render-time-resolved PII, which exists ONLY on this in-flight request
    /// (ADR-PC-025 §PII; the outbox record behind it stays structural).</summary>
    private sealed record RenderedPayload(
        string TemplateRef,
        string TemplatePackVersion,
        IReadOnlyDictionary<string, string> Fields,
        bool PiiResolved);

    /// <summary>The <c>NotificationDue</c> business payload on the wire (ADR-PC-025 Decision 1) — the same
    /// field set, under the same names, as the governed Avro schema
    /// (<c>contracts/avro/operations/NotificationDue.avsc</c>), in the snake_case JSON the repo's HTTP
    /// contracts use. <c>CustomerId</c> deliberately keeps the schema's <c>customer_id</c> spelling
    /// rather than the CLR signal's <c>CustomerRef</c>: the webhook and the bus name the SAME opaque
    /// recipient reference, and the governed schema wins on every wire. It is nullable here where the
    /// Avro field is not: the v1 SCHEDULED leg's signal carries no recipient reference (the ADR-PC-025
    /// named residual) — the webhook envelope surfaces that honestly instead of inventing one.</summary>
    private sealed record NotificationPayload(
        Guid NotificationId,
        Guid InstanceId,
        Guid? CustomerId,
        string TemplateRef,
        string TemplatePackVersion,
        NotificationTriggerKind TriggerKind,
        Guid? CausationId,
        IReadOnlyDictionary<string, string> Data,
        DateOnly DueAt);
}
