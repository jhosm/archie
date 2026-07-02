using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Babelstone.Notification.Delivery;

/// <summary>
/// The delivery estate's one-call composition (bd babelstone-60n8.4/.7) — everything the notification
/// host needs to activate the outbound webhook leg: the options, the outbox, the SCHEDULED-leg sink the
/// core's schedule pass discovers through its optional <see cref="INotificationDeliverySink"/> port, the
/// signed HTTP client, and the drain worker. The host composition root adds ONE line —
/// <c>builder.Services.AddNotificationWebhookDelivery(builder.Configuration)</c> — and the whole leg
/// wires; with no <c>Notification:Webhook:EndpointUrl</c> configured the call is a no-op and the host
/// runs exactly its pre-delivery shape (the scheduler raising reminders that go nowhere).
/// </summary>
public static class NotificationDeliveryServiceCollectionExtensions
{
    /// <summary>The configuration section the delivery leg binds (<c>Notification:Webhook</c>).</summary>
    public const string ConfigSection = "Notification:Webhook";

    /// <summary>
    /// Register the outbound webhook delivery leg. Dormant (registers nothing) when
    /// <c>Notification:Webhook:EndpointUrl</c> is unset — delivery is post-flag and optional, so an
    /// unconfigured host must keep scheduling exactly as before, never fail its boot. Once an endpoint IS
    /// configured, a missing <c>Notification:Webhook:Secret</c> fails loud: an unsigned webhook is not a
    /// degraded mode this transport has (ADR-IC-011 §D3 — no-signature was evaluated and rejected).
    /// </summary>
    public static IServiceCollection AddNotificationWebhookDelivery(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var endpointUrl = configuration[$"{ConfigSection}:EndpointUrl"];
        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            return services; // dormant — see summary
        }

        RequireDeliverableEndpoint(endpointUrl);

        var secret = configuration[$"{ConfigSection}:Secret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                $"'{ConfigSection}:EndpointUrl' is configured but '{ConfigSection}:Secret' is not. Every "
                + "webhook delivery is HMAC-SHA256-signed (ADR-IC-011 §D3); set the shared secret via the "
                + "environment/secret store (never committed, never logged).");
        }

        var options = new WebhookDeliveryOptions
        {
            EndpointUrl = endpointUrl,
            Secret = secret,
            SubscriptionId = configuration[$"{ConfigSection}:SubscriptionId"],
            MaxAttempts = configuration.GetValue($"{ConfigSection}:MaxAttempts", 10),
            ClaimBatchSize = configuration.GetValue($"{ConfigSection}:ClaimBatchSize", 50),
            // The pack version stamped on SCHEDULED signals this estate produces: explicitly configured,
            // else the host's pinned pack (the same Engine:PackVersion default the host's pack loader uses).
            TemplatePackVersion =
                configuration[$"{ConfigSection}:TemplatePackVersion"]
                ?? configuration.GetValue("Engine:PackVersion", "pt.2026.1")!,
        };
        services.AddSingleton(options);

        // The drain worker's cadence — a DISTINCT options type so it never collides with the scheduler's
        // CadenceSchedulerOptions registration in the same container.
        var cadence = new WebhookDeliveryCadenceOptions();
        var pollSeconds = configuration.GetValue<double?>($"{ConfigSection}:PollIntervalSeconds");
        if (pollSeconds is > 0)
        {
            cadence = new WebhookDeliveryCadenceOptions { PollInterval = TimeSpan.FromSeconds(pollSeconds.Value) };
        }

        services.AddSingleton(cadence);

        // The clock (the host normally registers TimeProvider.System already; Try* keeps this composition
        // self-sufficient for tests and future hosts without double-registering).
        services.TryAddSingleton(TimeProvider.System);

        // The per-service outbox (ADR-IC-004) — in-memory v1, durable store behind the port later.
        services.TryAddSingleton<IDeliveryOutbox, InMemoryDeliveryOutbox>();

        // The SCHEDULED leg (bd babelstone-60n8.4): the core's NotificationSchedulePass discovers this
        // sink through its optional INotificationDeliverySink parameter and hands every newly-raised
        // reminder to the outbox.
        services.AddSingleton<INotificationDeliverySink, ScheduledReminderDeliverySink>();

        // The outbound HTTP leg: a NAMED factory client (never captured — the singleton delivery client
        // asks the factory per attempt, so handler rotation keeps working) with redirects OFF: the
        // registered endpoint must be the final HTTPS endpoint (ADR-IC-011 §P1 — no redirect following
        // at delivery time).
        services.AddHttpClient(WebhookDeliveryClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<WebhookDeliveryClient>();

        // The drain: one pass per tick on the shared cadence machinery (ADR-IC-019 mechanism reuse).
        services.AddSingleton<WebhookDeliveryPass>();
        services.AddHostedService<WebhookDeliveryWorker>();

        return services;
    }

    /// <summary>
    /// The ADR-IC-011 §P1 endpoint posture, applied at composition (the registration-time gate this
    /// statically-provisioned consumer gets instead of a subscription API): HTTPS only, with plain HTTP
    /// tolerated for LOOPBACK hosts only so the local dev stack can run a stub receiver.
    /// </summary>
    private static void RequireDeliverableEndpoint(string endpointUrl)
    {
        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                $"'{ConfigSection}:EndpointUrl' is not an absolute URL: '{endpointUrl}'.");
        }

        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isLoopbackHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && uri.IsLoopback;
        if (!isHttps && !isLoopbackHttp)
        {
            throw new InvalidOperationException(
                $"'{ConfigSection}:EndpointUrl' must be HTTPS (ADR-IC-011 §P1 — plain HTTP is accepted for "
                + $"loopback dev endpoints only): '{endpointUrl}'.");
        }
    }
}
