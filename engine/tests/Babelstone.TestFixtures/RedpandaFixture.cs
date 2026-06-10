using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Babelstone.TestFixtures;

/// <summary>
/// A single-node Redpanda (dev-container) exposing the Kafka API and the built-in Confluent
/// Schema Registry, pinned to the SAME image as the dev stack (infra/compose.yaml). There is
/// no first-party Testcontainers.Redpanda, so this is a hand-rolled generic container — the
/// OpenBaoFixture shape (WithPortBinding(assignRandomHostPort) + a WaitStrategy).
/// </summary>
/// <remarks>
/// SHARED fixture (the single source of the Redpanda broker setup): the producer lane
/// (Babelstone.OutboxPublisher.Tests, Epic E.4/E.6) and the consumer lane
/// (Babelstone.InboxConsumer.Tests, Epic G.2) both reuse it — the inbox is the consumer mirror
/// of the outbox, so the broker setup must be IDENTICAL, and lives here rather than being
/// copy-pasted per lane. Redpanda advertises listeners to clients, so the broker must advertise
/// the host-mapped Kafka port — it is discovered after start and the broker is reconfigured in
/// place via a startup script that templates the mapped port into the advertised address.
/// </remarks>
public sealed class RedpandaFixture : IAsyncLifetime
{
    // The dev-stack image (infra/compose.yaml) — pinned, not :latest.
    private const string Image = "docker.redpanda.com/redpandadata/redpanda:v24.3.1";
    // Two Kafka listeners (mirroring the dev stack): `internal` for intra-container clients
    // (the built-in Schema Registry connects to the broker over this one), `external` for the
    // host. Only the external port is published to the host; the SR uses the internal listener
    // advertised as a container-local address it can actually reach.
    private const int KafkaInternalPort = 9092;
    private const int KafkaExternalPort = 29092;
    private const int SchemaRegistryPort = 8081;

    private IContainer _container = null!;

    public string BootstrapServers { get; private set; } = null!;

    public string SchemaRegistryUrl { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Start the broker WITHOUT the normal entrypoint, then exec `redpanda start` once we
        // know the host-mapped Kafka port (Redpanda must advertise an address the host client
        // can actually reach). A small wait loop on the script's marker file gates readiness.
        _container = new ContainerBuilder(Image)
            .WithExposedPort(KafkaExternalPort)
            .WithExposedPort(SchemaRegistryPort)
            .WithPortBinding(KafkaExternalPort, assignRandomHostPort: true)
            .WithPortBinding(SchemaRegistryPort, assignRandomHostPort: true)
            .WithEntrypoint("/bin/sh", "-c")
            // Block until the startup callback (below) has written the real start script — by
            // then we know the host-mapped external Kafka port Redpanda must advertise.
            .WithCommand("while [ ! -f /tmp/redpanda-start.sh ]; do sleep 0.1; done; exec /bin/sh /tmp/redpanda-start.sh")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Successfully started Redpanda!"))
            .WithStartupCallback(async (container, ct) =>
            {
                var externalHostPort = container.GetMappedPublicPort(KafkaExternalPort);
                // Two listeners (mirroring infra/compose.yaml):
                //   internal — advertised 127.0.0.1:9092, the address the in-container Schema
                //              Registry's own Kafka client connects to (must be container-local).
                //   external — advertised 127.0.0.1:<host-mapped>, the address the HOST test client
                //              reaches. Only this port is published to the host.
                // `--mode dev-container` is an `rpk redpanda start` flag — invoke rpk, NOT the raw
                // `redpanda` binary (which does not know --mode), mirroring the dev stack's
                // `command: [redpanda, start, ...]` (the image routes that through rpk).
                var startScript =
                    "#!/bin/sh\nexec rpk redpanda start --mode dev-container --smp 1 --default-log-level=info " +
                    $"--kafka-addr internal://0.0.0.0:{KafkaInternalPort},external://0.0.0.0:{KafkaExternalPort} " +
                    $"--advertise-kafka-addr internal://127.0.0.1:{KafkaInternalPort},external://127.0.0.1:{externalHostPort} " +
                    $"--schema-registry-addr 0.0.0.0:{SchemaRegistryPort}\n";
                var bytes = System.Text.Encoding.UTF8.GetBytes(startScript);
                await container.CopyAsync(bytes, "/tmp/redpanda-start.sh", ct: ct);
            })
            .Build();

        await _container.StartAsync();

        var kafkaPort = _container.GetMappedPublicPort(KafkaExternalPort);
        var srPort = _container.GetMappedPublicPort(SchemaRegistryPort);
        BootstrapServers = $"127.0.0.1:{kafkaPort}";
        SchemaRegistryUrl = $"http://127.0.0.1:{srPort}";

        // "Successfully started Redpanda!" fires before the cluster can serve the `_schemas`
        // topic the Schema Registry is backed by — registering too early returns
        // broker_not_available [8]. Gate on (1) cluster health (the same signal the dev-stack
        // healthcheck uses) and (2) the SR's /subjects endpoint returning OK.
        await WaitForClusterHealthAsync();
        await WaitForSchemaRegistryAsync();
    }

    private async Task WaitForClusterHealthAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var result = await _container.ExecAsync(["/bin/sh", "-c", "rpk cluster health"]);
            if (result.ExitCode == 0 && result.Stdout.Contains("Healthy:") && result.Stdout.Contains("true"))
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Redpanda cluster did not become healthy within 60s. Last rpk output:\n{result.Stdout}\n{result.Stderr}");
            }

            await Task.Delay(500);
        }
    }

    private async Task WaitForSchemaRegistryAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(SchemaRegistryUrl), Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(60);
        string lastSignal = "no attempt completed";
        while (true)
        {
            try
            {
                using var response = await http.GetAsync("subjects");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                lastSignal = $"HTTP {(int)response.StatusCode}";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastSignal = ex.GetType().Name + ": " + ex.Message;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Redpanda Schema Registry at {SchemaRegistryUrl} did not become ready within 60s. Last signal: {lastSignal}");
            }

            await Task.Delay(500);
        }
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}
