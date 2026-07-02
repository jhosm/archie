using System.Net;
using Babelstone.Notification.Delivery;
using Microsoft.Extensions.Logging.Abstractions;

namespace Babelstone.Notification.Delivery.Tests;

/// <summary>The shared test doubles for the delivery-leg tests: a settable clock (no real waits), a
/// recording <see cref="HttpMessageHandler"/> (no receiver, no network), and a factory over it — the same
/// fake-at-the-HTTP-seam technique the deposit read client and lifecycle sink tests use.</summary>
internal static class DeliveryTestSupport
{
    public const string Endpoint = "https://comms.example.test/webhooks/notifications";
    public const string Secret = "whsec_test_secret";

    public static WebhookDeliveryOptions Options(int maxAttempts = 10, string? subscriptionId = "sub-comms-01") => new()
    {
        EndpointUrl = Endpoint,
        Secret = Secret,
        SubscriptionId = subscriptionId,
        MaxAttempts = maxAttempts,
        TemplatePackVersion = "pt.2026.1",
    };

    /// <summary>A drain pass over the fakes, with jitter pinned to 0 so backoff times are exact. The
    /// EVENT_DRIVEN seams default inert: a Null bus source and a PII resolver that resolves nothing.</summary>
    public static WebhookDeliveryPass Pass(
        IDeliveryOutbox outbox,
        FakeHandler handler,
        MutableClock clock,
        WebhookDeliveryOptions? options = null,
        INotificationDueSource? source = null,
        IPiiResolveClient? piiResolveClient = null)
    {
        options ??= Options();
        var client = new WebhookDeliveryClient(
            new FakeHttpClientFactory(handler), options, clock, NullLogger<WebhookDeliveryClient>.Instance);
        var renderer = new PiiResolvingNoticeRenderer(
            piiResolveClient ?? new FakePiiResolveClient(), options, NullLogger<PiiResolvingNoticeRenderer>.Instance);
        return new WebhookDeliveryPass(
            outbox,
            client,
            source ?? new NullNotificationDueSource(),
            renderer,
            options,
            clock,
            NullLogger<WebhookDeliveryPass>.Instance,
            jitter: () => 0.0);
    }

    public static NotificationDueSignal Signal(
        NotificationTriggerKind triggerKind = NotificationTriggerKind.Scheduled,
        Guid? customerRef = null,
        Guid? causationId = null) => new(
        NotificationId: Guid.NewGuid(),
        InstanceId: Guid.NewGuid(),
        CustomerRef: customerRef,
        TemplateRef: "pt.test.notice",
        TemplatePackVersion: "pt.2026.1",
        TriggerKind: triggerKind,
        CausationId: causationId,
        Data: new Dictionary<string, string>
        {
            ["total_payout_cents"] = "1012345",
            ["occurrence_date"] = "2026-07-16",
        },
        DueAt: new DateOnly(2026, 7, 2));

    /// <summary>A settable <see cref="TimeProvider"/> — the tests advance it instead of sleeping (the
    /// hand-rolled shape the cadence tests use; no FakeTimeProvider package).</summary>
    public sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan by) => Now += by;
    }

    /// <summary>Records every request — method, headers, and the RAW body string (read before the
    /// request is disposed, since the §D3 signature is over those exact bytes) — and answers from a
    /// caller-supplied script.</summary>
    public sealed class FakeHandler(Func<int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(
                h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri, headers, body));
            return responder(Requests.Count);
        }

        public static HttpResponseMessage Status(HttpStatusCode code) => new(code);
    }

    public sealed record CapturedRequest(
        HttpMethod Method, Uri? Uri, IReadOnlyDictionary<string, string> Headers, string Body);

    public sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler, disposeHandler: false);

        public HttpClient CreateClient(string name) => _client;
    }

    /// <summary>An in-memory <see cref="INotificationDueSource"/>: hands out each queued batch once —
    /// the consumed-bus stand-in for the EVENT_DRIVEN ingress tests.</summary>
    public sealed class FakeNotificationDueSource : INotificationDueSource
    {
        private readonly Queue<IReadOnlyList<NotificationDueSignal>> _batches = new();

        public Func<IReadOnlyList<NotificationDueSignal>>? OnPoll { get; set; }

        public void QueueBatch(params NotificationDueSignal[] signals) => _batches.Enqueue(signals);

        public Task<IReadOnlyList<NotificationDueSignal>> PollAsync(CancellationToken ct = default)
        {
            if (OnPoll is not null)
            {
                return Task.FromResult(OnPoll());
            }

            return Task.FromResult(_batches.TryDequeue(out var batch)
                ? batch
                : (IReadOnlyList<NotificationDueSignal>)[]);
        }
    }

    /// <summary>A scripted <see cref="IPiiResolveClient"/>: answers a fixed field map (empty by default —
    /// the shredded/no-surface outcome), records every resolve, and can be told to throw (the
    /// resolve-surface-down transient case).</summary>
    public sealed class FakePiiResolveClient : IPiiResolveClient
    {
        public Dictionary<string, string> Pii { get; } = new(StringComparer.Ordinal);

        public List<(Guid SubjectRef, IReadOnlyList<string> Fields)> Resolves { get; } = [];

        public Exception? ThrowOnResolve { get; set; }

        public Task<IReadOnlyDictionary<string, string>> ResolveAsync(
            Guid subjectRef, IReadOnlyList<string> fields, CancellationToken ct = default)
        {
            if (ThrowOnResolve is not null)
            {
                throw ThrowOnResolve;
            }

            Resolves.Add((subjectRef, fields));
            IReadOnlyDictionary<string, string> resolved =
                Pii.Where(pair => fields.Contains(pair.Key, StringComparer.Ordinal))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            return Task.FromResult(resolved);
        }
    }
}
