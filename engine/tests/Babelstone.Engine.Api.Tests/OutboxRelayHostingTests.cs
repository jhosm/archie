using Babelstone.EventStore.Migrations;
using Babelstone.OutboxPublisher;
using Babelstone.TestFixtures;
using Confluent.Kafka;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Engine.Api.Tests;

/// <summary>
/// G.1 host-wiring (bd babelstone-s5gd): the production engine host (<see cref="Program"/>) must
/// actually RUN the IC-004 outbox relay so the §P4 publish-lag SLI is emitted — registering
/// <see cref="OutboxRelayService"/> as a hosted service and the <see cref="OutboxLagObserver"/>
/// singleton on the already-wired meter (.WithMetrics → AddMeter). Before this, the host had
/// .WithMetrics but ran nothing, so the gauge emitted nothing and the 30s-warn/5min-crit alerts
/// could never fire.
///
/// These tests assert the WIRING, not the Redpanda round-trip (that is the E.4/E.6
/// OutboxToRedpandaIntegrationTests lane against a real broker): the host BUILDS and STARTS with
/// the relay registered even when Redpanda is unreachable — Redpanda unavailability is backpressure
/// (rows stay PENDING, the loop backs off — ADR-IC-004 §P7), never a startup failure. Tagged
/// Integration because the host's composition root resolves the real PostgreSQL connection string,
/// so it runs in the Testcontainers lane (E.6), not the default unit lane.
/// </summary>
[Trait("Category", "Integration")]
[Collection(EngineApiHostCollection.Name)]
public sealed class OutboxRelayHostingTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async Task InitializeAsync()
    {
        await _pg.GatedStartAsync();
        await new MigrationRunner(_pg.GetConnectionString()).ApplyAsync();

        Environment.SetEnvironmentVariable("ConnectionStrings__Engine", _pg.GetConnectionString());
        Environment.SetEnvironmentVariable("Engine__PacksDir", PacksDir());
        // The host fails fast without an explicit deployment.environment (BabelstoneResource).
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        // Point the relay at a broker that is NOT listening: starting the host must NOT depend on
        // Redpanda being reachable — the relay co-hosts and treats unavailability as backpressure.
        Environment.SetEnvironmentVariable("Kafka__BootstrapServers", "127.0.0.1:1");
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Engine", null);
        Environment.SetEnvironmentVariable("Engine__PacksDir", null);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", null);
        Environment.SetEnvironmentVariable("Kafka__BootstrapServers", null);
        await _pg.DisposeAsync();
    }

    [Fact]
    public async Task Host_starts_with_the_outbox_relay_and_lag_observer_registered()
    {
        await using var factory = new WebApplicationFactory<Program>();

        // CreateClient() forces the full host (including the IHostedServices) to build and start.
        using var client = factory.CreateClient();

        // The §P4 SLI emitter (the oldest-PENDING-row gauge) is a resolvable singleton — its
        // construction is what registers the observable gauge on BabelstoneTelemetry.MeterName.
        var observer = factory.Services.GetService<OutboxLagObserver>();
        Assert.NotNull(observer);

        // The relay's poll loop is registered as a hosted service (alongside the projection relay),
        // so the host actually drains the outbox in-process rather than leaving it standing.
        var hostedServices = factory.Services.GetServices<IHostedService>();
        Assert.Contains(hostedServices, service => service is OutboxRelayService);

        // The drainer + options the relay depends on resolve from the same container.
        Assert.NotNull(factory.Services.GetService<OutboxDrainer>());
        var options = factory.Services.GetService<OutboxRelayOptions>();
        Assert.NotNull(options);
        Assert.Equal(_pg.GetConnectionString(), options.ConnectionString);
        Assert.Equal("127.0.0.1:1", options.BootstrapServers);
    }

    [Fact]
    public async Task Relay_options_default_to_SASL_off_when_no_identity_is_configured()
    {
        // The OFF-when-unconfigured posture (KAFKA_SASL_TOPIC_ACL / ADR-IC-016 §6): InitializeAsync
        // sets no Kafka:Sasl:OutboxPublisher:Username, so the host resolves an empty credential, SASL
        // is left OFF, and ApplyTo is a no-op — the plaintext loopback Redpanda (`make up`) posture.
        // The secret provider is never consulted, so the host boots with no SASL secret present.
        await using var factory = new WebApplicationFactory<Program>();
        var options = factory.Services.GetRequiredService<OutboxRelayOptions>();

        Assert.False(options.Sasl.IsConfigured);
        Assert.Null(options.Sasl.Username);
        Assert.Null(options.Sasl.Password);
    }

    [Fact]
    public async Task Relay_options_carry_the_resolved_SCRAM_identity_when_configured()
    {
        // The host-side wiring njt2.1 deferred (bd babelstone-njt2.5): a configured per-service username
        // turns SASL on, and the PASSWORD resolves at the composition root through the same
        // ISecretProvider seam as ConnectionStrings:Engine — the configuration-backed provider reads
        // ConnectionStrings:<SecretName>. So the relay options carry the distinct svc-outbox-publisher
        // SCRAM identity (SASL_SSL + SCRAM-SHA-256 defaults), which is what authenticates the producer
        // to Redpanda and lets the broker ACLs contain a compromise to that identity's grants.
        Environment.SetEnvironmentVariable("Kafka__Sasl__OutboxPublisher__Username", "svc-outbox-publisher");
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__OutboxPublisherSaslPassword", "resolved-from-isecretprovider");
        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            var options = factory.Services.GetRequiredService<OutboxRelayOptions>();

            Assert.True(options.Sasl.IsConfigured);
            Assert.Equal("svc-outbox-publisher", options.Sasl.Username);
            Assert.Equal("resolved-from-isecretprovider", options.Sasl.Password);
            // Secure defaults: SCRAM over TLS, never cleartext PLAIN.
            Assert.Equal(SaslMechanism.ScramSha256, options.Sasl.Mechanism);
            Assert.Equal(SecurityProtocol.SaslSsl, options.Sasl.SecurityProtocol);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Kafka__Sasl__OutboxPublisher__Username", null);
            Environment.SetEnvironmentVariable("ConnectionStrings__OutboxPublisherSaslPassword", null);
        }
    }

    [Fact]
    public async Task Bootstrap_servers_default_to_the_dev_redpanda_listener_when_unconfigured()
    {
        // With no Kafka__BootstrapServers set, the host falls back to the infra/compose.yaml external
        // Redpanda listener (localhost:19092) — the same convention `make up` exposes — so a dev host
        // boots without extra config. Override the per-test value InitializeAsync sets, only for this
        // factory, then restore it so the process-global env var cannot leak into another test.
        var previous = Environment.GetEnvironmentVariable("Kafka__BootstrapServers");
        Environment.SetEnvironmentVariable("Kafka__BootstrapServers", null);
        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            var options = factory.Services.GetRequiredService<OutboxRelayOptions>();

            Assert.Equal("localhost:19092", options.BootstrapServers);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Kafka__BootstrapServers", previous);
        }
    }

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
