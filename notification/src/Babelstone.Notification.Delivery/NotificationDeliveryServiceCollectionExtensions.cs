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

        // The PII fields the EVENT_DRIVEN renderer resolves (ADR-PC-025 §PII), configurable as an array
        // under Notification:Webhook:PiiFields; the name/NIF default matches the ADR-PC-025 example set.
        var piiFields = configuration.GetSection($"{ConfigSection}:PiiFields").GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();

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
            PiiFields = piiFields.Length > 0 ? piiFields : ["name", "nif"],
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

        // The per-service outbox (ADR-IC-004): the durable PostgreSQL store when a connection string
        // is configured (the production posture, ADR-IC-011), else the in-memory double (a host with
        // no delivery database keeps the in-process shape).
        var deliveryConnectionString =
            configuration["Notification:Delivery:ConnectionString"]
            ?? configuration.GetConnectionString("NotificationDelivery")
            ?? Environment.GetEnvironmentVariable("BABELSTONE_NOTIFICATION_DELIVERY_CONNECTION");
        if (!string.IsNullOrWhiteSpace(deliveryConnectionString))
        {
            AddDurableDeliveryStore(services, configuration, deliveryConnectionString);
        }
        else
        {
            services.TryAddSingleton<IDeliveryOutbox, InMemoryDeliveryOutbox>();
        }

        // The SCHEDULED leg (bd babelstone-60n8.4): the core's NotificationSchedulePass discovers this
        // sink through its optional INotificationDeliverySink parameter and hands every newly-raised
        // reminder to the outbox.
        services.AddSingleton<INotificationDeliverySink, ScheduledReminderDeliverySink>();

        // The EVENT_DRIVEN ingress seam (bd babelstone-60n8.7): dormant Null source until the engine-side
        // EVENT_DRIVEN emission + its bus consumer land — TryAdd, so composing the real consumer later
        // replaces it with no other change (see INotificationDueSource remarks).
        services.TryAddSingleton<INotificationDueSource, NullNotificationDueSource>();

        // Render-time PII resolution (ADR-PC-025 §PII): the renderer asks the ENGINE by reference, per
        // attempt, over the published resolve surface — a NAMED factory client on the engine API endpoint
        // (a service ENDPOINT, not a credential; the same posture and fallback chain as the host's read
        // client). PII rides one POST transiently and is never persisted.
        var engineBaseUrl =
            configuration["Engine:BaseUrl"]
            ?? configuration.GetConnectionString("Engine")
            ?? Environment.GetEnvironmentVariable("BABELSTONE_ENGINE_BASE_URL")
            ?? throw new InvalidOperationException(
                "Webhook delivery is configured but no engine API base URL is. Set Engine:BaseUrl, "
                + "ConnectionStrings:Engine, or BABELSTONE_ENGINE_BASE_URL — the EVENT_DRIVEN renderer "
                + "resolves PII at render time over the engine's resolve surface (ADR-PC-025 §PII).");
        services.AddHttpClient(EnginePiiResolveClient.HttpClientName, http =>
            http.BaseAddress = new Uri(engineBaseUrl.EndsWith('/') ? engineBaseUrl : engineBaseUrl + "/"));
        services.AddSingleton<IPiiResolveClient, EnginePiiResolveClient>();
        services.AddSingleton<INoticeRenderer, PiiResolvingNoticeRenderer>();

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
    /// The durable-delivery composition: the PostgreSQL <see cref="IDeliveryOutbox"/> + its boot-time
    /// migration service + the exhausted-backlog-age gauge, and — when the backbone is configured
    /// (<c>Kafka:BootstrapServers</c>, the same key the engine host reads) — the exhaustion relay that
    /// publishes <c>NotificationDeliveryExhausted</c> (ADR-IC-011). With no bootstrap servers
    /// configured the store still runs durable and dead-letters still write exhausted-outbox rows; the
    /// announcement drains once a broker is configured — rows are never lost, only waiting
    /// (ADR-IC-004 backpressure posture), a mode the boot WARN below and the climbing lag gauge keep
    /// from being silent.
    /// </summary>
    private static void AddDurableDeliveryStore(
        IServiceCollection services, IConfiguration configuration, string connectionString)
    {
        // The two-connection split (the ADR-PC-001 role discipline; the lifecycle/orchestrator shape):
        // DDL runs as the migration role; the runtime role holds only the enqueue/claim/flip envelope.
        // In dev the migration string falls back to the runtime one.
        var migrationConnectionString =
            configuration["Notification:Delivery:MigrationConnectionString"]
            ?? configuration.GetConnectionString("NotificationDeliveryMigration")
            ?? connectionString;

        var store = new PostgresDeliveryOutbox(connectionString);
        services.AddSingleton<IDeliveryOutbox>(store);
        services.AddSingleton<IExhaustedDeliveryOutbox>(store);

        // The exhausted-backlog-age gauge rides with the STORE, not the relay: it must keep climbing
        // (and alerting) precisely when no relay is draining — wedged, crashed, or never configured.
        // Container-owned singleton, so disposal removes the gauge with the host.
        services.AddSingleton(_ => new ExhaustedPendingLagObserver(connectionString));

        // Registered BEFORE the delivery/relay workers below: with the host's default sequential
        // hosted-service startup, registration order is start order and this service's StartAsync
        // AWAITS the runner, so those workers only start against a migrated store. (A host that opts
        // into concurrent startup, or a hosted service registered before this call — the scheduler's
        // NotificationWorker typically is — can race a first-boot migration; that is a transient
        // failure the cadence loop's backoff absorbs.)
        services.AddHostedService(provider => new DeliveryStoreMigrationService(
            migrationConnectionString,
            provider.GetService<Microsoft.Extensions.Logging.ILogger<DeliveryStoreMigrationService>>()));

        var bootstrapServers = configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(bootstrapServers))
        {
            // Durable store without a backbone: dead-letters are recorded but their announcements
            // cannot drain. A legitimate dev posture and a silent-stall production misconfiguration —
            // so say it ONCE, loudly, at boot (the lag gauge is the ongoing alarm).
            services.AddHostedService(provider => new StartupWarningService(
                provider.GetService<Microsoft.Extensions.Logging.ILogger<StartupWarningService>>(),
                "The durable notification delivery store is configured but no Kafka:BootstrapServers "
                + "is — NotificationDeliveryExhausted announcements will accumulate PENDING in "
                + "notification_delivery_exhausted and NOT drain to the backbone (ADR-IC-011) until "
                + "a broker is configured. Watch notification_delivery_exhausted_pending_lag_seconds."));
            return;
        }

        // The same SR keys the engine host's bus codec reads (Bus:SchemaRegistryUrl defaulting to the
        // infra/compose.yaml external listener; Bus:RegisterSchemas — register-if-absent — is the
        // ADR-IC-002 walking-skeleton convenience, flipped off where CI owns registration).
        var schemaRegistryUrl = configuration["Bus:SchemaRegistryUrl"] ?? "http://localhost:18081";
        var registerSchemas = configuration.GetValue("Bus:RegisterSchemas", true);

        services.TryAddSingleton<IExhaustedEventPublisher>(_ =>
            new KafkaExhaustedEventPublisher(bootstrapServers, schemaRegistryUrl, registerSchemas));

        var relayCadence = new ExhaustedRelayCadenceOptions();
        var relayPollSeconds = configuration.GetValue<double?>($"{ConfigSection}:ExhaustedRelayPollIntervalSeconds");
        if (relayPollSeconds is > 0)
        {
            relayCadence = new ExhaustedRelayCadenceOptions
            {
                PollInterval = TimeSpan.FromSeconds(relayPollSeconds.Value),
            };
        }

        services.AddSingleton(relayCadence);
        services.AddSingleton<ExhaustedEventRelayPass>();
        services.AddHostedService<ExhaustedEventRelayWorker>();
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
